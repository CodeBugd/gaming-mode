using GamingMode.Core;

namespace GamingMode.Tests;

public class UpdaterTests
{
    private static string Url(string file) => $"https://github.com/{Updater.Repo}/releases/download/v1.2.0/{file}";

    private static string Release(string msiName, string? msiUrl = null, bool prerelease = false, bool withSha = true) => $$"""
        {
          "tag_name": "v1.2.0", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "{{msiName}}", "browser_download_url": "{{msiUrl ?? Url(msiName)}}" }
            {{(withSha ? $", {{ \"name\": \"{msiName}.sha256\", \"browser_download_url\": \"{Url(msiName + ".sha256")}\" }}" : "")}}
          ]
        }
        """;

    [Fact]
    public void Reads_version_and_assets_from_a_release()
    {
        var info = Updater.ParseRelease(Release("GamingMode-1.2.0-x64.msi"))!;
        Assert.Equal(new Version(1, 2, 0), info.Version);
        Assert.EndsWith("GamingMode-1.2.0-x64.msi", info.MsiUrl);
        Assert.EndsWith(".msi.sha256", info.ShaUrl);
    }

    [Fact]
    public void Ignores_prereleases_and_releases_without_a_checksum()
    {
        Assert.Null(Updater.ParseRelease(Release("GamingMode-1.2.0-x64.msi", prerelease: true)));
        Assert.Null(Updater.ParseRelease(Release("GamingMode-1.2.0-x64.msi", withSha: false)));
        Assert.Null(Updater.ParseRelease(Release("SomethingElse.msi")));
    }

    [Fact]
    public void Only_downloads_from_this_repos_releases()
    {
        Assert.Null(Updater.ParseRelease(Release("GamingMode-1.2.0-x64.msi", msiUrl: "https://evil.example/GamingMode-1.2.0-x64.msi")));
        Assert.False(Updater.IsGitHubUrl("http://github.com/" + Updater.Repo + "/releases/download/v1/x.msi"));      // not https
        Assert.False(Updater.IsGitHubUrl("https://github.com/someone/else/releases/download/v1/x.msi"));            // other repo
        Assert.True(Updater.IsGitHubUrl(Url("x.msi")));
    }

    [Theory]
    [InlineData("3F2A0000000000000000000000000000000000000000000000000000000000AB  GamingMode-1.2.0-x64.msi", "3f2a0000000000000000000000000000000000000000000000000000000000ab")]
    [InlineData("3f2a0000000000000000000000000000000000000000000000000000000000ab\n", "3f2a0000000000000000000000000000000000000000000000000000000000ab")]
    [InlineData("not a hash", null)]
    public void Reads_checksum_files(string text, string? expected) => Assert.Equal(expected, Updater.ParseSha(text));
}
