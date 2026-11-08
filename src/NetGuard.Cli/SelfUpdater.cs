using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using NetGuard.Core.Updates;

namespace NetGuard.Cli;

/// <summary>
/// Replaces ngctl.exe with the latest GitHub Release of this repository.
///
/// Windows can't overwrite a running exe, but it can rename it: we move ngctl.exe to ngctl.exe.old,
/// put the new file in its place, and delete the .old file on the next start.
/// Note: during a lock ngctl itself has no internet (it's not on any allowlist), so updating only
/// works with normal internet or in Maintenance – that's intentional.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SelfUpdater
{
    public sealed record Result(bool Updated, Version From, Version To, string Message);

    public static ReleaseInfo CheckLatest()
    {
        using var http = NewHttp();
        try { return GitHubReleases.GetLatestAsync(http).GetAwaiter().GetResult(); }
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

    public static Result Install(ReleaseInfo release)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find ngctl.exe's own path.");
        var dir = Path.GetDirectoryName(exe)!;
        var zipAsset = release.Asset(GitHubReleases.NgctlZip)
                       ?? throw new InvalidOperationException($"Release {release.Tag} has no {GitHubReleases.NgctlZip}.");
        var shaAsset = release.Asset(GitHubReleases.NgctlZip + ".sha256")
                       ?? throw new InvalidOperationException($"Release {release.Tag} has no checksum file – refusing to install.");

        var temp = Path.Combine(Path.GetTempPath(), "netguard-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            using var http = NewHttp();
            var zipPath = Path.Combine(temp, zipAsset.Name);
            Console.WriteLine($"  Downloading {zipAsset.Name} ({zipAsset.Size / 1024 / 1024} MB)…");
            using (var s = http.GetStreamAsync(zipAsset.DownloadUrl).GetAwaiter().GetResult())
            using (var f = File.Create(zipPath))
                s.CopyTo(f);

            // Integrity: the zip must match the checksum published with the release.
            var expected = GitHubReleases.ParseSha256File(http.GetStringAsync(shaAsset.DownloadUrl).GetAwaiter().GetResult());
            string actual;
            using (var f = File.OpenRead(zipPath)) actual = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
            if (actual != expected)
                throw new InvalidOperationException("Downloaded file is damaged (checksum mismatch). Nothing was changed.");
            Console.WriteLine("  Checksum OK.");

            var extract = Path.Combine(temp, "x");
            ZipFile.ExtractToDirectory(zipPath, extract);
            var newExe = Directory.EnumerateFiles(extract, "ngctl.exe", SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidOperationException("The update package doesn't contain ngctl.exe.");
            var newDir = Path.GetDirectoryName(newExe)!;
            using (var f = File.OpenRead(newExe))
                if (f.ReadByte() != 'M' || f.ReadByte() != 'Z')
                    throw new InvalidOperationException("The update's ngctl.exe isn't a Windows program. Nothing was changed.");

            // Swap the exe: rename the running one, move the new one in, roll back on failure.
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Copy(newExe, exe); }
            catch
            {
                File.Move(old, exe);
                throw;
            }

            // Docs (TESTING-*.md, RECOVERY.md …) next to the exe.
            foreach (var file in Directory.EnumerateFiles(newDir).Where(f => !f.EndsWith("ngctl.exe", StringComparison.OrdinalIgnoreCase)))
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);

            return new Result(true, GitHubReleases.CurrentVersion, release.Version, $"Updated to {release.Version}.");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>Remove the previous exe left behind by an update. Called at every start.</summary>
    public static void CleanupOldExe()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch { /* the old process may still be exiting; next start will retry */ }
    }

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetGuard-Updater");
        return http;
    }
}
