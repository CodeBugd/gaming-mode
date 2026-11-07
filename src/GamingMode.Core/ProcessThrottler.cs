using System.Diagnostics;

namespace GamingMode.Core;

public sealed class PreviewItem
{
    public string Name { get; set; } = "";
    public string? Path { get; set; }
    public bool Protected { get; set; }
    public long WorkingSetBytes { get; set; }
}

/// <summary>
/// Pushes non-protected apps in one user session to the back (Idle priority + Efficiency mode + RAM trim),
/// keeps the app you're using at full speed, and remembers each app's original priority so it can be restored.
/// </summary>
public sealed class ProcessThrottler
{
    private enum Kind { Ignore, Protected, Target, Locked }

    private sealed class Entry
    {
        public int Id;
        public string Name = "";
        public Kind Kind;
        public long StartTicks;
        public ProcessPriorityClass Original;
        public bool Throttled, Touched, Trimmed, Exempt;
        public DateTime FirstSeenUtc;
        public double? CpuBaseMs;
        public double CpuLastMs;
        public int? OrigGpu, OrigMemory;   // only set once we've changed them
    }

    private readonly Dictionary<int, Entry> _procs = new();
    private readonly Dictionary<string, double> _cpuByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _sessionId;
    private readonly string _windowsDir;
    private readonly string _ownDir;
    private readonly int _ownPid = Environment.ProcessId;
    private Protection _protection;
    private readonly Settings _settings;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public long FreedBytes { get; private set; }

    public ProcessThrottler(int sessionId, Settings settings)
    {
        _sessionId = sessionId;
        _settings = settings;
        _protection = new Protection(settings.ProtectedApps);
        _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        _ownDir = AppContext.BaseDirectory.TrimEnd('\\') + "\\";
    }

    public int ThrottledCount => _procs.Values.Count(e => e.Throttled);
    public IReadOnlyList<string> ThrottledNames =>
        _procs.Values.Where(e => e.Throttled).Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();

    public List<ProcessRecord> Records => _procs.Values.Where(e => e.Touched)
        .Select(e => new ProcessRecord
        {
            Id = e.Id, Name = e.Name, StartTicks = e.StartTicks, Priority = e.Original.ToString(),
            GpuPriority = e.OrigGpu, MemoryPriority = e.OrigMemory,
        }).ToList();

    private Entry Classify(Process p)
    {
        if (_procs.TryGetValue(p.Id, out var existing) && existing.Name == p.ProcessName) return existing;
        var e = new Entry { Id = p.Id, Name = p.ProcessName, Kind = Kind.Ignore, FirstSeenUtc = DateTime.UtcNow };
        _procs[p.Id] = e;

        var path = Native.GetImagePath(p.Id);
        if (path == null || path.StartsWith(_windowsDir, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(_ownDir, StringComparison.OrdinalIgnoreCase)) return e;          // Windows itself, or us
        if (_protection.IsProtected(p.ProcessName, path)) { e.Kind = Kind.Protected; return e; }
        try
        {
            e.StartTicks = p.StartTime.ToUniversalTime().Ticks;
            e.Original = p.PriorityClass;
            e.Kind = Kind.Target;
        }
        catch { e.Kind = Kind.Locked; }   // can't read it, so we couldn't restore it either - leave it alone
        return e;
    }

    private void SetThrottle(Process p, Entry e, bool on)
    {
        try
        {
            if (on)
            {
                // Read the originals before changing anything.
                int? gpu = _settings.Features.LowerGpuPriority ? Native.GetGpuPriority(p.Id) : null;
                int? mem = _settings.Features.LowerMemoryPriority ? Native.GetMemoryPriority(p.Id) : null;

                p.PriorityClass = ProcessPriorityClass.Idle;
                Native.SetEfficiencyMode(p.Id, true);
                e.Throttled = e.Touched = true;
                if (e.CpuBaseMs is null) { e.CpuBaseMs = p.TotalProcessorTime.TotalMilliseconds; e.CpuLastMs = e.CpuBaseMs.Value; }
                if (gpu > Native.GpuPriorityBelowNormal && Native.SetGpuPriority(p.Id, Native.GpuPriorityBelowNormal))
                    e.OrigGpu ??= gpu;
                if (mem > Native.MemoryPriorityVeryLow && Native.SetMemoryPriority(p.Id, Native.MemoryPriorityVeryLow))
                    e.OrigMemory ??= mem;
                if (_settings.Features.TrimMemory && !e.Trimmed)
                {
                    long before = p.WorkingSet64;
                    Native.TrimWorkingSet(p.Id);
                    p.Refresh();
                    FreedBytes += Math.Max(0, before - p.WorkingSet64);
                    e.Trimmed = true;
                }
            }
            else
            {
                Native.SetEfficiencyMode(p.Id, false);
                if (p.PriorityClass == ProcessPriorityClass.Idle) p.PriorityClass = e.Original;
                RestoreGpuAndMemory(p.Id, e.OrigGpu, e.OrigMemory);
                e.Throttled = false;
            }
        }
        catch { e.Kind = Kind.Locked; }
    }

    /// <summary>One pass over the session's processes. Returns true when something new was throttled.</summary>
    public bool Scan(int foregroundPid, bool foregroundFullscreen, bool initial)
    {
        bool dirty = false;
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != _sessionId || p.Id == _ownPid) continue;
                    alive.Add(p.Id);
                    var e = Classify(p);
                    if (e.CpuBaseMs != null) { try { e.CpuLastMs = p.TotalProcessorTime.TotalMilliseconds; } catch { } }
                    if (e.Kind != Kind.Target || e.Exempt) continue;

                    if (p.Id == foregroundPid)
                    {
                        if (e.Throttled) SetThrottle(p, e, false);                 // what you're using runs at full speed
                        if (foregroundFullscreen)
                        {
                            e.Exempt = true;                                         // fullscreen = probably a game we didn't know
                            e.CpuBaseMs = null;                                      // ...so don't count it as background use
                            Log.Write($"Fullscreen app left at full speed for this session: {e.Name}");
                        }
                    }
                    else if (!e.Throttled && (initial || (DateTime.UtcNow - e.FirstSeenUtc).TotalSeconds >= _settings.GraceSeconds))
                    {
                        SetThrottle(p, e, true);
                        dirty = true;
                    }
                }
                catch { /* process exited mid-scan */ }
            }
        }
        foreach (var id in _procs.Keys.Where(id => !alive.Contains(id)).ToList())
        {
            var e = _procs[id];
            if (e.CpuBaseMs is double b) AddCpu(e.Name, e.CpuLastMs - b);    // closed - keep its CPU use for the report
            _procs.Remove(id);
        }
        return dirty;
    }

    /// <summary>Apply a new protected list while on: newly protected apps get their speed back.</summary>
    public void Reprotect(IEnumerable<string> protectedApps)
    {
        _protection = new Protection(protectedApps);
        foreach (var e in _procs.Values.ToList())
        {
            if (!e.Touched) { _procs.Remove(e.Id); continue; }              // re-classified on the next scan
            try
            {
                using var p = Process.GetProcessById(e.Id);
                if (_protection.IsProtected(e.Name, Native.GetImagePath(e.Id)))
                {
                    if (e.Throttled) SetThrottle(p, e, false);
                    e.Kind = Kind.Protected;
                }
                else e.Kind = Kind.Target;
            }
            catch { }
        }
    }

    private void AddCpu(string name, double ms)
    {
        if (ms <= 0) return;
        _cpuByName[name] = _cpuByName.GetValueOrDefault(name) + ms;
    }

    public string BuildReport()
    {
        foreach (var e in _procs.Values.Where(e => e.CpuBaseMs != null).ToList())
        {
            try { using var p = Process.GetProcessById(e.Id); e.CpuLastMs = p.TotalProcessorTime.TotalMilliseconds; } catch { }
            AddCpu(e.Name, e.CpuLastMs - e.CpuBaseMs!.Value);
            e.CpuBaseMs = null;
        }
        return FormatReport(_cpuByName, DateTime.UtcNow - _startedUtc, Environment.ProcessorCount);
    }

    /// <summary>"Pushed-back apps used 0.6% of your CPU over 45 min (top: msedge 0.5%, ...)." 100% = every core busy all session.</summary>
    internal static string FormatReport(IReadOnlyDictionary<string, double> cpuMsByName, TimeSpan elapsed, int cores)
    {
        double capacity = Math.Max(1, elapsed.TotalMilliseconds * Math.Max(1, cores));
        double total = cpuMsByName.Values.Sum();
        long mins = Math.Max(1, (long)Math.Round(elapsed.TotalMinutes));
        var line = $"Pushed-back apps used {100 * total / capacity:0.0}% of your CPU over {mins} min";
        var top = cpuMsByName.OrderByDescending(kv => kv.Value).Take(3)
            .Select(kv => $"{kv.Key} {100 * kv.Value / capacity:0.0}%").ToList();
        if (top.Count > 0) line += $" (top: {string.Join(", ", top)})";
        return line + ".";
    }

    /// <summary>What turning Gaming Mode on would do right now - changes nothing.</summary>
    public static List<PreviewItem> Preview(int sessionId, Settings settings)
    {
        var t = new ProcessThrottler(sessionId, settings);
        var items = new List<PreviewItem>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != sessionId || p.Id == t._ownPid) continue;
                    var e = t.Classify(p);
                    if (e.Kind is Kind.Target or Kind.Protected)
                        items.Add(new PreviewItem
                        {
                            Name = e.Name, Path = Native.GetImagePath(p.Id),
                            Protected = e.Kind == Kind.Protected, WorkingSetBytes = p.WorkingSet64,
                        });
                }
                catch { }
            }
        }
        return items.OrderBy(i => i.Protected).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Only puts a value back if it's still the one we set (so a change made by the app itself is kept).</summary>
    internal static void RestoreGpuAndMemory(int pid, int? gpu, int? memory)
    {
        if (gpu is int g && Native.GetGpuPriority(pid) == Native.GpuPriorityBelowNormal) Native.SetGpuPriority(pid, g);
        if (memory is int m && Native.GetMemoryPriority(pid) == Native.MemoryPriorityVeryLow) Native.SetMemoryPriority(pid, m);
    }

    /// <summary>Puts back every process recorded in a state file (used on turn-off and after a restart/crash).</summary>
    public static int RestoreProcesses(IEnumerable<ProcessRecord> records)
    {
        int restored = 0;
        foreach (var r in records)
        {
            try
            {
                using var p = Process.GetProcessById(r.Id);
                if (p.StartTime.ToUniversalTime().Ticks != r.StartTicks) continue;    // PID reused by another app
                Native.SetEfficiencyMode(p.Id, false);
                if (p.PriorityClass == ProcessPriorityClass.Idle && Enum.TryParse<ProcessPriorityClass>(r.Priority, out var original))
                    p.PriorityClass = original;                                         // don't undo a change you made yourself
                RestoreGpuAndMemory(p.Id, r.GpuPriority, r.MemoryPriority);
                restored++;
            }
            catch { /* gone */ }
        }
        return restored;
    }
}
