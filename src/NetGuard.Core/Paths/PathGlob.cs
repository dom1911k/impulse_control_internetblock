namespace NetGuard.Core.Paths;

/// <summary>
/// Minimal path globbing: '*' / '?' inside a segment, and '**' as a whole segment for "any depth".
/// Firewall rules need exact paths, so NetGuard expands these patterns itself (and re-expands them
/// on every self-heal cycle so updates that create new versioned folders are picked up).
/// </summary>
public static class PathGlob
{
    public const int MaxRecursionDepth = 8;
    public const int MaxResults = 500;

    private static readonly char[] Separators = { '\\', '/' };

    public static List<string> Expand(string pattern, Func<string, string>? expandEnv = null)
    {
        expandEnv ??= Environment.ExpandEnvironmentVariables;
        var expanded = expandEnv(pattern.Trim().Trim('"'));
        var results = new List<string>();
        if (expanded.Length == 0) return results;

        var (root, rest) = SplitRoot(expanded);
        if (root == null) return results; // relative paths are never allowed

        var segments = rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return results;

        Walk(root, segments, 0, results);
        return results
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool HasWildcard(string pattern) => pattern.IndexOfAny(new[] { '*', '?' }) >= 0;

    private static (string? root, string rest) SplitRoot(string p)
    {
        // Windows drive: C:\...
        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
            return (p[..2] + Path.DirectorySeparatorChar, p.Length > 2 ? p[2..] : "");
        // Unix absolute (used by tests on Linux)
        if (p.StartsWith('/') && Path.DirectorySeparatorChar == '/')
            return ("/", p[1..]);
        return (null, p);
    }

    private static void Walk(string dir, string[] segs, int i, List<string> results)
    {
        if (results.Count >= MaxResults) return;
        var seg = segs[i];
        var last = i == segs.Length - 1;

        if (seg == "**")
        {
            if (last) return; // "**" must be followed by something
            foreach (var d in SelfAndDescendants(dir, 0))
                Walk(d, segs, i + 1, results);
            return;
        }

        if (HasWildcard(seg))
        {
            IEnumerable<string> matches;
            try
            {
                matches = last ? Directory.EnumerateFiles(dir, seg) : Directory.EnumerateDirectories(dir, seg);
                matches = matches.ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return;
            }
            foreach (var m in matches)
            {
                if (last) { results.Add(m); if (results.Count >= MaxResults) return; }
                else Walk(m, segs, i + 1, results);
            }
            return;
        }

        var next = Path.Combine(dir, seg);
        if (last) { if (File.Exists(next)) results.Add(next); }
        else if (Directory.Exists(next)) Walk(next, segs, i + 1, results);
    }

    private static IEnumerable<string> SelfAndDescendants(string dir, int depth)
    {
        yield return dir;
        if (depth >= MaxRecursionDepth) yield break;
        List<string> children;
        try
        {
            children = Directory.EnumerateDirectories(dir)
                // never follow junctions / symlinks: avoids loops and redirection tricks
                .Where(d => (new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) == 0)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }
        foreach (var c in children)
            foreach (var d in SelfAndDescendants(c, depth + 1))
                yield return d;
    }
}
