using System.Reflection;
using System.Text.Json;

namespace NetGuard.Core.Updates;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(
    Version Version, string Tag, string Name, string Notes, DateTimeOffset? Published, string HtmlUrl, List<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Updates come only from this repository's GitHub Releases (built by the CI workflow).
/// No other download source is ever used.
/// </summary>
public static class GitHubReleases
{
    public const string Owner = "dom1911k";
    public const string Repo = "impulse_control_internetblock";
    /// <summary>The full package: NetGuard.exe (window app), ngctl.exe (console), docs.</summary>
    public const string PackageZip = "NetGuard-win-x64.zip";
    /// <summary>Name used by the first builds (ngctl only). Still published so those can update.</summary>
    public const string NgctlZip = "NetGuard-ngctl-win-x64.zip";
    public const string SetupExe = "NetGuardSetup.exe";
    public static string ReleasesPage => $"https://github.com/{Owner}/{Repo}/releases";
    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    public static async Task<ReleaseInfo> GetLatestAsync(HttpClient http, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
        req.Headers.UserAgent.ParseAdd("NetGuard-Updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("No release has been published yet.");
        resp.EnsureSuccessStatusCode();
        return Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    public static ReleaseInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        var version = ParseTag(tag) ?? throw new InvalidDataException($"Release tag '{tag}' is not a version.");
        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var arr))
            foreach (var a in arr.EnumerateArray())
                assets.Add(new ReleaseAsset(
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.TryGetProperty("size", out var s) ? s.GetInt64() : 0));
        return new ReleaseInfo(
            version, tag,
            Str(r, "name") ?? tag,
            Str(r, "body") ?? "",
            r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : null,
            Str(r, "html_url") ?? ReleasesPage,
            assets);
    }

    /// <summary>"v0.1.12" → 0.1.12. Returns null for anything that isn't a version.</summary>
    public static Version? ParseTag(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out var v) ? Normalize(v) : null;
    }

    /// <summary>Compare as major.minor.build (ignore the 4th "revision" part assemblies carry).</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    public static Version CurrentVersion =>
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Reads the hex hash from a "*.sha256" file ("abc123…  file.zip" or just "abc123…").</summary>
    public static string ParseSha256File(string text)
    {
        var token = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new InvalidDataException("The checksum file is malformed.");
        return token.ToLowerInvariant();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
