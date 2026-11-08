using NetGuard.Core.Config;
using NetGuard.Core.Firewall;
using NetGuard.Core.Hosts;
using NetGuard.Core.Planning;

namespace NetGuard.Core.Tests;

public class EngineTests
{
    private static FwRule Foreign(string name, FwAction action = FwAction.Allow, FwDirection dir = FwDirection.Out, string? group = null, bool enabled = true) => new()
    {
        Name = name, Grouping = group, Direction = dir, Action = action, Enabled = enabled,
        Program = @"C:\Something\" + name + ".exe", Protocol = FwProtocol.Any, Profiles = 0x7FFFFFFF,
    };

    [Fact]
    public void WoW_profile_allows_only_wow_programs_and_blocks_steam()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();

        var (desired, _, result) = engine.ApplyProfile(cfg, "WoW", "test", null);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.All(env.Firewall.Profiles.Values, p => Assert.Equal(new ProfileSettings(true, FwAction.Block), p));

        var allows = env.Firewall.Rules.Where(r => r.Action == FwAction.Allow).Select(r => r.Name).ToList();
        Assert.Contains("NetGuard:WoW:Wow.exe", allows);
        Assert.Contains("NetGuard:WoW:WowClassic.exe", allows);
        Assert.Contains("NetGuard:WoW:Battle.net.exe", allows);
        Assert.Contains("NetGuard:WoW:Battle.net.exe (2)", allows); // versioned folder copy
        Assert.Contains("NetGuard:WoW:Agent.exe", allows);
        Assert.Contains("NetGuard:WoW:Spotify.exe", allows);
        Assert.Contains("NetGuard:WoW:dns-udp", allows);
        Assert.Contains("NetGuard:WoW:dns-tcp", allows);

        var dns = env.Firewall.Rules.Single(r => r.Name == "NetGuard:WoW:dns-udp");
        Assert.Equal("Dnscache", dns.Service);
        Assert.Equal(FwProtocol.Udp, dns.Protocol);
        Assert.Equal("53", dns.RemotePorts);

        var blocks = env.Firewall.Rules.Where(r => r.Action == FwAction.Block).Select(r => r.Name).ToList();
        Assert.Contains("NetGuard:Always:steam.exe", blocks);
        Assert.Contains("NetGuard:Always:steamwebhelper.exe", blocks);
        Assert.DoesNotContain(env.Firewall.Rules, r => r.Name.Contains("steam", StringComparison.OrdinalIgnoreCase) && r.Action == FwAction.Allow);

        Assert.All(env.Firewall.Rules, r => Assert.Equal("NetGuard", r.Grouping));
        Assert.Empty(desired.RejectedPrograms);
        Assert.Equal("WoW", engine.LoadState().ActiveProfile);
        Assert.Contains("store.steampowered.com", HostsFile.ReadManagedHosts(File.ReadAllText(env.HostsPath)));
    }

    [Fact]
    public void Applying_twice_is_a_no_op()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "test", null);

        var plan = engine.PlanApply(cfg, engine.BuildDesired(cfg, "WoW"));
        Assert.True(plan.IsEmpty, string.Join("\n", plan.Actions.Select(a => a.Describe())));
    }

    [Fact]
    public void Offline_has_only_dns_and_service_rules_plus_steam_blocks()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        env.CreateEngine().ApplyProfile(cfg, "Offline", "test", null);

        var allows = env.Firewall.Rules.Where(r => r.Action == FwAction.Allow).ToList();
        Assert.All(allows, r => Assert.NotNull(r.Service));
        Assert.Contains(allows, r => r.Name == "NetGuard:Offline:dns-udp");
    }

    [Fact]
    public void Switching_profiles_removes_old_rules()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "test", null);
        engine.ApplyProfile(cfg, "Offline", "test", null);

        Assert.DoesNotContain(env.Firewall.Rules, r => r.Name.StartsWith("NetGuard:WoW:"));
        Assert.Contains(env.Firewall.Rules, r => r.Name == "NetGuard:Always:steam.exe");
    }

    [Fact]
    public void Maintenance_allows_everything_but_keeps_steam_blocked()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        env.CreateEngine().ApplyProfile(cfg, "Maintenance", "test", null);

        Assert.All(env.Firewall.Profiles.Values, p => Assert.Equal(FwAction.Allow, p.DefaultOutbound));
        Assert.Contains(env.Firewall.Rules, r => r.Name == "NetGuard:Always:steam.exe" && r.Action == FwAction.Block);
    }

    [Fact]
    public void Foreign_outbound_allow_rules_are_disabled_but_core_networking_and_others_untouched()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        env.Firewall.Rules.Add(Foreign("Microsoft Edge"));
        env.Firewall.Rules.Add(Foreign("Core Networking - DHCP", group: "@FirewallAPI.dll,-25000"));
        env.Firewall.Rules.Add(Foreign("Some inbound", dir: FwDirection.In));
        env.Firewall.Rules.Add(Foreign("Some block", action: FwAction.Block));
        env.Firewall.Rules.Add(Foreign("Already off", enabled: false));

        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "test", null);

        bool On(string n) => env.Firewall.Rules.Single(r => r.Name == n).Enabled;
        Assert.False(On("Microsoft Edge"));
        Assert.True(On("Core Networking - DHCP"));
        Assert.True(On("Some inbound"));
        Assert.True(On("Some block"));
        Assert.False(On("Already off"));

        var state = engine.LoadState();
        Assert.Single(state.DisabledForeignRules);
        Assert.Equal("Microsoft Edge", state.DisabledForeignRules[0].Name);

        // Recovery switches back on only what NetGuard switched off
        engine.Recover("test", "test");
        Assert.True(On("Microsoft Edge"));
        Assert.False(On("Already off"));
        Assert.Empty(engine.LoadState().DisabledForeignRules);
    }

    [Fact]
    public void Recovery_restores_normal_internet()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        var originalHosts = File.ReadAllText(env.HostsPath);
        engine.ApplyProfile(cfg, "WoW", "test", null);

        var (_, result) = engine.Recover("test", "test");

        Assert.True(result.Success);
        Assert.All(env.Firewall.Profiles.Values, p => Assert.Equal(new ProfileSettings(true, FwAction.Allow), p));
        Assert.DoesNotContain(env.Firewall.Rules, r => r.IsNetGuard);
        Assert.Equal(originalHosts, File.ReadAllText(env.HostsPath));
        Assert.Null(engine.LoadState().ActiveProfile);
        Assert.True(engine.PlanRecovery(engine.LoadState()).IsEmpty);
    }

    [Fact]
    public void Tampering_is_detected_as_drift()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "test", null);

        // Simulated tampering: firewall off, outbound allow, a rule deleted, a rule disabled, a new allow rule, hosts edited
        env.Firewall.SetEnabled(FwProfileType.Public, false);
        env.Firewall.SetDefaultOutbound(FwProfileType.Private, FwAction.Allow);
        env.Firewall.RemoveRules("NetGuard:Always:steam.exe");
        env.Firewall.Rules.Single(r => r.Name == "NetGuard:WoW:dns-udp").Enabled = false;
        env.Firewall.Rules.Add(Foreign("chrome"));
        File.WriteAllText(env.HostsPath, "127.0.0.1 localhost\r\n");

        var plan = engine.PlanApply(cfg, engine.BuildDesired(cfg, "WoW"));
        var kinds = plan.Actions.Select(a => a.GetType()).ToList();
        Assert.Contains(typeof(EnableFirewallAction), kinds);
        Assert.Contains(typeof(SetDefaultOutboundAction), kinds);
        Assert.Contains(plan.Actions, a => a is AddRuleAction ar && ar.Rule.Name == "NetGuard:Always:steam.exe");
        Assert.Contains(plan.Actions, a => a is AddRuleAction ar && ar.Rule.Name == "NetGuard:WoW:dns-udp");
        Assert.Contains(plan.Actions, a => a is DisableForeignRuleAction d && d.Name == "chrome");
        Assert.Contains(typeof(WriteHostsAction), kinds);

        engine.ApplyProfile(cfg, "WoW", "test", null);
        Assert.True(engine.PlanApply(cfg, engine.BuildDesired(cfg, "WoW")).IsEmpty);
    }

    [Fact]
    public void Renamed_browser_is_rejected_by_signature_pin()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        // "Spotify.exe" that is really Chrome
        var fake = env.P("Users", "dom", "AppData", "Roaming", "Spotify", "Spotify.exe");
        env.Verifier.Signers[fake] = "CN=Google LLC, O=Google LLC";
        // and an unsigned extra exe dropped in the Agent folder
        env.Touch("", "Agent", "Agent.9999", "Agent.exe");

        var desired = env.CreateEngine().BuildDesired(cfg, "WoW");

        Assert.Contains(desired.RejectedPrograms, r => r.Path == fake);
        Assert.Contains(desired.RejectedPrograms, r => r.Path.Contains("Agent.9999"));
        Assert.DoesNotContain(desired.Rules, r => r.Program == fake);
    }

    [Fact]
    public void Missing_programs_produce_warnings_not_errors()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        cfg.FindProfile("WoW")!.Programs.Add(new ProgramEntry { Name = "Nope", Paths = { env.P("nope", "x.exe") } });
        var desired = env.CreateEngine().BuildDesired(cfg, "WoW");
        Assert.Contains(desired.Warnings, w => w.StartsWith("Nope:"));
    }

    [Fact]
    public void Unknown_profile_throws()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        Assert.Throws<ArgumentException>(() => env.CreateEngine().BuildDesired(cfg, "Chrome"));
    }

    [Fact]
    public void Everything_is_logged()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "tester", null);
        engine.Recover("because", "tester");

        var types = engine.Log.ReadAll().Select(o => (string)o["type"]!).ToList();
        Assert.Equal(new[] { "profile-applied", "recovery" }, types);
    }
}
