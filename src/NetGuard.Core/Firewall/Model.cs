using System.Security.Cryptography;
using System.Text;

namespace NetGuard.Core.Firewall;

/// <summary>The three Windows Firewall profiles. Values match NET_FW_PROFILE_TYPE2.</summary>
public enum FwProfileType { Domain = 1, Private = 2, Public = 4 }

public enum FwAction { Block = 0, Allow = 1 }

public enum FwDirection { In = 1, Out = 2 }

public static class FwProtocol
{
    public const int Tcp = 6;
    public const int Udp = 17;
    public const int Any = 256;

    public static int Parse(string s) => s.Trim().ToUpperInvariant() switch
    {
        "TCP" => Tcp,
        "UDP" => Udp,
        "ANY" or "" => Any,
        var other when int.TryParse(other, out var n) => n,
        var other => throw new InvalidDataException($"Unknown protocol '{other}'."),
    };

    public static string Name(int p) => p switch { Tcp => "TCP", Udp => "UDP", Any => "Any", _ => p.ToString() };
}

public static class NetGuardNames
{
    public const string Group = "NetGuard";
    public const string Prefix = "NetGuard:";
    public const string AlwaysProfile = "Always";

    public static bool IsNetGuardRule(string? name, string? group) =>
        string.Equals(group, Group, StringComparison.OrdinalIgnoreCase) ||
        (name?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>A rule NetGuard wants to exist. Always outbound, always in group "NetGuard", all firewall profiles.</summary>
public sealed record RuleSpec(
    string Name,
    FwAction Action,
    string Program,
    string? Service,
    int Protocol,
    string? RemotePorts,
    string Description);

/// <summary>A rule as currently found in Windows Firewall.</summary>
public sealed class FwRule
{
    public string Name { get; init; } = "";
    public string? Grouping { get; init; }
    public FwDirection Direction { get; init; }
    public FwAction Action { get; init; }
    public bool Enabled { get; set; }
    public string? Program { get; init; }
    public string? Service { get; init; }
    public int Protocol { get; init; } = FwProtocol.Any;
    public string? LocalPorts { get; init; }
    public string? RemotePorts { get; init; }
    public string? RemoteAddresses { get; init; }
    public int Profiles { get; init; }
    public string? PackageId { get; init; }

    public bool IsNetGuard => NetGuardNames.IsNetGuardRule(Name, Grouping);

    private string? _key;

    /// <summary>
    /// Stable identity for a rule. Windows allows duplicate rule names, so we fingerprint
    /// the defining fields (everything except Enabled).
    /// </summary>
    public string Key => _key ??= ComputeKey();

    private string ComputeKey()
    {
        var parts = string.Join("|",
            Name, Grouping, (int)Direction, (int)Action, Program, Service, Protocol,
            LocalPorts, RemotePorts, RemoteAddresses, Profiles, PackageId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(parts.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 12);
    }

    public bool Matches(RuleSpec spec) =>
        Direction == FwDirection.Out &&
        Enabled &&
        Action == spec.Action &&
        string.Equals(Grouping, NetGuardNames.Group, StringComparison.OrdinalIgnoreCase) &&
        PathEq(Program, spec.Program) &&
        StrEq(Service, spec.Service) &&
        Protocol == spec.Protocol &&
        PortsEq(RemotePorts, spec.RemotePorts);

    internal static bool StrEq(string? a, string? b) =>
        string.Equals(string.IsNullOrEmpty(a) ? null : a, string.IsNullOrEmpty(b) ? null : b, StringComparison.OrdinalIgnoreCase);

    internal static bool PathEq(string? a, string? b) => StrEq(a?.Replace('/', '\\'), b?.Replace('/', '\\'));

    internal static bool PortsEq(string? a, string? b)
    {
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) || s == "*" ? null : s.Replace(" ", "");
        return StrEq(N(a), N(b));
    }
}

public sealed record ProfileSettings(bool Enabled, FwAction DefaultOutbound);

public sealed class FirewallSnapshot
{
    public Dictionary<FwProfileType, ProfileSettings> Profiles { get; init; } = new();
    public List<FwRule> Rules { get; init; } = new();

    public IEnumerable<FwRule> NetGuardRules => Rules.Where(r => r.IsNetGuard);
}
