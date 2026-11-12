using System.Text.RegularExpressions;
using NetGuard.Core.Config;
using NetGuard.Core.Paths;

namespace NetGuard.Core.Editing;

/// <summary>
/// Changes to the configuration made from the program editor. Every method says whether it
/// loosens (lets more online) – the service only allows loosening with normal internet or in Maintenance.
/// </summary>
public static class ConfigEditor
{
    public const string AlwaysBlockedList = "Always blocked";
    public const string WindowList = "Short window";

    /// <summary>Suggest an entry for a file the user picked: signed files are pinned to their publisher, unsigned ones to their exact content.</summary>
    public static ProgramEntry Suggest(string path, string? friendlyName, SignatureInfo sig, Func<string, string> sha256)
    {
        var name = string.IsNullOrWhiteSpace(friendlyName)
            ? Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1])
            : friendlyName.Trim();
        var signer = sig.IsTrusted ? SignerName(sig.Subject) : null;
        return signer != null
            ? new ProgramEntry { Name = name, Paths = { path }, Pin = PinMode.Signature, Signer = signer }
            : new ProgramEntry { Name = name, Paths = { path }, Pin = PinMode.Sha256, Sha256 = sha256(path) };
    }

    /// <summary>Organisation (O=) of a certificate subject, falling back to its common name (CN=).</summary>
    public static string? SignerName(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        string? Field(string key)
        {
            var m = Regex.Match(subject, $@"(?:^|,\s*){key}=(""(?:[^""]|"""")*""|[^,]*)");
            if (!m.Success) return null;
            var v = m.Groups[1].Value.Trim();
            if (v.StartsWith('"') && v.EndsWith('"')) v = v[1..^1].Replace("\"\"", "\"");
            return v.Length == 0 ? null : v;
        }
        return Field("O") ?? Field("CN");
    }

    public static void AddProgram(NetGuardConfig cfg, string profileName, ProgramEntry entry)
    {
        var list = ListFor(cfg, profileName);
        var p = cfg.FindProfile(profileName);
        if (p != null && p.Kind == ProfileKind.AllowAll)
            throw new InvalidOperationException($"{p.Name} already allows everything.");
        if (list.Any(e => e.Paths.Any(x => entry.Paths.Contains(x, StringComparer.OrdinalIgnoreCase))))
            throw new InvalidOperationException("That program is already on this list.");
        entry.Name = UniqueName(list, entry.Name);
        list.Add(entry);
        cfg.Validate();
    }

    public static void RemoveProgram(NetGuardConfig cfg, string profileName, string entryName)
    {
        var list = ListFor(cfg, profileName);
        var removed = list.RemoveAll(e => string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) throw new InvalidOperationException($"'{entryName}' is not on this list.");
    }

    public static void AddProfile(NetGuardConfig cfg, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 30 || name.Contains(':') || string.Equals(name, AlwaysBlockedList, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Please choose a short name without ':'.");
        if (cfg.FindProfile(name) != null) throw new InvalidOperationException($"There already is a profile called {name}.");
        cfg.Profiles.Add(new ProfileDefinition { Name = name, Kind = ProfileKind.Allowlist });
        cfg.Validate();
    }

    public static void RemoveProfile(NetGuardConfig cfg, string name, string? activeProfile)
    {
        var p = cfg.FindProfile(name) ?? throw new InvalidOperationException($"There is no profile called {name}.");
        if (string.Equals(p.Name, activeProfile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The active profile can't be deleted.");
        if (cfg.Profiles.Count(x => x.Kind == ProfileKind.Allowlist) <= 1 && p.Kind == ProfileKind.Allowlist)
            throw new InvalidOperationException("At least one locking profile has to stay.");
        cfg.Profiles.Remove(p);
    }

    /// <summary>Does this edit let more online than before?</summary>
    public static bool IsLoosening(string command, string profileName) => command switch
    {
        "program-add" => !IsAlwaysBlocked(profileName),
        "program-remove" => IsAlwaysBlocked(profileName),
        "website-add" => true,
        _ => false,
    };

    public static bool IsAlwaysBlocked(string profileName) =>
        string.Equals(profileName, AlwaysBlockedList, StringComparison.OrdinalIgnoreCase);

    public static bool IsWindowList(string profileName) =>
        string.Equals(profileName, WindowList, StringComparison.OrdinalIgnoreCase);

    public static void AddWebsite(NetGuardConfig cfg, string site)
    {
        site = NormalizeSite(site);
        cfg.BrowseWindow ??= DefaultConfig.BrowseWindowDefault();
        if (cfg.BrowseWindow.Websites.Contains(site, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{site} is already on the list.");
        cfg.BrowseWindow.Websites.Add(site);
    }

    public static void RemoveWebsite(NetGuardConfig cfg, string site)
    {
        cfg.BrowseWindow ??= DefaultConfig.BrowseWindowDefault();
        if (cfg.BrowseWindow.Websites.RemoveAll(w => string.Equals(w, site.Trim(), StringComparison.OrdinalIgnoreCase)) == 0)
            throw new InvalidOperationException($"{site} is not on the list.");
    }

    /// <summary>"https://www.songsterr.com/a/wsa/x" → "songsterr.com" (a Chrome allowlist entry covering all subdomains).</summary>
    public static string NormalizeSite(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        if (s.Contains("://")) s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        s = s.Split('/', '?', '#')[0].Split(':')[0];
        if (s.StartsWith("www.")) s = s[4..];
        if (s.Length == 0 || !s.Contains('.') || s.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-')))
            throw new InvalidOperationException($"'{input}' doesn't look like a website address (e.g. songsterr.com).");
        return s;
    }

    private static List<ProgramEntry> ListFor(NetGuardConfig cfg, string profileName) =>
        IsWindowList(profileName)
            ? (cfg.BrowseWindow ??= DefaultConfig.BrowseWindowDefault()).Programs
            : IsAlwaysBlocked(profileName)
            ? cfg.AlwaysBlocked
            : (cfg.FindProfile(profileName) ?? throw new InvalidOperationException($"There is no profile called {profileName}.")).Programs;

    private static string UniqueName(List<ProgramEntry> list, string name)
    {
        var candidate = name;
        for (var i = 2; list.Any(e => string.Equals(e.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++) candidate = $"{name} ({i})";
        return candidate;
    }
}
