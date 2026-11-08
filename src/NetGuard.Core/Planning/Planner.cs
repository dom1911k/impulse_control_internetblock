using NetGuard.Core.Firewall;
using NetGuard.Core.State;

namespace NetGuard.Core.Planning;

public static class Planner
{
    private static readonly FwProfileType[] AllProfiles = { FwProfileType.Domain, FwProfileType.Private, FwProfileType.Public };

    /// <summary>
    /// What must change so the firewall matches <paramref name="desired"/>. Empty plan = nothing to do.
    ///
    /// Order matters ("make before break"): Windows re-checks every open connection whenever the
    /// firewall changes. If "block by default" went in before a program's allow rule, a running game
    /// would have its connection silently dropped in that gap. So:
    ///   tightening (default Block): add/repair rules → remove old rules → disable foreign rules → Block
    ///   loosening  (default Allow): Allow first → then the rule changes
    /// </summary>
    public static Plan Diff(DesiredState desired, FirewallSnapshot snap, bool hostsUpToDate, IReadOnlyCollection<string> protectedGroups)
    {
        var plan = new Plan();
        var tightening = desired.DefaultOutbound == FwAction.Block;

        // Default outbound for all three profiles (+ firewall on)
        var profileActions = new List<PlanAction>();
        foreach (var p in AllProfiles)
        {
            var cur = snap.Profiles.TryGetValue(p, out var s) ? s : new ProfileSettings(false, FwAction.Allow);
            if (cur.DefaultOutbound != desired.DefaultOutbound) profileActions.Add(new SetDefaultOutboundAction(p, desired.DefaultOutbound));
            if (!cur.Enabled) profileActions.Add(new EnableFirewallAction(p));
        }
        if (!tightening) plan.Actions.AddRange(profileActions);

        // Exactly the expected NetGuard rules: new/repaired ones first, unwanted ones afterwards
        var existing = snap.NetGuardRules.GroupBy(r => r.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var wanted = desired.Rules.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        // Allow rules before block rules, so allowed programs never wait on the (many) Steam blocks.
        foreach (var spec in desired.Rules.OrderBy(r => r.Action == FwAction.Allow ? 0 : 1))
        {
            if (existing.TryGetValue(spec.Name, out var cur))
            {
                if (cur.Count == 1 && cur[0].Matches(spec)) continue;
                plan.Add(new RemoveRuleAction(spec.Name, cur.Count > 1 ? "duplicate" : "modified or disabled"));
            }
            plan.Add(new AddRuleAction(spec));
        }

        foreach (var (name, _) in existing.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (!wanted.Contains(name))
                plan.Add(new RemoveRuleAction(name, "not part of profile " + desired.ProfileName));

        // No foreign outbound allow rules may stay active in allow-list profiles
        if (desired.DisableForeignAllowRules)
        {
            foreach (var r in snap.Rules)
            {
                if (r.IsNetGuard || r.Direction != FwDirection.Out || r.Action != FwAction.Allow || !r.Enabled) continue;
                if (IsProtected(r, protectedGroups)) continue;
                plan.Add(new DisableForeignRuleAction(r.Key, r.Name, r.Program));
            }
        }

        if (!hostsUpToDate) plan.Add(new WriteHostsAction(desired.BlockedHosts));

        // Tightening: the switch to "block by default" comes last, once every allow rule is in place.
        if (tightening) plan.Actions.AddRange(profileActions);
        return plan;
    }

    /// <summary>Undo everything NetGuard did: default outbound Allow, no NetGuard rules, foreign rules back on, hosts block gone.</summary>
    public static Plan Recovery(FirewallSnapshot snap, NetGuardState state, bool hostsHasBlock)
    {
        var plan = new Plan();
        foreach (var p in AllProfiles)
            if (!snap.Profiles.TryGetValue(p, out var s) || s.DefaultOutbound != FwAction.Allow)
                plan.Add(new SetDefaultOutboundAction(p, FwAction.Allow));

        foreach (var name in snap.NetGuardRules.Select(r => r.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            plan.Add(new RemoveRuleAction(name, "recovery"));

        foreach (var rec in state.DisabledForeignRules)
        {
            var match = snap.Rules.Where(r => r.Key == rec.Key).ToList();
            if (match.Count > 0 && match.Any(r => !r.Enabled))
                plan.Add(new EnableForeignRuleAction(rec.Key, rec.Name));
        }

        if (hostsHasBlock) plan.Add(new WriteHostsAction(Array.Empty<string>()));
        return plan;
    }

    public static bool IsProtected(FwRule r, IReadOnlyCollection<string> protectedGroups) =>
        r.Grouping is { Length: > 0 } g && protectedGroups.Any(p => string.Equals(p, g, StringComparison.OrdinalIgnoreCase));
}
