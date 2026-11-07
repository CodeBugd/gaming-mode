using System.Diagnostics;
using GamingMode.Core;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace GamingMode.App;

/// <summary>
/// The tray icon. Shows on/off, tells the service which window you're using, and pops up a notification
/// when Gaming Mode turns on or off (including the CPU report).
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    public const string TrayExitEventName = @"Global\AceBot.GamingMode.TrayExit";
    private const string UserKey = @"Software\AceBot\GamingMode";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem, _toggleItem;
    private readonly Timer _timer;
    private readonly EventWaitHandle? _exitEvent;
    private bool? _wasOn;
    private int _lastOffCount = -1;
    private bool _busy, _polling;
    private SettingsForm? _settings;

    /// <param name="justChanged">Status right after this process toggled, so the first notification still shows.</param>
    public TrayApp(EngineStatus? justChanged = null)
    {
        if (justChanged != null)
        {
            _wasOn = !justChanged.IsOn;
            _lastOffCount = justChanged.IsOn ? justChanged.OffCount : justChanged.OffCount - 1;
        }
        try { _exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, TrayExitEventName); } catch { }

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Gaming Mode") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Turn Gaming Mode on", null, (_, _) => _ = ToggleAsync());
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add("Do Not Disturb while gaming...", null, (_, _) => SettingsForm.OpenDoNotDisturbSettings());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add("Check for updates", null, (_, _) => _ = CheckUpdatesAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Hide tray icon", null, (_, _) => ExitThread());

        _icon = new NotifyIcon { Icon = Icons.Off, Text = "Gaming Mode", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => _ = ToggleAsync();
        _icon.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;

        _timer = new Timer { Interval = 1000 };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();

        if (Registry.CurrentUser.OpenSubKey(UserKey)?.GetValue("FirstRunDone") == null)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UserKey)) k.SetValue("FirstRunDone", 1, RegistryValueKind.DWord);
            ShowSettings(firstRun: true);
        }
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            if (_exitEvent?.WaitOne(0) == true) { ExitThread(); return; }
            IpcResponse r;
            if (_wasOn == true)
            {
                var (pid, full) = Foreground.Get();
                r = await Ipc.SendAsync(new IpcRequest { Command = "foreground", ForegroundPid = pid, Fullscreen = full }, 1500);
            }
            else r = await Ipc.SendAsync(new IpcRequest { Command = "status" }, 1500);
            if (r.Status != null) Show(r.Status);
        }
        catch
        {
            _icon.Icon = Icons.Off;
            _icon.Text = "Gaming Mode - service not running";
            _statusItem.Text = "Service not running (reinstall Gaming Mode)";
            _toggleItem.Enabled = false;
            _wasOn = null;
        }
        finally { _polling = false; }
    }

    private void Show(EngineStatus s)
    {
        _toggleItem.Enabled = !_busy;
        _icon.Icon = s.IsOn ? Icons.On : Icons.Off;
        _toggleItem.Text = s.IsOn ? "Turn Gaming Mode off" : "Turn Gaming Mode on";
        _statusItem.Text = s.IsOn ? $"ON: {s.ThrottledCount} apps pushed back, {s.FreedMb:0} MB RAM freed" : "Gaming Mode is off";
        _icon.Text = Trim(s.IsOn ? $"Gaming Mode ON - {s.ThrottledCount} apps in the background" : "Gaming Mode is off", 63);

        if (_wasOn == false && s.IsOn)
        {
            if (!ShowDoNotDisturbTipOnce())
                Balloon("Gaming Mode ON", $"{s.ThrottledCount} apps pushed to the back, {s.FreedMb:0} MB of RAM freed. Double-click the tray icon or the shortcut to turn off.");
        }
        if (_lastOffCount >= 0 && s.OffCount > _lastOffCount && !string.IsNullOrEmpty(s.LastReport))
            Balloon("Gaming Mode OFF", s.LastReport!);
        _wasOn = s.IsOn;
        _lastOffCount = s.OffCount;
        NoticeNewVersion(s.Version);
    }

    private Action? _balloonClick;

    /// <summary>The first time Gaming Mode turns on: suggest Windows' own "Do not disturb while gaming" (click opens it).</summary>
    private bool ShowDoNotDisturbTipOnce()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UserKey);
        if (key.GetValue("DndTipShown") != null) return false;
        key.SetValue("DndTipShown", 1, RegistryValueKind.DWord);
        Balloon("Gaming Mode ON - one more tip", "Let Windows silence notifications while you play. Click here, then turn on " +
                "\"When playing a game\" under \"Turn on do not disturb automatically\".", SettingsForm.OpenDoNotDisturbSettings);
        return true;
    }

    private bool _versionChecked;

    /// <summary>One notification the first time the tray sees a new version (after an automatic update).</summary>
    private void NoticeNewVersion(string version)
    {
        if (_versionChecked) return;
        _versionChecked = true;
        using var key = Registry.CurrentUser.CreateSubKey(UserKey);
        var seen = key.GetValue("LastSeenVersion") as string;
        if (seen == version) return;
        key.SetValue("LastSeenVersion", version);
        if (seen != null) Balloon("Gaming Mode updated", $"Gaming Mode was updated to version {version}.");
    }

    private async Task CheckUpdatesAsync()
    {
        Balloon("Gaming Mode", "Checking for updates...");
        try
        {
            var r = await Ipc.SendAsync(new IpcRequest { Command = "checkUpdate" }, 3000);
            Balloon("Gaming Mode", r.Message ?? r.Error ?? "Done.");
        }
        catch (Exception ex) { Balloon("Gaming Mode", $"Couldn't check for updates: {ex.Message}"); }
    }

    private async Task ToggleAsync()
    {
        if (_busy) return;
        _busy = true; _toggleItem.Enabled = false;
        try
        {
            var r = await Ipc.SendAsync(new IpcRequest { Command = "toggle" }, 3000);
            if (!r.Ok) Balloon("Gaming Mode", r.Error ?? "Something went wrong.");
            else if (r.Status != null) Show(r.Status);
        }
        catch (Exception ex) { Balloon("Gaming Mode", $"Couldn't reach the Gaming Mode service: {ex.Message}"); }
        finally { _busy = false; _toggleItem.Enabled = true; }
    }

    private void ShowSettings(bool firstRun = false)
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _settings = new SettingsForm(firstRun);
        _settings.Show();
    }

    private static void OpenLog()
    {
        if (File.Exists(Paths.LogFile)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Paths.LogFile}\"") { UseShellExecute = true });
    }

    private void Balloon(string title, string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(5000, title, Trim(text, 250), ToolTipIcon.Info);
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _exitEvent?.Dispose();
        base.ExitThreadCore();
    }
}
