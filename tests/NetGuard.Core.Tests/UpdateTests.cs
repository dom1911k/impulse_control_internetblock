using NetGuard.Core.Updates;

namespace NetGuard.Core.Tests;

public class UpdateTests
{
    private const string Sample = """
    {
      "tag_name": "v0.1.12",
      "name": "NetGuard 0.1.12",
      "body": "Milestone 1 fixes",
      "html_url": "https://github.com/dom1911k/impulse_control_internetblock/releases/tag/v0.1.12",
      "published_at": "2026-11-08T12:43:05Z",
      "assets": [
        { "name": "NetGuard-ngctl-win-x64.zip", "browser_download_url": "https://example/zip", "size": 123 },
        { "name": "NetGuard-ngctl-win-x64.zip.sha256", "browser_download_url": "https://example/sha", "size": 90 }
      ]
    }
    """;

    [Fact]
    public void Parses_release_json()
    {
        var r = GitHubReleases.Parse(Sample);
        Assert.Equal(new Version(0, 1, 12), r.Version);
        Assert.Equal("https://example/zip", r.Asset(GitHubReleases.NgctlZip)!.DownloadUrl);
        Assert.NotNull(r.Asset(GitHubReleases.NgctlZip + ".sha256"));
        Assert.Equal("Milestone 1 fixes", r.Notes);
    }

    [Theory]
    [InlineData("v0.1.12", "0.1.12")]
    [InlineData("0.2.3", "0.2.3")]
    [InlineData("v1.0", "1.0.0")]
    public void Parses_tags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), GitHubReleases.ParseTag(tag));

    [Fact]
    public void Rejects_non_version_tags() => Assert.Null(GitHubReleases.ParseTag("latest"));

    [Fact]
    public void Newer_build_compares_greater_than_assembly_version()
    {
        var assembly = GitHubReleases.Normalize(new Version(0, 1, 9, 0));
        Assert.True(GitHubReleases.ParseTag("v0.1.10") > assembly);
        Assert.False(GitHubReleases.ParseTag("v0.1.9") > assembly);
    }

    [Fact]
    public void Parses_checksum_files()
    {
        var h = new string('a', 64);
        Assert.Equal(h, GitHubReleases.ParseSha256File(h.ToUpperInvariant() + "  NetGuard-ngctl-win-x64.zip\n"));
        Assert.Throws<InvalidDataException>(() => GitHubReleases.ParseSha256File("nope"));
    }
}
