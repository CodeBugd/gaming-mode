using System.Text.Json;
using GamingMode.Core;
using Key = GamingMode.Core.AutoOnController.ProcessKey;

namespace GamingMode.Tests;

public class GameDetectorTests
{
    private static readonly GameDetector Defaults = new(AutoOnSettings.CreateDefault());
    private static readonly GameDetector WithFullscreen = new(new AutoOnSettings { FullscreenApps = true }.Normalize().Also(a =>
    {
        a.Games = AutoOnSettings.DefaultGames.ToList();
        a.NotGames = AutoOnSettings.DefaultNotGames.ToList();
    }));

    [Theory]
    [InlineData("arma3_x64", @"D:\SteamLibrary\steamapps\common\Arma 3\arma3_x64.exe")]
    [InlineData("FortniteClient-Win64-Shipping", @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe")]
    [InlineData("VALORANT-Win64-Shipping", @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe")]
    [InlineData("iRacingSim64DX11", @"C:\Program Files (x86)\iRacing\iRacingSim64DX11.exe")]
    public void Games_in_game_folders_count(string name, string path) => Assert.True(Defaults.IsGame(name, path, fullscreen: false));

    [Theory]
    [InlineData("steam", @"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData("EpicGamesLauncher", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe")]
    [InlineData("arma3launcher", @"D:\SteamLibrary\steamapps\common\Arma 3\arma3launcher.exe")]
    [InlineData("UnityCrashHandler64", @"D:\SteamLibrary\steamapps\common\Some Game\UnityCrashHandler64.exe")]
    [InlineData("RiotClientServices", @"C:\Riot Games\Riot Client\RiotClientServices.exe")]
    [InlineData("msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData("SomeGame", null)]                                              // path unreadable = don't guess
    [InlineData("notepad", @"C:\Windows\System32\notepad.exe")]
    public void Launchers_browsers_and_windows_dont_count(string name, string? path) => Assert.False(Defaults.IsGame(name, path, fullscreen: true));

    [Fact]
    public void Fullscreen_counts_only_when_switched_on_and_never_for_browsers()
    {
        const string path = @"C:\Tools\Emulator\emu.exe";
        Assert.False(Defaults.IsGame("emu", path, fullscreen: true));
        Assert.True(WithFullscreen.IsGame("emu", path, fullscreen: true));
        Assert.False(WithFullscreen.IsGame("emu", path, fullscreen: false));
        Assert.False(WithFullscreen.IsGame("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", fullscreen: true));
        Assert.False(WithFullscreen.IsGame("vlc", @"C:\Program Files\VideoLAN\VLC\vlc.exe", fullscreen: true));
    }

    [Fact]
    public void Old_settings_files_get_default_auto_on_lists_switched_off()
    {
        var s = JsonSerializer.Deserialize<Settings>("{\"protectedApps\":[\"x\"]}", Settings.Json)!.Normalize();
        Assert.NotNull(s.AutoOn);
        Assert.False(s.AutoOn!.Enabled);
        Assert.Equal(AutoOnSettings.DefaultGames.Length, s.AutoOn.Games.Count);
        Assert.Equal(60, s.AutoOn.OffDelaySeconds);
    }

    [Fact]
    public void Normalize_cleans_lists_and_clamps_delay()
    {
        var a = new AutoOnSettings { Games = { " x ", "", "X" }, OffDelaySeconds = 99999 }.Normalize();
        Assert.Equal(new[] { "x" }, a.Games);
        Assert.Equal(1800, a.OffDelaySeconds);
    }
}

public class AutoOnControllerTests
{
    private static readonly DateTime T0 = new(2026, 11, 27, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(60);
    private readonly HashSet<Key> _running = new();
    private readonly AutoOnController _c = new();

    private AutoAction Step(int sec, bool on, bool auto, Key? fg) =>
        _c.Update(T0.AddSeconds(sec), on, auto, fg, fg == null ? null : "game", k => _running.Contains(k), Delay);

    [Fact]
    public void Game_in_front_turns_on_then_off_after_the_delay()
    {
        var game = new Key(100, 1);
        _running.Add(game);
        Assert.Equal(AutoAction.TurnOn, Step(0, on: false, auto: false, game));
        Assert.Equal(AutoAction.None, Step(1, on: true, auto: true, null));     // alt-tabbed out, game still running
        Assert.Equal("game", _c.CurrentGame);
        _running.Clear();                                                     // game closed at ~2 s
        Assert.Equal(AutoAction.None, Step(30, on: true, auto: true, null));
        Assert.Equal(AutoAction.TurnOff, Step(62, on: true, auto: true, null));
        Assert.Equal(AutoAction.None, Step(63, on: false, auto: false, null));
    }

    [Fact]
    public void Turned_on_by_hand_stays_on_when_the_game_closes()
    {
        var game = new Key(100, 1);
        _running.Add(game);
        Assert.Equal(AutoAction.None, Step(0, on: true, auto: false, game));
        _running.Clear();
        Assert.Equal(AutoAction.None, Step(500, on: true, auto: false, null));
    }

    [Fact]
    public void Turned_off_by_hand_while_playing_isnt_turned_back_on_by_that_game()
    {
        var game = new Key(100, 1);
        _running.Add(game);
        Assert.Equal(AutoAction.TurnOn, Step(0, false, false, game));
        Assert.Equal(AutoAction.None, Step(1, true, true, game));
        Assert.Equal(AutoAction.None, Step(2, false, false, game));            // you switched it off
        Assert.Equal(AutoAction.None, Step(3, false, false, game));

        var next = new Key(200, 5);                                           // a different game later still works
        _running.Clear(); _running.Add(next);
        Assert.Equal(AutoAction.TurnOn, Step(10, false, false, next));
    }

    [Fact]
    public void Reused_pid_is_a_different_process()
    {
        var game = new Key(100, 1);
        _running.Add(game);
        Step(0, false, false, game);
        Step(1, true, true, game);
        _running.Clear(); _running.Add(new Key(100, 999));                    // same PID, new process
        Assert.Equal(AutoAction.TurnOff, Step(70, true, true, null));
    }

    [Fact]
    public void Failed_turn_on_isnt_mistaken_for_you_turning_it_off()
    {
        var game = new Key(100, 1);
        _running.Add(game);
        Assert.Equal(AutoAction.TurnOn, Step(0, false, false, game));
        _c.ActionFailed();
        Assert.Equal(AutoAction.TurnOn, Step(1, false, false, game));          // tries again
    }
}

public class StatsTests
{
    [Fact]
    public void Stats_round_trip_on_one_line()
    {
        var line = Ipc.Serialize(new IpcResponse
        {
            Stats = new EngineStats { IsOn = true, Apps = { new AppStat { Name = "msedge", CpuPercent = 1.5, RamMb = 300 } }, Changes = { "a" } },
        });
        Assert.DoesNotContain('\n', line);
        var back = Ipc.Deserialize<IpcResponse>(line)!.Stats!;
        Assert.Equal("msedge", back.Apps[0].Name);
        Assert.Equal(1.5, back.Apps[0].CpuPercent);
    }

    [Fact]
    public void Describe_lists_only_what_was_changed()
    {
        var cfg = Settings.CreateDefault();
        Assert.Empty(SystemTweaks.Describe(new SessionState(), cfg));
        var s = new SessionState
        {
            Services = { "WSearch" }, PowerPlan = Guid.NewGuid(), QosPolicies = { "AceBotGamingMode-OneDrive.exe" }, DefenderScanCpu = 50,
        };
        var lines = SystemTweaks.Describe(s, cfg);
        Assert.Contains("Paused: Windows Search indexing", lines);
        Assert.Contains(lines, l => l.StartsWith("Upload caps: OneDrive.exe 1 Mbps"));
        Assert.Equal(4, lines.Count);
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> act) { act(value); return value; }
}
