using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NetGuard.Core.Firewall;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NetGuard.Windows;

/// <summary>
/// Windows Defender Firewall through its COM API (HNetCfg.FwPolicy2).
///
/// Why COM instead of PowerShell's NetSecurity cmdlets: the service re-reads the whole firewall
/// every 30 seconds. COM does that in milliseconds in-process; PowerShell needs a new runspace
/// (~1 s, lots of memory) and depends on the PowerShell execution environment.
/// We use late binding (IDispatch) so there is no interop assembly to ship.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ComFirewall : IFirewall
{
    private const int NET_FW_RULE_DIR_OUT = 2;
    private const int NET_FW_PROFILE2_ALL = 0x7FFFFFFF;

    private static object NewPolicy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;

    private static object NewRule() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;

    public FirewallSnapshot GetSnapshot()
    {
        var policy = NewPolicy();
        try
        {
            var snap = new FirewallSnapshot();
            foreach (var p in new[] { FwProfileType.Domain, FwProfileType.Private, FwProfileType.Public })
            {
                var enabled = Convert.ToBoolean(Get(policy, "FirewallEnabled", (int)p));
                var outbound = Convert.ToInt32(Get(policy, "DefaultOutboundAction", (int)p));
                snap.Profiles[p] = new ProfileSettings(enabled, outbound == 0 ? FwAction.Block : FwAction.Allow);
            }
            foreach (var r in EnumerateRules(policy))
            {
                try { snap.Rules.Add(ToModel(r)); }
                finally { Release(r); }
            }
            return snap;
        }
        finally { Release(policy); }
    }

    public void SetEnabled(FwProfileType profile, bool enabled)
    {
        var policy = NewPolicy();
        try { Set(policy, "FirewallEnabled", (int)profile, enabled); }
        catch (Exception e) when (e is TargetInvocationException or COMException or MissingMethodException)
        {
            // Fallback in case late-bound parameterised properties misbehave on some Windows build.
            Netsh($"advfirewall set {NetshProfile(profile)} state {(enabled ? "on" : "off")}");
        }
        finally { Release(policy); }
    }

    public void SetDefaultOutbound(FwProfileType profile, FwAction action)
    {
        var policy = NewPolicy();
        try { Set(policy, "DefaultOutboundAction", (int)profile, (int)action); }
        catch (Exception e) when (e is TargetInvocationException or COMException or MissingMethodException)
        {
            Netsh($"advfirewall set {NetshProfile(profile)} firewallpolicy blockinbound,{(action == FwAction.Block ? "blockoutbound" : "allowoutbound")}");
        }
        finally { Release(policy); }
    }

    public void AddRule(RuleSpec spec)
    {
        if (!spec.Name.StartsWith(NetGuardNames.Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("NetGuard only creates rules named NetGuard:…");

        var policy = NewPolicy();
        var rule = NewRule();
        try
        {
            Set(rule, "Name", spec.Name);
            Set(rule, "Description", spec.Description);
            Set(rule, "Grouping", NetGuardNames.Group);
            Set(rule, "ApplicationName", spec.Program);
            if (!string.IsNullOrEmpty(spec.Service)) Set(rule, "serviceName", spec.Service);
            Set(rule, "Protocol", spec.Protocol);                // must be set before ports
            if (!string.IsNullOrEmpty(spec.RemotePorts)) Set(rule, "RemotePorts", spec.RemotePorts);
            Set(rule, "Direction", NET_FW_RULE_DIR_OUT);
            Set(rule, "Action", (int)spec.Action);
            Set(rule, "Profiles", NET_FW_PROFILE2_ALL);
            Set(rule, "InterfaceTypes", "All");
            Set(rule, "Enabled", true);

            var rules = Get(policy, "Rules")!;
            try { Call(rules, "Add", rule); }
            finally { Release(rules); }
        }
        finally { Release(rule); Release(policy); }
    }

    public void RemoveRules(string name)
    {
        if (!name.StartsWith(NetGuardNames.Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("NetGuard only removes its own rules.");

        var policy = NewPolicy();
        try
        {
            var rules = Get(policy, "Rules")!;
            try
            {
                // INetFwRules.Remove deletes one rule per call when names are duplicated: loop until gone.
                for (var i = 0; i < 100 && CountNamed(policy, name) > 0; i++)
                    Call(rules, "Remove", name);
            }
            finally { Release(rules); }
        }
        finally { Release(policy); }
    }

    public int SetRuleEnabled(string name, string key, bool enabled)
    {
        var policy = NewPolicy();
        var n = 0;
        try
        {
            foreach (var r in EnumerateRules(policy))
            {
                try
                {
                    if (!string.Equals(Str(r, "Name"), name, StringComparison.Ordinal)) continue;
                    if (ToModel(r).Key != key) continue;
                    Set(r, "Enabled", enabled);
                    n++;
                }
                finally { Release(r); }
            }
        }
        finally { Release(policy); }
        return n;
    }

    // ---------- helpers ----------

    private static int CountNamed(object policy, string name)
    {
        var n = 0;
        foreach (var r in EnumerateRules(policy))
        {
            try { if (string.Equals(Str(r, "Name"), name, StringComparison.Ordinal)) n++; }
            finally { Release(r); }
        }
        return n;
    }

    private static List<object> EnumerateRules(object policy)
    {
        var list = new List<object>();
        var rules = Get(policy, "Rules")!;
        try
        {
            if (rules is IEnumerable e)
            {
                try
                {
                    foreach (var r in e) if (r != null) list.Add(r);
                    return list;
                }
                catch (Exception ex) when (ex is InvalidCastException or COMException or NotSupportedException)
                {
                    foreach (var r in list) Release(r);
                    list.Clear();
                }
            }
            // Fallback: walk IEnumVARIANT manually.
            var enumObj = Get(rules, "_NewEnum");
            if (enumObj is not ComTypes.IEnumVARIANT ev) throw new InvalidOperationException("Cannot enumerate firewall rules.");
            var buf = new object[1];
            while (ev.Next(1, buf, IntPtr.Zero) == 0)
                if (buf[0] != null) list.Add(buf[0]);
            return list;
        }
        finally { Release(rules); }
    }

    private static FwRule ToModel(object r) => new()
    {
        Name = Str(r, "Name") ?? "",
        Grouping = Str(r, "Grouping"),
        Direction = Int(r, "Direction") == NET_FW_RULE_DIR_OUT ? FwDirection.Out : FwDirection.In,
        Action = Int(r, "Action") == 1 ? FwAction.Allow : FwAction.Block,
        Enabled = Bool(r, "Enabled"),
        Program = ExpandPath(Str(r, "ApplicationName")),
        Service = Str(r, "serviceName"),
        Protocol = Int(r, "Protocol") ?? FwProtocol.Any,
        LocalPorts = Str(r, "LocalPorts"),
        RemotePorts = Str(r, "RemotePorts"),
        RemoteAddresses = Str(r, "RemoteAddresses"),
        Profiles = Int(r, "Profiles") ?? 0,
        PackageId = Str(r, "LocalAppPackageId"),
    };

    private static string? ExpandPath(string? p) => string.IsNullOrEmpty(p) ? p : Environment.ExpandEnvironmentVariables(p);

    private static object? Get(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args);

    private static void Set(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, args);

    private static object? Call(object o, string name, params object[] args) =>
        o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);

    private static object? TryGet(object o, string name)
    {
        try { return Get(o, name); }
        catch (Exception e) when (e is TargetInvocationException or COMException or MissingMethodException) { return null; }
    }

    private static string? Str(object o, string name) => TryGet(o, name) as string;
    private static int? Int(object o, string name) => TryGet(o, name) is { } v ? Convert.ToInt32(v) : null;
    private static bool Bool(object o, string name) => TryGet(o, name) is { } v && Convert.ToBoolean(v);

    private static void Release(object? o)
    {
        if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    private static string NetshProfile(FwProfileType p) => p switch
    {
        FwProfileType.Domain => "domainprofile",
        FwProfileType.Private => "privateprofile",
        _ => "publicprofile",
    };

    private static void Netsh(string args)
    {
        var r = ProcessRunner.Run("netsh.exe", args);
        if (r.ExitCode != 0) throw new InvalidOperationException($"netsh {args} failed: {r.Output}");
    }
}
