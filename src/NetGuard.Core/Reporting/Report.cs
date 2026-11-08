using System.Text;
using System.Text.Json.Nodes;
using NetGuard.Core.Config;
using NetGuard.Core.Logging;
using NetGuard.Core.Planning;

namespace NetGuard.Core.Reporting;

/// <summary>Plain-text descriptions shared by the window app and ngctl.</summary>
public static class Report
{
    public static string Detected(DetectedPaths d)
    {
        var sb = new StringBuilder();
        void L(string label, IEnumerable<string> v, string none)
        {
            var list = v.ToList();
            sb.Append($"{label,-20}").AppendLine(list.Count > 0 ? list[0] : none);
            foreach (var x in list.Skip(1)) sb.Append(new string(' ', 20)).AppendLine(x);
        }
        L("World of Warcraft", d.WowRoots, "(not found)");
        L("Battle.net app", d.BattleNetDir is { } b ? new[] { b } : Array.Empty<string>(), "(not found)");
        L("Blizzard Agent", d.BattleNetAgentDir is { } a ? new[] { a } : Array.Empty<string>(), "(not found)");
        L("Spotify (desktop)", d.SpotifyExes, "(not found)");
        L("Spotify (Store)", d.SpotifyStoreInstalls, "(none – good)");
        L("Steam (blocked)", d.SteamRoots, "(not found)");
        foreach (var w in d.Warnings) sb.AppendLine("! " + w);
        return sb.ToString();
    }

    public static string Desired(DesiredState d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Profile {d.ProfileName} – " +
                      (d.Kind == ProfileKind.AllowAll ? "everything allowed except always-blocked programs" : "only these programs may use the internet:"));
        if (d.Kind == ProfileKind.Allowlist)
        {
            if (d.AllowedPrograms.Count == 0) sb.AppendLine("  (none – DNS and time sync only)");
            foreach (var p in d.AllowedPrograms) sb.AppendLine($"  + {p.Path}   [{p.Detail}]");
            foreach (var p in d.RejectedPrograms) sb.AppendLine($"  x {p.Path}   [REJECTED: {p.Detail}]");
        }
        sb.AppendLine($"Always blocked: {d.BlockedPrograms.Count} Steam program files, {d.BlockedHosts.Count} Steam web addresses (hosts file)");
        foreach (var w in d.Warnings.Where(w => !w.Contains("NOT allowed"))) sb.AppendLine("! " + w);
        return sb.ToString();
    }

    public static string Plan(Plan plan)
    {
        if (plan.IsEmpty) return "Changes: none – the firewall already matches.\n";
        var sb = new StringBuilder($"Changes ({plan.Actions.Count}):\n");
        foreach (var a in plan.Actions) sb.AppendLine("  - " + a.Describe());
        return sb.ToString();
    }

    public static string Result(ExecutionResult r)
    {
        if (r.Success) return $"{r.Done.Count} change(s) applied.\n";
        var sb = new StringBuilder($"{r.Done.Count} change(s) applied, {r.Errors.Count} FAILED:\n");
        foreach (var e in r.Errors) sb.AppendLine($"  ! {e.Action}: {e.Error}");
        return sb.ToString();
    }

    public static string EventName(string? type) => type switch
    {
        LogTypes.ProfileApplied => "Profile applied",
        LogTypes.ApplyError => "Problem while applying",
        LogTypes.Recovery => "Normal internet restored",
        LogTypes.TestModeStarted => "Test lock started",
        LogTypes.TestModeEnded => "Test lock ended",
        LogTypes.Tamper => "Tampering undone",
        LogTypes.ConfigCreated => "Configuration created",
        LogTypes.Rejected => "Program rejected",
        LogTypes.Updated => "NetGuard updated",
        _ => type ?? "?",
    };

    public static string LogSummary(JsonObject entry)
    {
        if (entry["data"] is not JsonObject d) return "";
        var parts = new List<string>();
        foreach (var key in new[] { "profile", "reason", "minutes", "path", "from", "to", "endedEarly" })
            if (d[key] is { } v) parts.Add($"{key}: {v.ToJsonString().Trim('"')}");
        if (d["changes"] is JsonArray c) parts.Add($"{c.Count} change(s)");
        if (d["errors"] is JsonArray er && er.Count > 0)
        {
            var first = er[0] is JsonObject fe ? $"{(string?)fe["action"]}: {(string?)fe["error"]}" : er[0]?.ToJsonString();
            parts.Add($"{er.Count} ERROR(S) – {first}");
        }
        return string.Join(" · ", parts);
    }
}
