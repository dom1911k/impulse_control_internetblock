using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;

namespace NetGuard.Windows;

public sealed record BlockedApp(string Application, int Count, DateTime LastSeen, IReadOnlyList<string> Destinations);

/// <summary>
/// Diagnostic: which programs had outbound connections blocked recently?
/// Uses Windows' own audit events (Security log, event 5157 "Filtering Platform Connection").
/// Useful to find a Blizzard helper exe we forgot to allow.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BlockedConnections
{
    // GUID of the "Filtering Platform Connection" audit subcategory (language-independent).
    private const string SubcategoryGuid = "{0CCE9226-69AE-11D9-BED3-505054503030}";

    public static string SetAuditing(bool on)
    {
        var r = ProcessRunner.Run("auditpol.exe", $"/set /subcategory:{SubcategoryGuid} /failure:{(on ? "enable" : "disable")}");
        if (r.ExitCode != 0) throw new InvalidOperationException("auditpol failed: " + r.Output);
        return r.Output;
    }

    public static List<BlockedApp> Query(TimeSpan window, int maxEvents = 50_000)
    {
        var ms = (long)window.TotalMilliseconds;
        var q = new EventLogQuery("Security", PathType.LogName,
            $"*[System[(EventID=5157) and TimeCreated[timediff(@SystemTime) <= {ms}]]]") { ReverseDirection = true };
        var devMap = DeviceToDriveMap();
        var byApp = new Dictionary<string, (int count, DateTime last, HashSet<string> dests)>(StringComparer.OrdinalIgnoreCase);

        using var reader = new EventLogReader(q);
        var n = 0;
        for (var rec = reader.ReadEvent(); rec != null && n < maxEvents; rec = reader.ReadEvent(), n++)
        {
            using (rec)
            {
                var data = XDocument.Parse(rec.ToXml()).Descendants()
                    .Where(e => e.Name.LocalName == "Data" && e.Attribute("Name") != null)
                    .ToDictionary(e => e.Attribute("Name")!.Value, e => e.Value);
                if (data.GetValueOrDefault("Direction") != "%%14593") continue; // outbound only
                var app = ToDosPath(data.GetValueOrDefault("Application") ?? "System", devMap);
                var dest = $"{data.GetValueOrDefault("DestAddress")}:{data.GetValueOrDefault("DestPort")}";
                var t = rec.TimeCreated ?? DateTime.MinValue;
                if (!byApp.TryGetValue(app, out var v)) v = (0, t, new HashSet<string>());
                v.count++;
                if (t > v.last) v.last = t;
                if (v.dests.Count < 5) v.dests.Add(dest);
                byApp[app] = v;
            }
        }
        return byApp.Select(kv => new BlockedApp(kv.Key, kv.Value.count, kv.Value.last, kv.Value.dests.ToList()))
            .OrderByDescending(a => a.Count).ToList();
    }

    private static Dictionary<string, string> DeviceToDriveMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buf = new StringBuilder(1024);
        foreach (var d in DriveInfo.GetDrives())
        {
            var letter = d.Name.TrimEnd('\\');
            buf.Clear();
            if (QueryDosDevice(letter, buf, buf.Capacity) != 0) map[buf.ToString()] = letter;
        }
        return map;
    }

    private static string ToDosPath(string devicePath, Dictionary<string, string> map)
    {
        foreach (var (dev, letter) in map)
            if (devicePath.StartsWith(dev + "\\", StringComparison.OrdinalIgnoreCase))
                return letter + devicePath[dev.Length..];
        return devicePath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
}
