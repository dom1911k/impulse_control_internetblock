using System.Runtime.Versioning;
using System.Text;
using NetGuard.Core;
using NetGuard.Core.Firewall;
using NetGuard.Core.Planning;
using NetGuard.Core.State;

namespace NetGuard.Windows;

public sealed record NetGuardStatus(
    NetGuardState State,
    FirewallSnapshot Firewall,
    bool DeadManArmed,
    Plan? Drift,
    string? DriftError)
{
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Active profile:   {State.ActiveProfile ?? "none (normal internet)"}");
        if (State.ActiveSinceUtc is { } s) sb.AppendLine($"Active since:     {s.ToLocalTime():g}");
        if (State.TestMode is { } tm) sb.AppendLine($"Test mode:        ends at {tm.DeadlineUtc.ToLocalTime():T}");
        sb.AppendLine($"Safety task:      {(DeadManArmed ? "armed" : "not present")}");
        sb.AppendLine($"Disabled rules:   {State.DisabledForeignRules.Count} Windows allow rules switched off by NetGuard");
        sb.AppendLine();
        foreach (var (p, ps) in Firewall.Profiles.OrderBy(k => k.Key))
            sb.AppendLine($"Firewall {p,-8}  {(ps.Enabled ? "ON " : "OFF")}  default outbound {ps.DefaultOutbound.ToString().ToUpperInvariant()}");
        var rules = Firewall.NetGuardRules.OrderBy(r => r.Name).ToList();
        sb.AppendLine($"NetGuard rules:   {rules.Count}");
        foreach (var r in rules) sb.AppendLine($"  {(r.Enabled ? " " : "!")} {r.Action.ToString().ToUpperInvariant(),-5} {r.Name}");
        sb.AppendLine();
        if (DriftError != null) sb.AppendLine("Drift check failed: " + DriftError);
        else if (Drift == null) { }
        else if (Drift.IsEmpty) sb.AppendLine("In sync: the firewall matches the active profile.");
        else
        {
            sb.AppendLine($"{Drift.Actions.Count} difference(s) from the active profile:");
            foreach (var a in Drift.Actions) sb.AppendLine("  - " + a.Describe());
        }
        return sb.ToString();
    }
}

[SupportedOSPlatform("windows")]
public static class StatusReader
{
    public static NetGuardStatus Read(Engine engine)
    {
        var state = engine.LoadState();
        var snap = new ComFirewall().GetSnapshot();
        Plan? drift = null;
        string? err = null;
        if (state.ActiveProfile != null && File.Exists(engine.Paths.ConfigFile))
        {
            try
            {
                var cfg = engine.LoadConfig();
                drift = engine.PlanApply(cfg, engine.BuildDesired(cfg, state.ActiveProfile));
            }
            catch (Exception e) { err = e.Message; }
        }
        return new NetGuardStatus(state, snap, DeadManSwitch.IsArmed(), drift, err);
    }
}
