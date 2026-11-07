using System.Text.Json;
using GamingMode.Core;

namespace GamingMode.Tests;

public class ProtectionTests
{
    private static readonly Protection Defaults = new(Settings.DefaultProtectedApps);

    [Theory]
    [InlineData("Discord", null)]
    [InlineData("DiscordSystemHelper", null)]          // discord* wildcard
    [InlineData("obs64", null)]
    [InlineData("STEAM", null)]                        // case-insensitive
    [InlineData("SomeGame", @"D:\SteamLibrary\steamapps\common\SomeGame\SomeGame.exe")]
    [InlineData("crashpad_handler", @"C:\Program Files\Elgato\StreamDeck\crashpad_handler.exe")]
    [InlineData("QtWebEngineProcess", @"C:\Users\x\AppData\Local\VoicemodV3\app\last\QtWebEngineProcess.exe")]
    public void Protects_gaming_streaming_and_chat(string name, string? path) => Assert.True(Defaults.IsProtected(name, path));

    [Theory]
    [InlineData("msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData("obsidian", @"C:\Users\x\AppData\Local\Programs\Obsidian\Obsidian.exe")]   // "obs" must not match obsidian
    [InlineData("OneDrive", null)]
    public void Leaves_everything_else_unprotected(string name, string? path) => Assert.False(Defaults.IsProtected(name, path));

    [Fact]
    public void Exe_suffix_and_question_mark_wildcard_work()
    {
        var p = new Protection(new[] { "myapp.exe", "game?" });
        Assert.True(p.IsProtected("MyApp", null));
        Assert.True(p.IsProtected("game2", null));
        Assert.False(p.IsProtected("game22", null));
    }

    [Fact]
    public void Empty_path_entry_protects_nothing()
    {
        var p = new Protection(new[] { "path:", "  ", "# comment" });
        Assert.False(p.IsProtected("anything", @"C:\anything.exe"));
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_have_everything_switched_on()
    {
        var s = Settings.CreateDefault();
        Assert.All(typeof(FeatureSet).GetProperties(), p => Assert.True((bool)p.GetValue(s.Features)!, p.Name));
        Assert.Contains(s.UploadLimits, u => u.App == "OneDrive.exe" && u.Mbps == 1);
    }

    [Fact]
    public void Normalize_cleans_bad_input()
    {
        var s = new Settings
        {
            ProtectedApps = { " steam ", "STEAM", "", "discord" },
            UploadLimits = { new() { App = "a.exe", Mbps = 1 }, new() { App = "A.EXE", Mbps = 5 }, new() { App = "b.exe", Mbps = 0 } },
            ScanSeconds = 0, DefenderScanCpuPercent = 1, WindowsUpdatePauseDays = 99,
        }.Normalize();
        Assert.Equal(new[] { "steam", "discord" }, s.ProtectedApps);
        Assert.Single(s.UploadLimits);
        Assert.Equal(5, s.UploadLimits[0].Mbps);           // last one wins
        Assert.Equal(1, s.ScanSeconds);
        Assert.Equal(5, s.DefenderScanCpuPercent);
        Assert.Equal(7, s.WindowsUpdatePauseDays);
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gm-test-{Guid.NewGuid():N}.json");
        try
        {
            var s = Settings.CreateDefault();
            s.Features.PauseWindowsUpdate = false;
            s.ProtectedApps.Add("mytool");
            s.Save(path);
            var back = Settings.Load(path);
            Assert.False(back.Features.PauseWindowsUpdate);
            Assert.Contains("mytool", back.ProtectedApps);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_feature_keys_default_to_on()
    {
        var s = JsonSerializer.Deserialize<Settings>("{\"features\":{\"trimMemory\":false}}", Settings.Json)!.Normalize();
        Assert.False(s.Features.TrimMemory);
        Assert.True(s.Features.UploadLimits);
    }
}

public class SystemTweakTests
{
    [Theory]
    [InlineData("OneDrive.exe", true)]
    [InlineData("Google Drive (x86).exe", true)]
    [InlineData("evil'; Remove-Item C:\\ -Recurse #.exe", false)]
    [InlineData("C:\\path\\app.exe", false)]
    [InlineData("notanexe", false)]
    public void Only_plain_exe_names_reach_wmi(string name, bool ok) => Assert.Equal(ok, SystemTweaks.IsSafeExeName(name));

    [Fact]
    public void Windows_update_pause_respects_a_longer_user_pause()
    {
        var until = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(SystemTweaks.ShouldPause(null, until));
        Assert.True(SystemTweaks.ShouldPause("2026-11-08T00:00:00Z", until));     // shorter -> we extend it
        Assert.False(SystemTweaks.ShouldPause("2026-12-01T00:00:00Z", until));    // user's own longer pause -> leave it
        Assert.True(SystemTweaks.ShouldPause("garbage", until));
    }
}

public class ReportTests
{
    [Fact]
    public void Report_is_share_of_whole_cpu()
    {
        // 8 cores x 10 min = 4,800,000 ms capacity; 48,000 ms used = 1.0%
        var used = new Dictionary<string, double> { ["msedge"] = 36_000, ["claude"] = 12_000 };
        var text = ProcessThrottler.FormatReport(used, TimeSpan.FromMinutes(10), 8);
        Assert.Equal("Pushed-back apps used 1.0% of your CPU over 10 min (top: msedge 0.8%, claude 0.3%).", text);
    }

    [Fact]
    public void Report_with_nothing_throttled()
    {
        var text = ProcessThrottler.FormatReport(new Dictionary<string, double>(), TimeSpan.FromSeconds(20), 4);
        Assert.Equal("Pushed-back apps used 0.0% of your CPU over 1 min.", text);
    }
}

public class IpcTests
{
    [Fact]
    public void Requests_round_trip_as_json()
    {
        var req = new IpcRequest { Command = "foreground", ForegroundPid = 42, Fullscreen = true, Settings = Settings.CreateDefault() };
        var back = JsonSerializer.Deserialize<IpcRequest>(JsonSerializer.Serialize(req, Settings.Json), Settings.Json)!;
        Assert.Equal("foreground", back.Command);
        Assert.Equal(42, back.ForegroundPid);
        Assert.True(back.Fullscreen);
        Assert.Equal(req.Settings!.ProtectedApps.Count, back.Settings!.ProtectedApps.Count);
    }

    [Fact]
    public void Messages_are_always_a_single_line()
    {
        var withSettings = Ipc.Serialize(new IpcResponse { Settings = Settings.CreateDefault(), Status = new EngineStatus { ThrottledNames = { "a", "b" } } });
        Assert.DoesNotContain('\n', withSettings);
        Assert.Equal(Settings.CreateDefault().ProtectedApps.Count, Ipc.Deserialize<IpcResponse>(withSettings)!.Settings!.ProtectedApps.Count);
    }

    [Fact]
    public async Task Line_reader_handles_split_messages()
    {
        var ms = new MemoryStream();
        await Ipc.WriteLineAsync(ms, new string('x', 10_000), default);
        ms.Position = 0;
        Assert.Equal(10_000, (await Ipc.ReadLineAsync(ms, default))!.Length);
    }
}
