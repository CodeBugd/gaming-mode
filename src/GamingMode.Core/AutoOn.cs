namespace GamingMode.Core;

/// <summary>Settings for turning Gaming Mode on by itself when a game starts.</summary>
public sealed class AutoOnSettings
{
    public bool Enabled { get; set; }
    /// <summary>Also count any app that covers the whole screen (borderless or fullscreen) as a game.</summary>
    public bool FullscreenApps { get; set; }
    /// <summary>How long after the last game closes before Gaming Mode turns itself off.</summary>
    public int OffDelaySeconds { get; set; } = 60;
    /// <summary>What counts as a game: process names (wildcards) or "path:" + part of the exe path.</summary>
    public List<string> Games { get; set; } = new();
    /// <summary>Never counts as a game, even inside a game folder or full screen (launchers, browsers, video players).</summary>
    public List<string> NotGames { get; set; } = new();

    public AutoOnSettings Normalize()
    {
        Games = Clean(Games);
        NotGames = Clean(NotGames);
        OffDelaySeconds = Math.Clamp(OffDelaySeconds, 0, 1800);
        return this;
    }

    private static List<string> Clean(List<string>? list) => (list ?? new()).Select(s => s.Trim()).Where(s => s.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static AutoOnSettings CreateDefault() => new()
    {
        Games = DefaultGames.ToList(),
        NotGames = DefaultNotGames.ToList(),
    };

    /// <summary>Game install folders. Launchers live next to some of these, so they're on the "not games" list.</summary>
    public static readonly string[] DefaultGames =
    {
        @"path:\steamapps\common\", @"path:\Epic Games\", @"path:\Riot Games\", @"path:\XboxGames\",
        @"path:\GOG Games\", @"path:\EA Games\", @"path:\Ubisoft Game Launcher\games\", @"path:\Rockstar Games\",
        @"path:\Battle.net\", @"path:\Blizzard", @"path:\Games\", @"path:\iRacing\",
    };

    public static readonly string[] DefaultNotGames =
    {
        // launchers, stores, updaters, crash reporters
        "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "epicwebhelper", "eadesktop", "eabackgroundservice",
        "upc", "ubisoftconnect", "battle.net", "agent", "riotclient*", "leagueclient*", "galaxyclient*", "xboxpcapp",
        "launcher*", "*launcher", "*updater*", "*crash*", "*reporter*", "unitycrashhandler*", "cefsharp*", "*webhelper*",
        "socialclubhelper", "rockstarservice", "playgtav", "easyanticheat*", "beservice", "battleye*",
        @"path:\Epic Games\Launcher\", @"path:\steamapps\common\Steamworks Shared\", @"path:\steamapps\common\SteamVR\",
        // things people watch full screen
        "msedge", "chrome", "firefox", "brave", "opera*", "vivaldi", "arc", "vlc", "mpc-hc*", "mpv", "potplayer*",
        "spotify", "netflix", "plex*", "kodi", "powerpnt", "explorer", "applicationframehost",
    };
}

/// <summary>Decides whether a process is a game, using the auto-on lists.</summary>
public sealed class GameDetector
{
    private readonly Protection _games, _notGames;
    private readonly bool _fullscreen;
    private readonly string _windowsDir;

    public GameDetector(AutoOnSettings settings)
    {
        _games = new Protection(settings.Games);
        _notGames = new Protection(settings.NotGames);
        _fullscreen = settings.FullscreenApps;
        _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
    }

    /// <summary>Full exe path of a process (works for other users' apps in your session without admin).</summary>
    public static string? ImagePath(int pid) => Native.GetImagePath(pid);

    public bool IsGame(string processName, string? imagePath, bool fullscreen)
    {
        if (string.IsNullOrEmpty(imagePath) || imagePath.StartsWith(_windowsDir, StringComparison.OrdinalIgnoreCase)) return false;
        if (_notGames.IsProtected(processName, imagePath)) return false;
        return _games.IsProtected(processName, imagePath) || (_fullscreen && fullscreen);
    }
}

public enum AutoAction { None, TurnOn, TurnOff }

/// <summary>
/// When to turn Gaming Mode on and off by itself. A game in front turns it on; it turns off a while after the
/// last game it saw has closed - but only if it was the one that turned it on. If you turn it off yourself while a
/// game is running, that game won't turn it back on.
/// </summary>
public sealed class AutoOnController
{
    /// <summary>A running process: PID plus start time, so a reused PID isn't mistaken for the game.</summary>
    public readonly record struct ProcessKey(int Pid, long StartTicks);

    private readonly HashSet<ProcessKey> _games = new();
    private readonly HashSet<ProcessKey> _declined = new();
    private DateTime? _lastGameSeenUtc;
    private bool _wasOn;
    private bool _expectChange;

    public string? CurrentGame { get; private set; }

    /// <param name="foregroundGame">The game in front right now, if any.</param>
    /// <param name="isAlive">Whether a process seen earlier is still running.</param>
    public AutoAction Update(DateTime nowUtc, bool isOn, bool autoStarted, ProcessKey? foregroundGame, string? gameName,
        Func<ProcessKey, bool> isAlive, TimeSpan offDelay)
    {
        // You switched it off yourself while games were running: leave those games alone from now on.
        if (_wasOn && !isOn && !_expectChange)
        {
            _declined.UnionWith(_games);
            _games.Clear();
        }
        _expectChange = false;
        _wasOn = isOn;

        _games.RemoveWhere(g => !isAlive(g));
        _declined.RemoveWhere(g => !isAlive(g));
        if (foregroundGame is ProcessKey fg && !_declined.Contains(fg))
        {
            _games.Add(fg);
            CurrentGame = gameName ?? CurrentGame;
        }
        if (_games.Count > 0) _lastGameSeenUtc = nowUtc;
        else CurrentGame = null;

        if (!isOn && _games.Count > 0)
        {
            _expectChange = true;
            return AutoAction.TurnOn;
        }
        if (isOn && autoStarted && _games.Count == 0 && _lastGameSeenUtc is DateTime last && nowUtc - last >= offDelay)
        {
            _lastGameSeenUtc = null;
            _expectChange = true;
            return AutoAction.TurnOff;
        }
        return AutoAction.None;
    }

    /// <summary>The action asked for didn't happen (service unreachable etc.), so a status change isn't ours.</summary>
    public void ActionFailed() => _expectChange = false;
}
