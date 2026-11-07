using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GamingMode.Core;

/// <summary>
/// The machine-wide parts of Gaming Mode. Every Apply* records the original value in the session state
/// before changing anything, and Restore puts back exactly what was recorded - nothing else.
/// </summary>
internal static class SystemTweaks
{
    private static readonly string[] PausableServices = { "WSearch", "SysMain", "DiagTrack" };
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    private const string WuKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    internal static readonly string[] WuNames =
    {
        "PauseUpdatesStartTime", "PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime",
        "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
    };
    private const string MaintenanceKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string DvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private static readonly Protection StreamingApps = new(new[]
        { "obs64", "obs32", "obs", "Streamlabs*", "xsplit*", "prismlivestudio", "twitchstudio*", "meld*" });

    // Only plain file names reach WMI (and they come from settings any local user can edit).
    private static readonly Regex SafeExeName = new(@"^[A-Za-z0-9 ._()\-]{1,100}\.exe$", RegexOptions.IgnoreCase);
    internal static bool IsSafeExeName(string name) => SafeExeName.IsMatch(name);

    public static void Apply(SessionState s, Settings cfg)
    {
        var f = cfg.Features;
        if (f.PauseServices) Try("pause services", () => PauseServices(s));
        if (f.HighPerformancePower) Try("power plan", () => SetPowerPlan(s));
        if (f.WindowsGameMode) Try("Windows Game Mode", () => EnableGameMode(s));
        if (f.PauseWindowsUpdate) Try("pause Windows Update", () => PauseWindowsUpdate(s, cfg.WindowsUpdatePauseDays));
        if (f.UploadLimits) Try("upload limits", () => SetUploadLimits(s, cfg));
        if (f.HoldMaintenance) Try("hold maintenance", () => HoldMaintenance(s));
        if (f.LimitDefenderScans) Try("limit Defender scans", () => LimitDefender(s, cfg.DefenderScanCpuPercent));
        if (f.StopBackgroundRecording) Try("background recording", () => StopBackgroundRecording(s));
    }

    public static void Restore(SessionState s)
    {
        foreach (var name in s.Services)
            Try($"restart {name}", () =>
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            });
        if (s.PowerPlan is Guid plan) Try("power plan", () => Native.SetActivePowerScheme(plan));
        if (s.GameMode != null) Try("Windows Game Mode", () => RestoreUserDword(s.UserSid, GameBarKey, "AutoGameModeEnabled", s.GameMode));
        if (s.WindowsUpdatePause != null) Try("Windows Update", () => RestoreWindowsUpdate(s.WindowsUpdatePause));
        foreach (var q in s.QosPolicies) Try($"remove upload limit {q}", () => RemoveQosPolicy(q));
        if (s.Maintenance != null) Try("maintenance", () => RestoreMachineDword(MaintenanceKey, "MaintenanceDisabled", s.Maintenance));
        if (s.DefenderScanCpu is int cpu) Try("Defender", () => SetDefenderScanCpu(cpu));
        if (s.BackgroundRecording != null) Try("background recording", () => RestoreUserDword(s.UserSid, DvrKey, "HistoricalCaptureEnabled", s.BackgroundRecording));
    }

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Write($"Couldn't {what}: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- services
    private static void PauseServices(SessionState s)
    {
        foreach (var name in PausableServices)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status != ServiceControllerStatus.Running || !sc.CanStop) continue;
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                s.Services.Add(name);
            }
            catch (Exception ex) { Log.Write($"Couldn't pause {name}: {ex.Message}"); }
        }
        if (s.Services.Count > 0) Log.Write($"Paused services: {string.Join(", ", s.Services)}");
    }

    // ---------------------------------------------------------------- power plan (only away from Balanced / Power saver)
    private static void SetPowerPlan(SessionState s)
    {
        var active = Native.GetActivePowerScheme();
        if (active != Balanced && active != PowerSaver) return;
        if (Native.SetActivePowerScheme(HighPerformance)) { s.PowerPlan = active; Log.Write("Power plan -> High performance."); }
        else Log.Write("No High performance power plan on this PC - left alone.");
    }

    // ---------------------------------------------------------------- per-user registry (HKEY_USERS\<sid>)
    private static RegistryKey? OpenUserKey(string? sid, string subKey, bool create)
    {
        if (string.IsNullOrEmpty(sid)) return null;
        var path = sid + "\\" + subKey;
        return create ? Registry.Users.CreateSubKey(path, writable: true) : Registry.Users.OpenSubKey(path, writable: true);
    }

    private static void EnableGameMode(SessionState s)
    {
        using var key = OpenUserKey(s.UserSid, GameBarKey, create: true);
        if (key == null) return;
        var v = key.GetValue("AutoGameModeEnabled");
        if (v is int i && i == 1) return;
        key.SetValue("AutoGameModeEnabled", 1, RegistryValueKind.DWord);
        s.GameMode = v is int old ? old.ToString(CultureInfo.InvariantCulture) : SessionState.Missing;
    }

    /// <summary>Game Bar "record what happened" encodes video in the background - pointless next to OBS.</summary>
    private static void StopBackgroundRecording(SessionState s)
    {
        bool streaming = Process.GetProcesses().Any(p =>
        {
            using (p) return p.SessionId == s.SessionId && StreamingApps.IsProtected(p.ProcessName, null);
        });
        if (!streaming) return;
        using var key = OpenUserKey(s.UserSid, DvrKey, create: false);
        if (key?.GetValue("HistoricalCaptureEnabled") is not int v || v != 1) return;
        key.SetValue("HistoricalCaptureEnabled", 0, RegistryValueKind.DWord);
        s.BackgroundRecording = "1";
        Log.Write("Game Bar background recording off (streaming app is open).");
    }

    private static void RestoreUserDword(string? sid, string subKey, string name, string original)
    {
        using var key = OpenUserKey(sid, subKey, create: false);
        if (key == null) return;
        if (original == SessionState.Missing) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, int.Parse(original, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
    }

    // ---------------------------------------------------------------- Windows Update pause (same keys as the Settings app)
    internal static bool ShouldPause(string? existingExpiry, DateTime untilUtc) =>
        !(DateTime.TryParse(existingExpiry, CultureInfo.InvariantCulture,
              DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var e) && e >= untilUtc);

    private static void PauseWindowsUpdate(SessionState s, int days)
    {
        using var key = Registry.LocalMachine.CreateSubKey(WuKey, writable: true);
        var until = DateTime.UtcNow.AddDays(days);
        if (!ShouldPause(key.GetValue("PauseUpdatesExpiryTime") as string, until))
        {
            Log.Write("Windows Update already paused by you - left alone.");
            return;
        }
        s.WindowsUpdatePause = WuNames.ToDictionary(n => n, n => key.GetValue(n)?.ToString());
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        string end = until.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        foreach (var n in WuNames) key.SetValue(n, n.Contains("Start") ? now : end, RegistryValueKind.String);
        Log.Write($"Windows Update paused (expires on its own at {end}).");
    }

    private static void RestoreWindowsUpdate(Dictionary<string, string?> original)
    {
        using var key = Registry.LocalMachine.OpenSubKey(WuKey, writable: true);
        if (key == null) return;
        foreach (var (name, value) in original)
        {
            if (!WuNames.Contains(name)) continue;
            if (value == null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    // ---------------------------------------------------------------- Automatic Maintenance
    private static void HoldMaintenance(SessionState s)
    {
        using var key = Registry.LocalMachine.CreateSubKey(MaintenanceKey, writable: true);
        var v = key.GetValue("MaintenanceDisabled");
        if (v is int i && i == 1) return;
        key.SetValue("MaintenanceDisabled", 1, RegistryValueKind.DWord);
        s.Maintenance = v is int old ? old.ToString(CultureInfo.InvariantCulture) : SessionState.Missing;
        Log.Write("Automatic Maintenance held off.");
    }

    private static void RestoreMachineDword(string subKey, string name, string original)
    {
        using var key = Registry.LocalMachine.OpenSubKey(subKey, writable: true);
        if (key == null) return;
        if (original == SessionState.Missing) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, int.Parse(original, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
    }

    // ---------------------------------------------------------------- Defender scheduled-scan CPU cap
    private const string DefenderNs = @"\\.\root\Microsoft\Windows\Defender";

    private static int? GetDefenderScanCpu()
    {
        using var cls = new ManagementClass(DefenderNs, "MSFT_MpPreference", null);
        foreach (ManagementObject o in cls.GetInstances())
            using (o) return Convert.ToInt32(o["ScanAvgCPULoadFactor"], CultureInfo.InvariantCulture);
        return null;
    }

    private static void SetDefenderScanCpu(int percent)
    {
        using var cls = new ManagementClass(DefenderNs, "MSFT_MpPreference", null);
        using var args = cls.GetMethodParameters("Set");
        args["ScanAvgCPULoadFactor"] = (byte)percent;
        cls.InvokeMethod("Set", args, null);
    }

    private static void LimitDefender(SessionState s, int percent)
    {
        var before = GetDefenderScanCpu();
        if (before is null || before <= percent) return;
        SetDefenderScanCpu(percent);
        if (GetDefenderScanCpu() != percent) { Log.Write("Defender ignored the scan limit (managed by policy or another antivirus)."); return; }
        s.DefenderScanCpu = before;
        Log.Write($"Defender scheduled scans limited to {percent}% CPU (was {before}%).");
    }

    // ---------------------------------------------------------------- per-app upload caps (QoS policies, in memory only)
    private const string QosNs = @"\\.\root\StandardCimv2";
    private const string QosPrefix = "AceBotGamingMode-";

    private static ManagementNamedValueCollection ActiveStore()
    {
        var ctx = new ManagementNamedValueCollection();
        ctx.Add("PolicyStore", "ActiveStore");   // memory only: a restart clears these too
        return ctx;
    }

    private static void SetUploadLimits(SessionState s, Settings cfg)
    {
        var protection = new Protection(cfg.ProtectedApps);
        foreach (var limit in cfg.UploadLimits)
        {
            if (!IsSafeExeName(limit.App)) { Log.Write($"Skipped upload limit for '{limit.App}' (not a plain .exe name)."); continue; }
            if (protection.IsProtected(Path.GetFileNameWithoutExtension(limit.App), null)) continue;
            var name = QosPrefix + limit.App;
            try { RemoveQosPolicy(name); } catch { /* not there */ }
            using var cls = new ManagementClass(QosNs, "MSFT_NetQosPolicySettingData", null);
            using var inst = cls.CreateInstance();
            inst["Name"] = name;
            inst["AppPathNameMatchCondition"] = limit.App;
            inst["ThrottleRateAction"] = (ulong)(limit.Mbps * 1_000_000);
            inst.Put(new PutOptions(ActiveStore(), TimeSpan.FromSeconds(15), false, PutType.CreateOnly));
            s.QosPolicies.Add(name);
        }
        if (s.QosPolicies.Count > 0) Log.Write($"Upload limits on: {string.Join(", ", s.QosPolicies.Select(q => q[QosPrefix.Length..]))}");
    }

    private static void RemoveQosPolicy(string name)
    {
        if (!name.StartsWith(QosPrefix, StringComparison.Ordinal) || !IsSafeExeName(name[QosPrefix.Length..])) return;
        var query = new ObjectQuery($"SELECT * FROM MSFT_NetQosPolicySettingData WHERE Name = '{name}'");
        using var searcher = new ManagementObjectSearcher(new ManagementScope(QosNs), query,
            new System.Management.EnumerationOptions { Context = ActiveStore(), ReturnImmediately = false });
        foreach (ManagementObject o in searcher.Get())
            using (o) o.Delete(new DeleteOptions(ActiveStore(), TimeSpan.FromSeconds(15)));
    }

    /// <summary>Removes any of our upload limits still in memory (used by startup cleanup).</summary>
    public static void RemoveAllOurQosPolicies()
    {
        var query = new ObjectQuery("SELECT Name FROM MSFT_NetQosPolicySettingData");
        using var searcher = new ManagementObjectSearcher(new ManagementScope(QosNs), query,
            new System.Management.EnumerationOptions { Context = ActiveStore(), ReturnImmediately = false });
        foreach (ManagementObject o in searcher.Get())
            using (o)
                if (o["Name"] is string n && n.StartsWith(QosPrefix, StringComparison.Ordinal))
                    Try($"remove leftover {n}", () => RemoveQosPolicy(n));
    }
}
