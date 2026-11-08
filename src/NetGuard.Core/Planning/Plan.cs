using NetGuard.Core.Firewall;

namespace NetGuard.Core.Planning;

public abstract record PlanAction
{
    public abstract string Describe();
}

public sealed record EnableFirewallAction(FwProfileType Profile) : PlanAction
{
    public override string Describe() => $"Turn Windows Firewall ON for the {Profile} profile";
}

public sealed record SetDefaultOutboundAction(FwProfileType Profile, FwAction Action) : PlanAction
{
    public override string Describe() => $"Set default OUTBOUND action of the {Profile} profile to {Action.ToString().ToUpperInvariant()}";
}

public sealed record AddRuleAction(RuleSpec Rule) : PlanAction
{
    public override string Describe() =>
        $"Add rule  {Rule.Name}  [{Rule.Action.ToString().ToUpperInvariant()} out]  {Rule.Program}" +
        (Rule.Service is { } s ? $"  service={s}" : "") +
        (Rule.Protocol != FwProtocol.Any ? $"  {FwProtocol.Name(Rule.Protocol)}" : "") +
        (Rule.RemotePorts is { } p ? $"  port {p}" : "");
}

public sealed record RemoveRuleAction(string Name, string Reason) : PlanAction
{
    public override string Describe() => $"Remove rule  {Name}  ({Reason})";
}

public sealed record DisableForeignRuleAction(string Key, string Name, string? Program) : PlanAction
{
    public override string Describe() => $"Disable non-NetGuard allow rule  \"{Name}\"" + (Program is { Length: > 0 } p ? $"  ({p})" : "");
}

public sealed record EnableForeignRuleAction(string Key, string Name) : PlanAction
{
    public override string Describe() => $"Re-enable rule NetGuard had disabled  \"{Name}\"";
}

public sealed record WriteHostsAction(IReadOnlyList<string> Hosts) : PlanAction
{
    public override string Describe() => Hosts.Count == 0
        ? "Remove the NetGuard block from the hosts file"
        : $"Write the NetGuard block in the hosts file ({Hosts.Count} blocked host names: {string.Join(", ", Hosts.Take(4))}{(Hosts.Count > 4 ? ", …" : "")})";
}

public sealed class Plan
{
    public List<PlanAction> Actions { get; } = new();
    public bool IsEmpty => Actions.Count == 0;
    public void Add(PlanAction a) => Actions.Add(a);
}
