namespace NetGuard.Core.Hosts;

/// <summary>
/// Manages one clearly marked block inside the Windows hosts file. Everything outside the
/// markers is left untouched.
/// </summary>
public static class HostsFile
{
    public const string BeginMarker = "# >>> NetGuard managed block - do not edit (changes are undone automatically) >>>";
    public const string EndMarker = "# <<< NetGuard managed block <<<";

    public static string DefaultPath =>
        Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    /// <summary>Host names currently inside the NetGuard block.</summary>
    public static SortedSet<string> ReadManagedHosts(string text)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var inside = false;
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line == BeginMarker) { inside = true; continue; }
            if (line == EndMarker) { inside = false; continue; }
            if (!inside || line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) set.Add(parts[1]);
        }
        return set;
    }

    /// <summary>True if the block exists and contains exactly these hosts in the canonical form.</summary>
    public static bool IsUpToDate(string text, IEnumerable<string> hosts) =>
        ExtractBlock(text) == BuildBlock(hosts);

    /// <summary>Return the file text with the NetGuard block replaced (or removed if <paramref name="hosts"/> is empty).</summary>
    public static string Rewrite(string text, IEnumerable<string> hosts)
    {
        var outside = new List<string>();
        var inside = false;
        foreach (var line in SplitLines(text))
        {
            var t = line.Trim();
            if (t == BeginMarker) { inside = true; continue; }
            if (t == EndMarker) { inside = false; continue; }
            if (!inside) outside.Add(line);
        }
        while (outside.Count > 0 && outside[^1].Trim().Length == 0) outside.RemoveAt(outside.Count - 1);

        var block = BuildBlock(hosts);
        var sb = new System.Text.StringBuilder();
        foreach (var l in outside) sb.Append(l).Append("\r\n");
        if (block != null)
        {
            if (outside.Count > 0) sb.Append("\r\n");
            sb.Append(block);
        }
        return sb.ToString();
    }

    private static string? BuildBlock(IEnumerable<string> hosts)
    {
        var list = hosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0)
            .Distinct().OrderBy(h => h, StringComparer.Ordinal).ToList();
        if (list.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        sb.Append(BeginMarker).Append("\r\n");
        foreach (var h in list)
        {
            sb.Append("0.0.0.0 ").Append(h).Append("\r\n");
            sb.Append(":: ").Append(h).Append("\r\n");
        }
        sb.Append(EndMarker).Append("\r\n");
        return sb.ToString();
    }

    private static string? ExtractBlock(string text)
    {
        var start = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0) return "broken";
        var block = text[start..(end + EndMarker.Length)];
        return string.Join("\r\n", SplitLines(block).Select(l => l.Trim())) + "\r\n";
    }

    private static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
