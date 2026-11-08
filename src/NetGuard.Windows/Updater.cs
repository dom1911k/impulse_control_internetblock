using System.IO.Compression;
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

    /// <summary>Download, verify and install. Returns the names of the files replaced.</summary>
    public static async Task<List<string>> InstallAsync(ReleaseInfo release, Action<string>? progress = null)
    {
        var zipAsset = release.Asset(GitHubReleases.PackageZip) ?? release.Asset(GitHubReleases.NgctlZip)
                       ?? throw new InvalidOperationException($"Release {release.Tag} has no download package.");
        var shaAsset = release.Asset(zipAsset.Name + ".sha256")
                       ?? throw new InvalidOperationException($"Release {release.Tag} has no checksum file – refusing to install.");

        var temp = Path.Combine(Path.GetTempPath(), "netguard-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            using var http = NewHttp();
            var zipPath = Path.Combine(temp, zipAsset.Name);
            progress?.Invoke($"Downloading {zipAsset.Name} ({zipAsset.Size / 1024 / 1024} MB)…");
            await using (var s = await http.GetStreamAsync(zipAsset.DownloadUrl))
            await using (var f = File.Create(zipPath))
                await s.CopyToAsync(f);

            // Integrity: the zip must match the checksum published with the release.
            var expected = GitHubReleases.ParseSha256File(await http.GetStringAsync(shaAsset.DownloadUrl));
            string actual;
            await using (var f = File.OpenRead(zipPath)) actual = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
            if (actual != expected)
                throw new InvalidOperationException("Downloaded file is damaged (checksum mismatch). Nothing was changed.");
            progress?.Invoke("Checksum OK.");

            var extract = Path.Combine(temp, "x");
            ZipFile.ExtractToDirectory(zipPath, extract);
            var files = Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories).ToList();
            if (!files.Any(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The update package contains no program. Nothing was changed.");
            foreach (var exe in files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                await using (var f = File.OpenRead(exe))
                    if (f.ReadByte() != 'M' || f.ReadByte() != 'Z')
                        throw new InvalidOperationException($"{Path.GetFileName(exe)} in the update isn't a Windows program. Nothing was changed.");

            return Swap(files);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>Move each existing file aside (*.old) and copy the new one in. Rolls everything back on failure.</summary>
    private static List<string> Swap(List<string> newFiles)
    {
        var dir = AppDir;
        var done = new List<(string target, string? old)>();
        try
        {
            foreach (var src in newFiles)
            {
                var target = Path.Combine(dir, Path.GetFileName(src));
                string? old = null;
                if (File.Exists(target))
                {
                    old = target + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                }
                done.Add((target, old));
                File.Copy(src, target);
            }
            return done.Select(d => Path.GetFileName(d.target)).ToList();
        }
        catch
        {
            foreach (var (target, old) in Enumerable.Reverse(done))
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (old != null && File.Exists(old)) File.Move(old, target);
                }
                catch { }
            }
            throw;
        }
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
