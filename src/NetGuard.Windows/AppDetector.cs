using System.Runtime.Versioning;
using Microsoft.Win32;
using NetGuard.Core.Config;

namespace NetGuard.Windows;

/// <summary>Finds WoW, Battle.net, Agent, Spotify and Steam on this PC (registry + common folders).</summary>
[SupportedOSPlatform("windows")]
public static class AppDetector
{
    public static DetectedPaths Detect()
    {
        var d = new DetectedPaths();

        // ---- World of Warcraft (retail + classic share one root with _retail_, _classic_, _classic_era_ …)
        var candidates = new List<string?>
        {
            ReadHklm(@"SOFTWARE\Blizzard Entertainment\World of Warcraft", "InstallPath", RegistryView.Registry32),
            ReadHklm(@"SOFTWARE\Blizzard Entertainment\World of Warcraft", "GamePath", RegistryView.Registry32),
            ReadHklm(@"SOFTWARE\Blizzard Entertainment\World of Warcraft", "InstallPath", RegistryView.Registry64),
        };
        candidates.AddRange(UninstallLocations("World of Warcraft"));
        foreach (var drive in FixedDrives())
        {
            foreach (var rel in new[]
                     {
                         "World of Warcraft", @"Games\World of Warcraft", @"Program Files (x86)\World of Warcraft",
                         @"Program Files\World of Warcraft", @"Battle.net\World of Warcraft", @"Blizzard\World of Warcraft",
                         @"Games\Blizzard\World of Warcraft", @"Program Files (x86)\Blizzard\World of Warcraft",
                     })
                candidates.Add(Path.Combine(drive, rel));
        }
        foreach (var c in candidates)
        {
            var root = NormalizeWowRoot(c);
            if (root != null && !d.WowRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) d.WowRoots.Add(root);
        }
        if (d.WowRoots.Count == 0) d.Warnings.Add("World of Warcraft was not found. Edit the WoW paths in config.json.");

        // ---- Battle.net app + Agent
        var bnet = UninstallLocations("Battle.net").Select(Clean).FirstOrDefault(Directory.Exists)
                   ?? Existing(Path.Combine(ProgramFilesX86(), "Battle.net"));
        d.BattleNetDir = bnet;
        if (bnet == null) d.Warnings.Add("Battle.net app was not found.");
        d.BattleNetAgentDir = Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent"));
        if (d.BattleNetAgentDir == null) d.Warnings.Add("Blizzard Agent folder (C:\\ProgramData\\Battle.net\\Agent) was not found.");

        // ---- Spotify (desktop version lives in each user's AppData\Roaming)
        var usersDir = ReadHklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", "ProfilesDirectory", RegistryView.Registry64);
        d.UsersDir = usersDir != null ? Environment.ExpandEnvironmentVariables(usersDir) : Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users");
        foreach (var user in SafeDirs(d.UsersDir))
        {
            var exe = Path.Combine(user, "AppData", "Roaming", "Spotify", "Spotify.exe");
            if (File.Exists(exe)) d.SpotifyExes.Add(exe);
            foreach (var pkg in SafeDirs(Path.Combine(user, "AppData", "Local", "Packages"), "SpotifyAB.SpotifyMusic_*"))
                d.SpotifyStoreInstalls.Add(pkg);
        }
        if (d.SpotifyStoreInstalls.Count > 0)
            d.Warnings.Add("The Microsoft Store version of Spotify is installed. Its path changes with every update, so NetGuard cannot " +
                           "allow it reliably. Please uninstall it and install the desktop version from spotify.com/download.");
        if (d.SpotifyExes.Count == 0) d.Warnings.Add("Desktop Spotify (AppData\\Roaming\\Spotify\\Spotify.exe) was not found for any user.");

        // ---- Steam (to block)
        var steam = new List<string?>
        {
            ReadHklm(@"SOFTWARE\Valve\Steam", "InstallPath", RegistryView.Registry32),
            ReadHklm(@"SOFTWARE\Valve\Steam", "InstallPath", RegistryView.Registry64),
            Path.Combine(ProgramFilesX86(), "Steam"),
        };
        foreach (var sid in Registry.Users.GetSubKeyNames())
        {
            try
            {
                using var k = Registry.Users.OpenSubKey(sid + @"\Software\Valve\Steam");
                steam.Add(k?.GetValue("SteamPath") as string);
            }
            catch { }
        }
        foreach (var s in steam.Select(Clean).Where(s => s != null && Directory.Exists(s)))
            if (!d.SteamRoots.Contains(s!, StringComparer.OrdinalIgnoreCase)) d.SteamRoots.Add(Path.GetFullPath(s!));

        return d;
    }

    private static string? NormalizeWowRoot(string? path)
    {
        path = Clean(path);
        if (path == null) return null;
        if (File.Exists(path)) path = Path.GetDirectoryName(path);
        // Walk up out of _retail_ / _classic_ folders
        while (path != null && Path.GetFileName(path.TrimEnd('\\')) is { } name && name.StartsWith('_') && name.EndsWith('_'))
            path = Path.GetDirectoryName(path.TrimEnd('\\'));
        if (path == null || !Directory.Exists(path)) return null;
        var hasGame = SafeDirs(path, "_*_").Any(f => SafeFiles(f, "Wow*.exe").Any());
        return hasGame ? Path.GetFullPath(path).TrimEnd('\\') : null;
    }

    private static string? Clean(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')).Replace('/', '\\');
        return p.Length == 0 ? null : p;
    }

    private static string? Existing(string p) => Directory.Exists(p) ? p : null;

    private static string ProgramFilesX86() =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) is { Length: > 0 } p ? p : @"C:\Program Files (x86)";

    private static IEnumerable<string> FixedDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName);

    private static string? ReadHklm(string key, string value, RegistryView view)
    {
        try
        {
            using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var k = b.OpenSubKey(key);
            return k?.GetValue(value) as string;
        }
        catch { return null; }
    }

    private static IEnumerable<string?> UninstallLocations(string displayNameStartsWith)
    {
        var list = new List<string?>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var u = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (u == null) continue;
                foreach (var name in u.GetSubKeyNames())
                {
                    using var k = u.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is string dn && dn.StartsWith(displayNameStartsWith, StringComparison.OrdinalIgnoreCase))
                        list.Add(k.GetValue("InstallLocation") as string);
                }
            }
            catch { }
        }
        return list;
    }

    private static IEnumerable<string> SafeDirs(string dir, string pattern = "*")
    {
        try { return Directory.EnumerateDirectories(dir, pattern).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
