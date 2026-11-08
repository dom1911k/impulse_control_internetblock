namespace NetGuard.Core.Firewall;

/// <summary>A fake firewall for unit tests and simulations.</summary>
public sealed class InMemoryFirewall : IFirewall
{
    public Dictionary<FwProfileType, ProfileSettings> Profiles { get; } = new()
    {
        [FwProfileType.Domain] = new(true, FwAction.Allow),
        [FwProfileType.Private] = new(true, FwAction.Allow),
        [FwProfileType.Public] = new(true, FwAction.Allow),
    };

    public List<FwRule> Rules { get; } = new();

    public FirewallSnapshot GetSnapshot() => new()
    {
        Profiles = new(Profiles),
        Rules = Rules.Select(Clone).ToList(),
    };

    public void SetEnabled(FwProfileType profile, bool enabled) =>
        Profiles[profile] = Profiles[profile] with { Enabled = enabled };

    public void SetDefaultOutbound(FwProfileType profile, FwAction action) =>
        Profiles[profile] = Profiles[profile] with { DefaultOutbound = action };

    public void AddRule(RuleSpec spec) => Rules.Add(new FwRule
    {
        Name = spec.Name,
        Grouping = NetGuardNames.Group,
        Direction = FwDirection.Out,
        Action = spec.Action,
        Enabled = true,
        Program = spec.Program,
        Service = spec.Service,
        Protocol = spec.Protocol,
        RemotePorts = spec.RemotePorts,
        Profiles = 0x7FFFFFFF,
    });

    public void RemoveRules(string name) => Rules.RemoveAll(r => r.Name == name);

    public int SetRuleEnabled(string name, string key, bool enabled)
    {
        var n = 0;
        foreach (var r in Rules.Where(r => r.Name == name && r.Key == key)) { r.Enabled = enabled; n++; }
        return n;
    }

    private static FwRule Clone(FwRule r) => new()
    {
        Name = r.Name, Grouping = r.Grouping, Direction = r.Direction, Action = r.Action, Enabled = r.Enabled,
        Program = r.Program, Service = r.Service, Protocol = r.Protocol, LocalPorts = r.LocalPorts,
        RemotePorts = r.RemotePorts, RemoteAddresses = r.RemoteAddresses, Profiles = r.Profiles, PackageId = r.PackageId,
    };
}
