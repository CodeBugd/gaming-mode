using System.Text.Json;

namespace GamingMode.Core;

public sealed class ProcessRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public long StartTicks { get; set; }
    public string Priority { get; set; } = "Normal";
}

/// <summary>
/// Everything Gaming Mode changed, written to disk as it goes. If the PC restarts or the service
/// crashes while Gaming Mode is on, the service reads this at startup and puts it all back.
/// </summary>
public sealed class SessionState
{
    public DateTime EnabledAtUtc { get; set; }
    public int SessionId { get; set; }
    public string? UserSid { get; set; }
    public List<ProcessRecord> Processes { get; set; } = new();
    public List<string> Services { get; set; } = new();
    public Guid? PowerPlan { get; set; }
    public string? GameMode { get; set; }              // original value, or "missing"
    public Dictionary<string, string?>? WindowsUpdatePause { get; set; }
    public List<string> QosPolicies { get; set; } = new();
    public string? Maintenance { get; set; }
    public int? DefenderScanCpu { get; set; }
    public string? BackgroundRecording { get; set; }

    public const string Missing = "missing";

    public static SessionState? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), Settings.Json) : null; }
        catch (Exception ex) { Log.Write($"State file unreadable: {ex.Message}"); return null; }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Settings.Json));
        File.Move(tmp, path, overwrite: true);
    }
}
