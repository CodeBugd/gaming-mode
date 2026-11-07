namespace GamingMode.Core;

public sealed class EngineStatus
{
    public bool IsOn { get; set; }
    public int ThrottledCount { get; set; }
    public double FreedMb { get; set; }
    public DateTime? EnabledAtUtc { get; set; }
    public List<string> ThrottledNames { get; set; } = new();
    /// <summary>Changes every time Gaming Mode turns off, so the tray knows a new report is ready.</summary>
    public int OffCount { get; set; }
    public string? LastReport { get; set; }
    public string Version { get; set; } = Updater.Current.ToString();
}

/// <summary>Runs inside the service. All public methods are thread-safe.</summary>
public sealed class GamingEngine : IDisposable
{
    private readonly object _gate = new();
    private Settings _settings;
    private SessionState? _state;
    private ProcessThrottler? _throttler;
    private Timer? _timer;
    private int _fgPid;
    private bool _fgFullscreen;
    private DateTime _fgUpdatedUtc;
    private int _offCount;
    private string? _lastReport;

    public GamingEngine() => _settings = Settings.Load(Paths.SettingsFile);

    public bool IsOn { get { lock (_gate) return _state != null; } }
    public int? ActiveSessionId { get { lock (_gate) return _state?.SessionId; } }

    /// <summary>Called when the service starts: anything left over from a restart or crash gets put back.</summary>
    public void StartupCleanup()
    {
        lock (_gate)
        {
            if (!File.Exists(Paths.SettingsFile)) Try(() => _settings.Save(Paths.SettingsFile));
            var leftover = SessionState.Load(Paths.StateFile);
            if (leftover != null)
            {
                Log.Write("Found leftovers from last time (restart or crash) - putting everything back.");
                RestoreAll(leftover);
            }
            Try(SystemTweaks.RemoveAllOurQosPolicies);
        }
    }

    public EngineStatus TurnOn(int sessionId, string? userSid)
    {
        lock (_gate)
        {
            if (_state != null) return StatusLocked();
            _settings = Settings.Load(Paths.SettingsFile);
            _state = new SessionState { EnabledAtUtc = DateTime.UtcNow, SessionId = sessionId, UserSid = userSid };
            _state.Save(Paths.StateFile);                       // written first, so a crash mid-way still gets cleaned up
            SystemTweaks.Apply(_state, _settings);
            _state.Save(Paths.StateFile);
            if (_settings.Features.ThrottleApps)
            {
                _throttler = new ProcessThrottler(sessionId, _settings);
                _throttler.Scan(CurrentForeground(), _fgFullscreen, initial: true);
                SaveStateLocked();
                Log.Write($"ON. Pushed back: {string.Join(", ", _throttler.ThrottledNames)}");
            }
            else Log.Write("ON (app throttling switched off in settings).");
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(_settings.ScanSeconds), TimeSpan.FromSeconds(_settings.ScanSeconds));
            return StatusLocked();
        }
    }

    public EngineStatus TurnOff()
    {
        lock (_gate)
        {
            if (_state == null) return StatusLocked();
            _timer?.Dispose(); _timer = null;
            string report = "";
            try { report = _throttler?.BuildReport() ?? ""; } catch (Exception ex) { Log.Write($"Report error: {ex.Message}"); }
            SaveStateLocked();
            int restored = RestoreAll(_state);
            _state = null; _throttler = null;
            _offCount++;
            _lastReport = $"All back to normal ({restored} apps restored). {report}".Trim();
            Log.Write($"OFF. {report}");
            return StatusLocked();
        }
    }

    public EngineStatus Toggle(int sessionId, string? userSid) => IsOn ? TurnOff() : TurnOn(sessionId, userSid);

    /// <summary>The tray app reports which window you're using (the service can't see your desktop).</summary>
    public EngineStatus ReportForeground(int pid, bool fullscreen)
    {
        lock (_gate)
        {
            _fgPid = pid; _fgFullscreen = fullscreen; _fgUpdatedUtc = DateTime.UtcNow;
            return StatusLocked();
        }
    }

    public EngineStatus Status() { lock (_gate) return StatusLocked(); }

    public Settings GetSettings() { lock (_gate) return Settings.Load(Paths.SettingsFile); }

    public bool AutoUpdateEnabled { get { lock (_gate) return Settings.Load(Paths.SettingsFile).Features.AutoUpdate; } }

    public void SaveSettings(Settings settings)
    {
        lock (_gate)
        {
            settings.Normalize().Save(Paths.SettingsFile);
            _settings = settings;
            if (_throttler != null) { _throttler.Reprotect(settings.ProtectedApps); SaveStateLocked(); }
            Log.Write("Settings saved.");
        }
    }

    /// <summary>What turning on would slow down right now. Pass unsaved settings to preview edits.</summary>
    public List<PreviewItem> Preview(int sessionId, Settings? settings = null)
    {
        lock (_gate) return ProcessThrottler.Preview(sessionId, settings?.Normalize() ?? Settings.Load(Paths.SettingsFile));
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_throttler == null) return;
            try { if (_throttler.Scan(CurrentForeground(), _fgFullscreen, initial: false)) SaveStateLocked(); }
            catch (Exception ex) { Log.Write($"Scan error: {ex.Message}"); }
        }
    }

    private int CurrentForeground() =>
        (DateTime.UtcNow - _fgUpdatedUtc).TotalSeconds < Math.Max(6, _settings.ScanSeconds * 3) ? _fgPid : 0;

    private void SaveStateLocked()
    {
        if (_state == null) return;
        if (_throttler != null) _state.Processes = _throttler.Records;
        Try(() => _state.Save(Paths.StateFile));
    }

    private static int RestoreAll(SessionState s)
    {
        int restored = ProcessThrottler.RestoreProcesses(s.Processes);
        SystemTweaks.Restore(s);
        Try(() => File.Delete(Paths.StateFile));
        Log.Write($"Restored {restored} app(s) and system settings.");
        return restored;
    }

    private EngineStatus StatusLocked() => new()
    {
        IsOn = _state != null,
        EnabledAtUtc = _state?.EnabledAtUtc,
        ThrottledCount = _throttler?.ThrottledCount ?? 0,
        ThrottledNames = _throttler?.ThrottledNames.ToList() ?? new(),
        FreedMb = Math.Round((_throttler?.FreedBytes ?? 0) / 1048576.0, 1),
        OffCount = _offCount,
        LastReport = _lastReport,
    };

    private static void Try(Action a)
    {
        try { a(); } catch (Exception ex) { Log.Write(ex.Message); }
    }

    public void Dispose()
    {
        if (IsOn) TurnOff();
    }
}
