using System.Runtime.Versioning;
using System.Security.Cryptography;
using NetGuard.Core.Updates;

namespace NetGuard.Windows;

/// <summary>
/// Replaces the files in NetGuard's program folder with the latest GitHub Release of this repository.
///
/// Windows can't overwrite a running exe, but it can rename it: each file is moved to "*.old", the
/// new file is put in its place, and the .old files are deleted on the next start.
/// During a lock NetGuard itself has no internet (it's not on any allowlist), so updating only works
/// with normal internet or in Maintenance – that's intentional.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Updater
{
    public static string AppDir => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;

    public static async Task<ReleaseInfo> CheckLatestAsync()
    {
        using var http = NewHttp();
        try { return await GitHubReleases.GetLatestAsync(http); }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException(
                "Can't reach GitHub (" + e.Message + "). Updates only work with normal internet or in Maintenance mode.");
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("GitHub didn't answer in time. Updates only work with normal internet or in Maintenance mode.");
        }
    }

    /// <summary>
    /// Download the release's NetGuardSetup.exe, verify its checksum, and return its local path.
    /// Running it updates NetGuard properly (stops the service, replaces all files, starts it again).
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(ReleaseInfo release, Action<string>? progress = null)
    {
        var setup = release.Asset(GitHubReleases.SetupExe)
                    ?? throw new InvalidOperationException($"Release {release.Tag} has no {GitHubReleases.SetupExe}.");
        var sha = release.Asset(GitHubReleases.SetupExe + ".sha256")
                  ?? throw new InvalidOperationException($"Release {release.Tag} has no checksum for the installer – refusing to run it.");

        var dir = Path.Combine(Path.GetTempPath(), "NetGuardUpdate");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"NetGuardSetup-{release.Version}.exe");

        using var http = NewHttp();
        progress?.Invoke($"Downloading {setup.Name} ({setup.Size / 1024 / 1024} MB)…");
        await using (var s = await http.GetStreamAsync(setup.DownloadUrl))
        await using (var f = File.Create(path))
            await s.CopyToAsync(f);

        var expected = GitHubReleases.ParseSha256File(await http.GetStringAsync(sha.DownloadUrl));
        string actual;
        await using (var f = File.OpenRead(path)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(path);
            throw new InvalidOperationException("The downloaded installer is damaged (checksum mismatch). Nothing was changed.");
        }
        progress?.Invoke("Checksum OK.");
        return path;
    }

    /// <summary>Start the installer. Windows asks for administrator permission.</summary>
    public static void RunInstaller(string path, bool silent)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, silent ? "/SILENT /SUPPRESSMSGBOXES" : "")
        {
            UseShellExecute = true,
            Verb = "runas",
        });
    }

    /// <summary>Delete files left behind by an update. Called at every start.</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppDir, "*.old"))
                try { File.Delete(f); } catch { /* still running; next start retries */ }
        }
        catch { }
    }

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetGuard-Updater");
        return http;
    }
}
