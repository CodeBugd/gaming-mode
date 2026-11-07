using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamingMode.Core;

public sealed class UploadLimit
{
    public string App { get; set; } = "";
    public double Mbps { get; set; }
}

/// <summary>Each part of Gaming Mode can be switched off on its own.</summary>
public sealed class FeatureSet
{
    public bool ThrottleApps { get; set; } = true;
    public bool TrimMemory { get; set; } = true;
    public bool PauseServices { get; set; } = true;
    public bool PauseWindowsUpdate { get; set; } = true;
    public bool UploadLimits { get; set; } = true;
    public bool HoldMaintenance { get; set; } = true;
    public bool LimitDefenderScans { get; set; } = true;
    public bool StopBackgroundRecording { get; set; } = true;
    public bool HighPerformancePower { get; set; } = true;
    public bool WindowsGameMode { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
}

public sealed class Settings
{
    /// <summary>Never slowed down. A process name (no .exe, * and ? wildcards) or "path:" + text found in the exe path.</summary>
    public List<string> ProtectedApps { get; set; } = new();
    public List<UploadLimit> UploadLimits { get; set; } = new();
    public FeatureSet Features { get; set; } = new();
    public int ScanSeconds { get; set; } = 3;
    public int GraceSeconds { get; set; } = 10;
    public int DefenderScanCpuPercent { get; set; } = 5;
    public int WindowsUpdatePauseDays { get; set; } = 2;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public Settings Normalize()
    {
        ProtectedApps = ProtectedApps.Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        UploadLimits = UploadLimits.Where(u => !string.IsNullOrWhiteSpace(u.App) && u.Mbps > 0)
            .Select(u => new UploadLimit { App = u.App.Trim(), Mbps = Math.Min(u.Mbps, 10_000) })
            .GroupBy(u => u.App, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
        Features ??= new FeatureSet();
        ScanSeconds = Math.Clamp(ScanSeconds, 1, 60);
        GraceSeconds = Math.Clamp(GraceSeconds, 0, 120);
        DefenderScanCpuPercent = Math.Clamp(DefenderScanCpuPercent, 5, 100);
        WindowsUpdatePauseDays = Math.Clamp(WindowsUpdatePauseDays, 1, 7);
        return this;
    }

    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? CreateDefault()).Normalize();
        }
        catch (Exception ex) { Log.Write($"Settings unreadable, using defaults: {ex.Message}"); }
        return CreateDefault();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Normalize(), Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static Settings CreateDefault() => new Settings
    {
        ProtectedApps = DefaultProtectedApps.ToList(),
        UploadLimits = DefaultUploadLimits.Select(t => new UploadLimit { App = t.App, Mbps = t.Mbps }).ToList(),
    }.Normalize();

    public static readonly (string App, double Mbps)[] DefaultUploadLimits =
    {
        // cloud sync - big uploads that kill a stream
        ("OneDrive.exe", 1), ("Dropbox.exe", 1), ("GoogleDriveFS.exe", 1), ("iCloudDrive.exe", 1), ("MEGAsync.exe", 1),
        // browsers - plenty for chat and dashboards, stops big file uploads
        ("msedge.exe", 3), ("chrome.exe", 3), ("firefox.exe", 3), ("brave.exe", 3), ("opera.exe", 3),
        // torrents
        ("qbittorrent.exe", 1), ("utorrent.exe", 1),
    };

    public static readonly string[] DefaultProtectedApps =
    {
        // game folders - covers every game installed in them
        @"path:\steamapps\", @"path:\Epic Games\", @"path:\Riot Games\", @"path:\XboxGames\", @"path:\GOG Games\",
        @"path:\GOG Galaxy\", @"path:\EA Games\", @"path:\Electronic Arts\", @"path:\Ubisoft\", @"path:\Battle.net\",
        @"path:\Blizzard", @"path:\Rockstar Games\", @"path:\Games\", @"path:\iRacing\", @"path:\SimHub\", @"path:\AceBot\ApexRadio\",
        // launchers / stores
        "steam", "steamwebhelper", "steamservice", "gameoverlayui*", "epicgameslauncher", "epicwebhelper", "eadesktop",
        "eabackgroundservice", "origin*", "upc", "ubisoftconnect", "uplay*", "battle.net", "riotclient*", "leagueclient*",
        "galaxyclient*", "xboxpcapp", "gamingservices*", "xboxappservices", "gamebar*", "playnite.*", "socialclubhelper",
        "rockstarservice", "amazon games*",
        // anti-cheat - throttling these can get you kicked
        "easyanticheat*", "beservice", "vgc", "vgtray", "faceit*", "eaanticheat*",
        // voice chat
        "discord*", "teamspeak", "ts3client*", "mumble", "guilded",
        // streaming / recording / audio
        "obs", "obs64", "obs32", "obs-browser-page", "obs-ffmpeg-mux", "streamlabs*", "xsplit*", "twitchstudio*", "meld*",
        "prismlivestudio", "nvidia broadcast*", "streamdeck", "camera hub", "wavelink*", "voicemeeter*", "voicemod*",
        "streamer.bot", "mixitup*", "touch portal*", "vtube studio", "vseeface", "veadotube*", "beacn*", "goxlr*",
        @"path:\obs-studio\", @"path:\Streamlabs", @"path:\Elgato\", @"path:\BEACN\", @"path:\Voicemod", @"path:\TC-Helicon\", @"path:\Rode\",
        // GPU, overlays, peripherals
        "nvcontainer", "nvdisplay.container", "nvidia share", "nvidia overlay", "nvidia app", "nvsphelper64", "nvidia web helper",
        "radeonsoftware", "amdrsserv", "amdow", "amddvr", "rtss*", "msiafterburner", "lghub*", "icue*", "corsair*", "razer*",
        "rzsynapse", "steelseries*", "armourycrate*", "nzxt cam", "cam_helper", @"path:\NZXT", "signalrgb", "openrgb", "hyperx*",
        // sim racing
        "simhub*", "crewchief*", "iracing*", "acc", "ac2-win64-shipping", "acs", "content manager", "ams2*", "le mans ultimate",
        "lmu", "rrre*", "rfactor2*", "beamng*", "fanatecapp", "pit house", "moza*", "simucube*", "trading paints*", "garage61*", "racelab*",
        // VR
        "vrserver", "vrmonitor", "vrcompositor", "vrdashboard", "vrwebhelper", "oculus*", "ovr*", "virtual desktop*",
    };
}
