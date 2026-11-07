using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GamingMode.Core;

public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0);
    public string MsiUrl { get; init; } = "";
    public string ShaUrl { get; init; } = "";
    public string Tag { get; init; } = "";
}

/// <summary>
/// Checks GitHub Releases for a newer version and installs it silently (runs inside the service).
/// The MSI is downloaded into SYSTEM's own temp folder (only SYSTEM and admins can write there),
/// must match the SHA-256 published with the release, and is only installed while Gaming Mode is off.
/// </summary>
public sealed class Updater : IDisposable
{
    /// <summary>owner/repo on GitHub. Releases come from github.com/{Repo}/releases.</summary>
    public const string Repo = "CodeBugd/gaming-mode";
    public const string RelaunchTrayFlag = "relaunch-tray";

    private static readonly Regex MsiName = new(@"^GamingMode-(\d+\.\d+\.\d+)-x64\.msi$", RegexOptions.IgnoreCase);
    private readonly HttpClient _http;
    private readonly Func<bool> _isGamingModeOn;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private Timer? _timer;

    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new(0, 0, 0);
    public string LastResult { get; private set; } = "Not checked yet.";

    public Updater(Func<bool> isGamingModeOn, HttpMessageHandler? handler = null)
    {
        _isGamingModeOn = isGamingModeOn;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true) { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AceBot-GamingMode", Current.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>First check a couple of minutes after startup, then every 12 hours.</summary>
    public void StartSchedule(Func<bool> enabled) =>
        _timer = new Timer(_ => { if (enabled()) _ = CheckAndInstallAsync(manual: false); }, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(12));

    /// <summary>Parses GitHub's "latest release" JSON. Returns null when there's no usable installer in it.</summary>
    internal static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        string? msi = null, sha = null;
        Version? version = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var url = asset.GetProperty("browser_download_url").GetString() ?? "";
            var m = MsiName.Match(name);
            if (m.Success) { msi = url; version = Version.Parse(m.Groups[1].Value); }
            else if (name.EndsWith(".msi.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (msi == null || sha == null || version == null) return null;
        if (!IsGitHubUrl(msi) || !IsGitHubUrl(sha)) return null;
        return new UpdateInfo { Version = version, MsiUrl = msi, ShaUrl = sha, Tag = tag };
    }

    internal static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        u.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"3f2a…  GamingMode-1.1.0-x64.msi" or just the hash.</summary>
    internal static string? ParseSha(string text)
    {
        var m = Regex.Match(text, @"\b([0-9a-fA-F]{64})\b");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    public async Task<string> CheckAndInstallAsync(bool manual)
    {
        if (!await _busy.WaitAsync(0).ConfigureAwait(false)) return LastResult = "Already checking for updates.";
        try
        {
            var json = await _http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(false);
            var info = ParseRelease(json);
            if (info == null) return Done("No installer found in the latest release.");
            if (info.Version <= Current) return Done($"Up to date (v{Current}).");

            // Never update in the middle of a gaming session - try again when it's off.
            for (int waited = 0; _isGamingModeOn(); waited++)
            {
                if (waited == 0) Log.Write($"Update v{info.Version} found - waiting until Gaming Mode is off.");
                await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            }

            var expected = ParseSha(await _http.GetStringAsync(info.ShaUrl).ConfigureAwait(false))
                           ?? throw new InvalidDataException("The release's checksum file is unreadable.");
            var msiPath = Path.Combine(Path.GetTempPath(), $"AceBotGamingMode-{info.Version}-{Guid.NewGuid():N}.msi");
            await using (var file = new FileStream(msiPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var body = await _http.GetStreamAsync(info.MsiUrl).ConfigureAwait(false))
                await body.CopyToAsync(file).ConfigureAwait(false);

            string actual;
            await using (var check = new FileStream(msiPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check).ConfigureAwait(false)).ToLowerInvariant();
            if (actual != expected)
            {
                File.Delete(msiPath);
                return Done($"Update v{info.Version} rejected: download didn't match its checksum.");
            }

            Log.Write($"Installing update v{Current} -> v{info.Version}.");
            File.WriteAllText(Path.Combine(Paths.DataDir, RelaunchTrayFlag), info.Version.ToString());
            // msiexec stops this service, swaps the files and starts the new version, which brings the tray icon back.
            Process.Start(new ProcessStartInfo("msiexec.exe",
                $"/i \"{msiPath}\" /qn /norestart /l*v \"{Path.Combine(Paths.DataDir, "update-install.log")}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            return Done($"Installing v{info.Version}...");
        }
        catch (Exception ex)
        {
            return Done($"Update check failed: {ex.Message}", log: true);
        }
        finally { _busy.Release(); }

        string Done(string result, bool log = false)
        {
            if (log || manual || !result.StartsWith("Up to date", StringComparison.Ordinal)) Log.Write(result);
            return LastResult = result;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }
}
