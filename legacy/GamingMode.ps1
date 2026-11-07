#Requires -Version 5.1
<#
  Gaming Mode - one-click toggle with a tray icon.

  ON : every app NOT in protected-apps.txt is pushed to the back (lowest CPU priority +
       Windows "Efficiency mode") and its unused RAM is handed back. Indexing / telemetry
       services are paused, Windows Update is paused, apps in upload-limits.txt (cloud sync,
       browsers) get their upload speed capped so your stream keeps the bandwidth, Windows
       Automatic Maintenance is held off, Defender's scheduled scans are limited to 5% CPU,
       Game Bar background recording is turned off while OBS & co. are open, Windows
       Game Mode is enabled, and the power plan goes to High performance if you were on
       Balanced / Power saver.
       While ON, a green "G" sits in the tray and keeps watching: apps you open later get
       pushed back too. Whatever window you're actively using always runs at full speed,
       and anything you run fullscreen is treated as a game and left alone.
  OFF: click again (or tray icon -> Turn off). Everything is put back exactly how it was, and
       a notification tells you how much CPU the pushed-back apps used during the session.

  Nothing is ever closed or killed.

  Switches:  -Toggle     what the shortcut runs
             -Install    one-time setup (admin): no more admin prompt on every click
             -Uninstall  remove that setup
             -DryRun     show what would be slowed down, change nothing
#>
param([switch]$Toggle, [switch]$DryRun, [switch]$Install, [switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$TaskName     = 'AceBot Gaming Mode'
$StateDir     = Join-Path $env:LOCALAPPDATA 'GamingMode'
$StateFile    = Join-Path $StateDir 'state.json'
$LogFile      = Join-Path $StateDir 'log.txt'
$ConfigFile   = Join-Path $PSScriptRoot 'protected-apps.txt'
$UploadFile   = Join-Path $PSScriptRoot 'upload-limits.txt'
$WuKey        = 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
$WuNames      = 'PauseUpdatesStartTime', 'PauseUpdatesExpiryTime', 'PauseFeatureUpdatesStartTime',
                'PauseFeatureUpdatesEndTime', 'PauseQualityUpdatesStartTime', 'PauseQualityUpdatesEndTime'
$WuPauseDays  = 2      # safety net: the pause expires by itself even if Gaming Mode never gets turned off
$MaintKey     = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance'
$DefenderScanCpu = 5   # % CPU Defender's scheduled scans may use while gaming (Windows default is 50)
$DvrKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR'
$StreamApps   = 'obs64', 'obs32', 'obs', 'Streamlabs*', 'xsplit*', 'prismlivestudio', 'twitchstudio*', 'meld*'
$OffEventName = 'Local\AceBotGamingMode.TurnOff'
$ScanMs       = 3000   # how often to look for newly opened apps
$GraceSeconds = 10     # new apps get this long to grab focus (e.g. a game loading) before being pushed back

$PausableServices = @('WSearch', 'SysMain', 'DiagTrack')
$LowPowerPlans    = @('381b4222-f694-41f0-9685-ff5bb260df2e', 'a1841308-3541-4fab-bc81-f71556f20b4a') # Balanced, Power saver
$HighPerfPlan     = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c'
$GameBarKey       = 'HKCU:\Software\Microsoft\GameBar'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GmNative {
    [StructLayout(LayoutKind.Sequential)] struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(IntPtr h, int cls, ref PowerThrottlingState info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool K32EmptyWorkingSet(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    // Efficiency mode (EcoQoS). on=false hands control back to Windows.
    public static bool SetEfficiency(int pid, bool on) {
        IntPtr h = OpenProcess(0x0200 /*SET_INFORMATION*/, false, pid);
        if (h == IntPtr.Zero) return false;
        try {
            var s = new PowerThrottlingState { Version = 1, ControlMask = on ? 1u : 0u, StateMask = on ? 1u : 0u };
            return SetProcessInformation(h, 4 /*ProcessPowerThrottling*/, ref s, Marshal.SizeOf(s));
        } finally { CloseHandle(h); }
    }
    // Hand the app's idle RAM back to Windows. It pages back in on demand if the app needs it.
    public static bool TrimMemory(int pid) {
        IntPtr h = OpenProcess(0x1100 /*SET_QUOTA|QUERY_LIMITED*/, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return K32EmptyWorkingSet(h); } finally { CloseHandle(h); }
    }
    public static int ForegroundPid() {
        IntPtr w = GetForegroundWindow();
        if (w == IntPtr.Zero) return 0;
        uint pid; GetWindowThreadProcessId(w, out pid); return (int)pid;
    }
    public static bool ForegroundIsFullscreen() {
        IntPtr w = GetForegroundWindow(); RECT r;
        if (w == IntPtr.Zero || !GetWindowRect(w, out r)) return false;
        var b = System.Windows.Forms.Screen.FromHandle(w).Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }
}
'@

# ---------------------------------------------------------------- helpers
function Write-Log($msg) {
    try {
        New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
        if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 1MB) { Remove-Item $LogFile -Force }
        Add-Content -Path $LogFile -Value ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $msg)
    } catch {}
}

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-Protection {
    if (-not (Test-Path $ConfigFile)) { throw "Can't find $ConfigFile - keep it next to GamingMode.ps1." }
    $names = @(); $paths = @()
    foreach ($raw in Get-Content $ConfigFile) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        if ($line -like 'path:*') { $paths += $line.Substring(5).Trim() } else { $names += $line }
    }
    [pscustomobject]@{ Names = $names; Paths = $paths }
}

function Test-Protected($name, $path) {
    foreach ($n in $script:prot.Names) { if ($name -like $n) { return $true } }
    if ($path) { foreach ($p in $script:prot.Paths) { if ($path.IndexOf($p, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true } } }
    $false
}

function Get-ActivePlan {
    $out = powercfg /getactivescheme
    if ($out -match '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') { $Matches[1] } else { $null }
}

function Format-GB($bytes) { '{0:0.0} GB' -f ($bytes / 1GB) }

# ---------------------------------------------------------------- per-app tracking
# $script:procs : PID -> entry. Kind = windows | protected | locked | target
function Get-Entry($p) {
    $e = $script:procs[$p.Id]
    if ($e -and $e.Name -eq $p.ProcessName) { return $e }
    $e = [pscustomobject]@{ Id = $p.Id; Name = $p.ProcessName; Kind = 'windows'; Start = $null; Orig = $null
                            Throttled = $false; Touched = $false; Trimmed = $false; Exempt = $false; FirstSeen = [DateTime]::UtcNow
                            CpuBase = $null; CpuLast = $null }
    $script:procs[$p.Id] = $e
    $path = $null; try { $path = $p.Path } catch {}
    if (-not $path -or $path.StartsWith($script:windir, [StringComparison]::OrdinalIgnoreCase)) { return $e }  # Windows itself
    if (Test-Protected $p.ProcessName $path) { $e.Kind = 'protected'; return $e }
    try {
        $e.Start = "$($p.StartTime.ToUniversalTime().Ticks)"
        $e.Orig  = "$($p.PriorityClass)"
        $e.Kind  = 'target'
    } catch { $e.Kind = 'locked' }   # can't read it, so we couldn't restore it either - leave it alone
    $e
}

function Set-Throttle($p, $e, [bool]$on) {
    try {
        if ($on) {
            $p.PriorityClass = [Diagnostics.ProcessPriorityClass]::Idle
            [void][GmNative]::SetEfficiency($p.Id, $true)
            $e.Throttled = $true; $e.Touched = $true
            if ($null -eq $e.CpuBase) { try { $e.CpuBase = $p.TotalProcessorTime.TotalMilliseconds; $e.CpuLast = $e.CpuBase } catch {} }
            if (-not $e.Trimmed) {
                $before = $p.WorkingSet64
                [void][GmNative]::TrimMemory($p.Id)
                $p.Refresh()
                $script:freed += [math]::Max(0, $before - $p.WorkingSet64)
                $e.Trimmed = $true
            }
        } else {
            [void][GmNative]::SetEfficiency($p.Id, $false)
            if ($p.PriorityClass -eq [Diagnostics.ProcessPriorityClass]::Idle) { $p.PriorityClass = [Diagnostics.ProcessPriorityClass]$e.Orig }
            $e.Throttled = $false
        }
    } catch { $e.Kind = 'locked' }
}

function Invoke-Scan([bool]$initial) {
    $fg = [GmNative]::ForegroundPid()
    $alive = @{}; $dirty = $false
    foreach ($p in [Diagnostics.Process]::GetProcesses()) {
        if ($p.SessionId -ne $script:session -or $p.Id -eq $PID) { continue }
        $alive[$p.Id] = $true
        $e = Get-Entry $p
        if ($null -ne $e.CpuBase) { try { $e.CpuLast = $p.TotalProcessorTime.TotalMilliseconds } catch {} }
        if ($e.Kind -ne 'target' -or $e.Exempt) { continue }
        if ($p.Id -eq $fg) {
            # Whatever you're actively using runs at full speed.
            if ($e.Throttled) { Set-Throttle $p $e $false }
            if ([GmNative]::ForegroundIsFullscreen()) {   # fullscreen = probably a game we didn't know about
                $e.Exempt = $true
                $e.CpuBase = $null                        # it's the game - don't count it as background use
                Write-Log "Fullscreen app left at full speed for this session: $($e.Name)"
            }
        } elseif (-not $e.Throttled -and ($initial -or ([DateTime]::UtcNow - $e.FirstSeen).TotalSeconds -ge $GraceSeconds)) {
            Set-Throttle $p $e $true
            $dirty = $true
        }
    }
    foreach ($id in @($script:procs.Keys)) {
        if ($alive.ContainsKey($id)) { continue }
        $e = $script:procs[$id]
        if ($null -ne $e.CpuBase) { Add-CpuUse $e.Name ($e.CpuLast - $e.CpuBase) }   # app closed - keep its CPU use for the report
        $script:procs.Remove($id)
    }
    if ($dirty) { Save-State }
}

# ---------------------------------------------------------------- session report (#5)
function Add-CpuUse($name, $ms) {
    if ($ms -le 0) { return }
    if ($script:cpuByName.ContainsKey($name)) { $script:cpuByName[$name] += $ms } else { $script:cpuByName[$name] = $ms }
}

function Get-SessionReport {
    foreach ($e in @($script:procs.Values)) {
        if ($null -eq $e.CpuBase) { continue }
        $p = Get-Process -Id $e.Id -ErrorAction SilentlyContinue
        if ($p) { try { $e.CpuLast = $p.TotalProcessorTime.TotalMilliseconds } catch {} }
        Add-CpuUse $e.Name ($e.CpuLast - $e.CpuBase)
        $e.CpuBase = $null
    }
    $elapsed = ((Get-Date) - $script:startedAt).TotalMilliseconds
    $capacity = [math]::Max(1, $elapsed * [Environment]::ProcessorCount)   # 100% = every core busy the whole session
    $total = 0; foreach ($v in $script:cpuByName.Values) { $total += $v }
    $mins = [math]::Max(1, [math]::Round($elapsed / 60000))
    $top = $script:cpuByName.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 3 |
           ForEach-Object { '{0} {1:0.0}%' -f $_.Key, (100 * $_.Value / $capacity) }
    $line = 'Pushed-back apps used {0:0.0}% of your CPU over {1} min' -f (100 * $total / $capacity), $mins
    if ($top) { $line += " (top: $($top -join ', '))" }
    $line + '.'
}

function Save-State {
    $script:state.processes = @($script:procs.Values | Where-Object Touched | ForEach-Object {
        [ordered]@{ id = $_.Id; name = $_.Name; start = $_.Start; priority = $_.Orig } })
    New-Item -ItemType Directory -Path $StateDir -Force | Out-Null
    $script:state | ConvertTo-Json -Depth 5 | Set-Content -Path $StateFile -Encoding UTF8
}

# ---------------------------------------------------------------- system-wide tweaks
function Enable-SystemTweaks {
    if (Test-Admin) {
        foreach ($name in $PausableServices) {
            $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
            if ($svc -and $svc.Status -eq 'Running') {
                try { Stop-Service -Name $name -ErrorAction Stop; $script:state.services += $name }
                catch { Write-Log "Couldn't pause $name : $($_.Exception.Message)" }
            }
        }
        try { Suspend-WindowsUpdate } catch { Write-Log "Couldn't pause Windows Update: $($_.Exception.Message)" }
        try { Set-UploadCaps }        catch { Write-Log "Couldn't set upload limits: $($_.Exception.Message)" }
        try { Suspend-Maintenance }   catch { Write-Log "Couldn't hold off maintenance: $($_.Exception.Message)" }
        try { Limit-DefenderScans }   catch { Write-Log "Couldn't limit Defender scans: $($_.Exception.Message)" }
    } else { Write-Log 'Not admin - services, Windows Update, upload limits, maintenance and Defender left alone.' }
    try { Disable-BackgroundRecording } catch { Write-Log "Couldn't turn off background recording: $($_.Exception.Message)" }

    $active = Get-ActivePlan
    if ($active -and $LowPowerPlans -contains $active -and ((powercfg /list) -match $HighPerfPlan)) {
        powercfg /setactive $HighPerfPlan | Out-Null
        $script:state.powerPlan = $active
    }

    $gm = (Get-ItemProperty -Path $GameBarKey -Name AutoGameModeEnabled -ErrorAction SilentlyContinue).AutoGameModeEnabled
    if ($gm -ne 1) {
        if (-not (Test-Path $GameBarKey)) { New-Item -Path $GameBarKey -Force | Out-Null }
        Set-ItemProperty -Path $GameBarKey -Name AutoGameModeEnabled -Value 1 -Type DWord
        $script:state.gameMode = if ($null -eq $gm) { 'missing' } else { "$gm" }
    }
}

# Uses the same "Pause updates" setting as the Settings app. Your own pause, if longer, is left untouched.
function Suspend-WindowsUpdate {
    $cur   = Get-ItemProperty -Path $WuKey -ErrorAction SilentlyContinue
    $until = (Get-Date).ToUniversalTime().AddDays($WuPauseDays)
    if ($cur -and $cur.PauseUpdatesExpiryTime) {
        try { if ([DateTime]::Parse($cur.PauseUpdatesExpiryTime).ToUniversalTime() -ge $until) { Write-Log 'Windows Update already paused by you - left alone.'; return } } catch {}
    }
    $orig = [ordered]@{}
    foreach ($n in $WuNames) { $orig[$n] = if ($cur -and $null -ne $cur.$n) { "$($cur.$n)" } else { $null } }
    if (-not (Test-Path $WuKey)) { New-Item -Path $WuKey -Force | Out-Null }
    $now = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); $end = $until.ToString('yyyy-MM-ddTHH:mm:ssZ')
    foreach ($n in $WuNames) { Set-ItemProperty -Path $WuKey -Name $n -Value $(if ($n -like '*Start*') { $now } else { $end }) -Type String }
    $script:state.wuPause = $orig
    Write-Log "Windows Update paused (expires on its own at $end UTC)."
}

# Per-app upload caps. ActiveStore = in memory only, so a restart clears them too.
function Set-UploadCaps {
    if (-not (Test-Path $UploadFile)) { return }
    foreach ($raw in Get-Content $UploadFile) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $parts = $line -split '\s+'
        if ($parts.Count -lt 2) { continue }
        $exe = $parts[0]; $mbps = [double]$parts[1]
        if (Test-Protected ($exe -replace '\.exe$', '') $null) { continue }
        $name = "GamingMode-$exe"
        Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue
        New-NetQosPolicy -Name $name -AppPathNameMatchCondition $exe -ThrottleRateActionBitsPerSecond ([uint64]($mbps * 1e6)) `
            -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
        $script:state.qos += $name
    }
    if ($script:state.qos) { Write-Log "Upload limits on: $($script:state.qos -join ', ')" }
}

# Windows Automatic Maintenance (disk optimise, scheduled scans, cleanup) - a classic mid-game stutter source.
function Suspend-Maintenance {
    $v = (Get-ItemProperty -Path $MaintKey -Name MaintenanceDisabled -ErrorAction SilentlyContinue).MaintenanceDisabled
    if ($v -eq 1) { return }   # already off
    Set-ItemProperty -Path $MaintKey -Name MaintenanceDisabled -Value 1 -Type DWord
    $script:state.maintenance = if ($null -eq $v) { 'missing' } else { "$v" }
    Write-Log 'Automatic Maintenance held off.'
}

# Caps how much CPU Defender's scheduled scans may use. Real-time protection is untouched.
function Limit-DefenderScans {
    $before = (Get-MpPreference -ErrorAction Stop).ScanAvgCPULoadFactor
    if ($before -le $DefenderScanCpu) { return }
    Set-MpPreference -ScanAvgCPULoadFactor $DefenderScanCpu -ErrorAction Stop
    if ((Get-MpPreference).ScanAvgCPULoadFactor -ne $DefenderScanCpu) { Write-Log 'Defender ignored the scan limit (managed by policy or another antivirus).'; return }
    $script:state.defenderCpu = [int]$before
    Write-Log "Defender scheduled scans limited to $DefenderScanCpu% CPU (was $before%)."
}

# Game Bar "record what happened" encodes video in the background - pointless double work next to OBS.
function Disable-BackgroundRecording {
    if (-not (Get-Process -Name $StreamApps -ErrorAction SilentlyContinue)) { return }   # only while a streaming app is open
    $v = (Get-ItemProperty -Path $DvrKey -Name HistoricalCaptureEnabled -ErrorAction SilentlyContinue).HistoricalCaptureEnabled
    if ($v -ne 1) { return }
    Set-ItemProperty -Path $DvrKey -Name HistoricalCaptureEnabled -Value 0 -Type DWord
    $script:state.dvrHistorical = "$v"
    Write-Log 'Game Bar background recording turned off (streaming app is running).'
}

# Puts everything recorded in the state file back. Also used to clean up after a restart or crash.
function Restore-FromState {
    if (-not (Test-Path $StateFile)) { return 0 }
    $s = Get-Content $StateFile -Raw | ConvertFrom-Json
    $restored = 0
    foreach ($e in @($s.processes)) {
        if (-not $e) { continue }
        $p = Get-Process -Id $e.id -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        try {
            if ("$($p.StartTime.ToUniversalTime().Ticks)" -ne $e.start) { continue }   # PID reused by a different app
            [void][GmNative]::SetEfficiency($p.Id, $false)
            if ($p.PriorityClass -eq [Diagnostics.ProcessPriorityClass]::Idle) { $p.PriorityClass = [Diagnostics.ProcessPriorityClass]$e.priority }
            $restored++
        } catch {}
    }
    foreach ($name in @($s.services)) {
        if (-not $name) { continue }
        try { Start-Service -Name $name -ErrorAction Stop } catch { Write-Log "Couldn't restart $name (Windows will start it later)." }
    }
    if ($s.powerPlan) { powercfg /setactive $s.powerPlan | Out-Null }
    if ($s.maintenance) {
        if ($s.maintenance -eq 'missing') { Remove-ItemProperty -Path $MaintKey -Name MaintenanceDisabled -ErrorAction SilentlyContinue }
        else { Set-ItemProperty -Path $MaintKey -Name MaintenanceDisabled -Value ([int]$s.maintenance) -Type DWord }
    }
    if ($null -ne $s.defenderCpu) {
        try { Set-MpPreference -ScanAvgCPULoadFactor ([int]$s.defenderCpu) -ErrorAction Stop } catch { Write-Log "Couldn't restore Defender scan limit: $($_.Exception.Message)" }
    }
    if ($s.dvrHistorical) { Set-ItemProperty -Path $DvrKey -Name HistoricalCaptureEnabled -Value ([int]$s.dvrHistorical) -Type DWord }
    foreach ($q in @($s.qos)) { if ($q) { Remove-NetQosPolicy -Name $q -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue } }
    if ($s.wuPause) {
        foreach ($prop in $s.wuPause.PSObject.Properties) {
            try {
                if ($null -eq $prop.Value) { Remove-ItemProperty -Path $WuKey -Name $prop.Name -ErrorAction SilentlyContinue }
                else { Set-ItemProperty -Path $WuKey -Name $prop.Name -Value $prop.Value -Type String }
            } catch { Write-Log "Couldn't restore $($prop.Name): $($_.Exception.Message)" }
        }
    }
    if ($s.gameMode) {
        if ($s.gameMode -eq 'missing') { Remove-ItemProperty -Path $GameBarKey -Name AutoGameModeEnabled -ErrorAction SilentlyContinue }
        else { Set-ItemProperty -Path $GameBarKey -Name AutoGameModeEnabled -Value ([int]$s.gameMode) -Type DWord }
    }
    Remove-Item $StateFile -Force
    Write-Log "Restored $restored app(s) and system settings."
    $restored
}

# ---------------------------------------------------------------- tray app
function New-TrayIcon {
    $bmp = New-Object Drawing.Bitmap 32, 32
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0, 190, 90))), 1, 1, 30, 30)
    $font = New-Object Drawing.Font('Segoe UI', 19, [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString('G', $font, [Drawing.Brushes]::White, (New-Object Drawing.RectangleF(0, 1, 32, 32)), $sf)
    $g.Dispose()
    [Drawing.Icon]::FromHandle($bmp.GetHicon())
}

function Update-TrayText {
    $n = @($script:procs.Values | Where-Object Throttled).Count
    $script:ni.Text = "Gaming Mode ON - $n apps in the background"
    $script:statusItem.Text = "ON: $n apps pushed back, $(Format-GB $script:freed) RAM freed"
}

function Start-GamingMode {
    $created = $false
    try { $script:offEvent = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::ManualReset, $OffEventName, [ref]$created) }
    catch { Write-Log "Couldn't reach the running Gaming Mode: $($_.Exception.Message)"; return }
    if (-not $created) { [void]$script:offEvent.Set(); return }   # already on -> this click turns it off

    [void][GmNative]::SetProcessDPIAware()
    $script:session = (Get-Process -Id $PID).SessionId
    $script:windir  = $env:windir.TrimEnd('\') + '\'
    $script:prot    = Get-Protection
    $script:procs   = @{}
    $script:freed   = 0

    # Left on from before a restart / crash? Put that back first, then start fresh.
    if (Test-Path $StateFile) { Write-Log 'Found leftovers from last time - cleaning up.'; [void](Restore-FromState) }

    $script:state = [ordered]@{ enabledAt = (Get-Date).ToString('o'); processes = @(); services = @(); powerPlan = $null
                                gameMode = $null; wuPause = $null; qos = @()
                                maintenance = $null; defenderCpu = $null; dvrHistorical = $null }
    $script:ni = $null
    $script:startedAt = Get-Date
    $script:cpuByName = @{}
    $report = $null
    try {
        Enable-SystemTweaks
        Save-State
        Invoke-Scan $true
        Save-State
        Write-Log "ON. Pushed back: $((@($script:procs.Values | Where-Object Throttled).Name | Sort-Object -Unique) -join ', ')"

        $menu = New-Object Windows.Forms.ContextMenuStrip
        $script:statusItem = $menu.Items.Add('ON'); $script:statusItem.Enabled = $false
        [void]$menu.Items.Add('-')
        $menu.Items.Add('Turn off Gaming Mode').Add_Click({ [void]$script:offEvent.Set() }) | Out-Null
        $menu.Items.Add('Edit protected apps...').Add_Click({ Start-Process notepad.exe -ArgumentList "`"$ConfigFile`"" }) | Out-Null
        $menu.Items.Add('Reload protected apps').Add_Click({
            try {
                $script:prot = Get-Protection
                foreach ($id in @($script:procs.Keys)) {
                    $e = $script:procs[$id]
                    if ($e.Throttled) { $p = Get-Process -Id $id -ErrorAction SilentlyContinue; if ($p) { Set-Throttle $p $e $false } }
                    if (-not $e.Touched) { $script:procs.Remove($id) } else { $e.Kind = 'reload' }
                }
                # Touched entries keep their saved original priority; re-check them against the new list.
                foreach ($id in @($script:procs.Keys)) {
                    $e = $script:procs[$id]; $p = Get-Process -Id $id -ErrorAction SilentlyContinue
                    $path = $null; try { $path = $p.Path } catch {}
                    if ($p -and $path -and -not (Test-Protected $e.Name $path)) { $e.Kind = 'target'; Set-Throttle $p $e $true } else { $e.Kind = 'protected' }
                }
                Save-State; Update-TrayText
                $script:ni.ShowBalloonTip(3000, 'Gaming Mode', 'Protected apps list reloaded.', 'Info')
            } catch { Write-Log "Reload failed: $($_.Exception.Message)" }
        }) | Out-Null
        $menu.Items.Add('Open log').Add_Click({ if (Test-Path $LogFile) { Start-Process notepad.exe -ArgumentList "`"$LogFile`"" } }) | Out-Null

        $script:ni = New-Object Windows.Forms.NotifyIcon
        $script:ni.Icon = New-TrayIcon
        $script:ni.ContextMenuStrip = $menu
        $script:ni.Visible = $true
        Update-TrayText
        $n = @($script:procs.Values | Where-Object Throttled).Count
        $script:ni.ShowBalloonTip(4000, 'Gaming Mode ON', "$n apps pushed to the back, $(Format-GB $script:freed) of RAM freed. Click the shortcut again to turn off.", 'Info')

        $scan = New-Object Windows.Forms.Timer; $scan.Interval = $ScanMs
        $scan.Add_Tick({ try { Invoke-Scan $false; Update-TrayText } catch { Write-Log "Scan error: $($_.Exception.Message)" } })
        $watch = New-Object Windows.Forms.Timer; $watch.Interval = 300
        $watch.Add_Tick({ if ($script:offEvent.WaitOne(0)) { [Windows.Forms.Application]::Exit() } })
        $scan.Start(); $watch.Start()
        [Windows.Forms.Application]::Run()
        $scan.Stop(); $watch.Stop()
    } catch {
        Write-Log "Error: $($_.Exception.Message)"
    } finally {
        try { $report = Get-SessionReport } catch { Write-Log "Report error: $($_.Exception.Message)" }
        $count = Restore-FromState
        Write-Log "OFF. $report"
        if ($script:ni) {
            $script:ni.ShowBalloonTip(5000, 'Gaming Mode OFF', ("All back to normal ($count apps restored). $report" | ForEach-Object { if ($_.Length -gt 250) { $_.Substring(0, 250) } else { $_ } }), 'Info')
            Start-Sleep -Seconds 3
            $script:ni.Visible = $false; $script:ni.Dispose()
        }
        $script:offEvent.Dispose()
    }
}

# ---------------------------------------------------------------- setup
function Set-DesktopShortcut([bool]$viaTask) {
    $lnkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Gaming Mode.lnk'
    $ws  = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut($lnkPath)
    if ($viaTask) {
        # conhost --headless = no console window flash
        $lnk.TargetPath = "$env:windir\System32\conhost.exe"
        $lnk.Arguments  = "--headless `"$env:windir\System32\schtasks.exe`" /run /tn `"$TaskName`""
    } else {
        $lnk.TargetPath = Join-Path $PSScriptRoot 'Gaming Mode.bat'
        $lnk.Arguments  = ''
    }
    $lnk.WorkingDirectory = $PSScriptRoot
    $ico = Join-Path $PSScriptRoot 'GamingMode.ico'
    $lnk.IconLocation = if (Test-Path $ico) { "$ico,0" } else { "$env:windir\System32\imageres.dll,-1028" }
    $lnk.Description  = 'Toggle Gaming Mode on/off'
    $lnk.Save()
    $lnkPath
}

function Install-GamingMode {
    if (-not (Test-Admin)) { throw 'Setup needs to run as admin.' }
    $ps  = "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"
    $arg = "--headless `"$ps`" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" -Toggle"
    $action    = New-ScheduledTaskAction -Execute "$env:windir\System32\conhost.exe" -Argument $arg -WorkingDirectory $PSScriptRoot
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
    # No time limit (the tray stays up while gaming) and Parallel so a second click can reach the running one.
    $settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances Parallel `
                    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Priority 5
    Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings `
        -Description 'One-click Gaming Mode toggle (runs elevated so there is no admin prompt).' -Force | Out-Null
    $lnk = Set-DesktopShortcut $true
    Write-Host "  Installed. Desktop shortcut: $lnk" -ForegroundColor Green
    Write-Host '  From now on clicking it toggles Gaming Mode with no admin prompt.' -ForegroundColor Green
}

function Uninstall-GamingMode {
    if (-not (Test-Admin)) { throw 'Removing setup needs to run as admin.' }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    [void](Set-DesktopShortcut $false)
    Write-Host '  Setup removed. The shortcut now runs Gaming Mode.bat (asks for admin each time).' -ForegroundColor Yellow
}

function Show-DryRun {
    $script:session = (Get-Process -Id $PID).SessionId
    $script:windir  = $env:windir.TrimEnd('\') + '\'
    $script:prot    = Get-Protection
    $script:procs   = @{}
    foreach ($p in [Diagnostics.Process]::GetProcesses()) {
        if ($p.SessionId -eq $script:session -and $p.Id -ne $PID) { [void](Get-Entry $p) }
    }
    $t = @($script:procs.Values | Where-Object Kind -eq 'target'    | ForEach-Object Name | Sort-Object -Unique)
    $k = @($script:procs.Values | Where-Object Kind -eq 'protected' | ForEach-Object Name | Sort-Object -Unique)
    $ram = ($script:procs.Values | Where-Object Kind -eq 'target' | ForEach-Object { (Get-Process -Id $_.Id -ErrorAction SilentlyContinue).WorkingSet64 } | Measure-Object -Sum).Sum
    Write-Host ''
    Write-Host '  GAMING MODE - dry run (nothing changed)' -ForegroundColor Green
    Write-Host ''
    Write-Host ("  Would push to the back ({0}): {1}" -f $t.Count, ($t -join ', ')) -ForegroundColor Yellow
    Write-Host ("  They're holding {0} of RAM, most of which would be handed back." -f (Format-GB $ram)) -ForegroundColor Yellow
    Write-Host ''
    Write-Host ("  Kept at full speed ({0}): {1}" -f $k.Count, ($k -join ', ')) -ForegroundColor Cyan
}

# ---------------------------------------------------------------- main
try {
    if     ($Install)   { Install-GamingMode }
    elseif ($Uninstall) { Uninstall-GamingMode }
    elseif ($DryRun)    { Show-DryRun }
    else                { Start-GamingMode }     # -Toggle (default)
} catch {
    Write-Log "Fatal: $($_.Exception.Message)"
    Write-Host "  Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
