namespace NetGuard.Core.Firewall;

/// <summary>
/// The only way NetGuard touches Windows Firewall. Implemented by the COM adapter on Windows
/// and by <see cref="InMemoryFirewall"/> in tests.
/// </summary>
public interface IFirewall
{
    FirewallSnapshot GetSnapshot();
    void SetEnabled(FwProfileType profile, bool enabled);
    void SetDefaultOutbound(FwProfileType profile, FwAction action);
    void AddRule(RuleSpec spec);
    /// <summary>Remove every rule with this exact name. Only ever called with NetGuard names.</summary>
    void RemoveRules(string name);
    /// <summary>
    /// Enable/disable the rule(s) called <paramref name="name"/> whose <see cref="FwRule.Key"/> equals <paramref name="key"/>.
    /// Returns how many matched.
    /// </summary>
    int SetRuleEnabled(string name, string key, bool enabled);
}
