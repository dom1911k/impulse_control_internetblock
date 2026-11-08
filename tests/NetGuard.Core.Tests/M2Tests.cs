using NetGuard.Core.Config;
using NetGuard.Core.Firewall;
using NetGuard.Core.Ipc;
using NetGuard.Core.Policy;

namespace NetGuard.Core.Tests;

public class ProfilePolicyTests
{
    private static readonly NetGuardConfig Cfg = DefaultConfig.Create(new DetectedPaths());

    [Theory]
    [InlineData(null, "Offline", true)]
    [InlineData(null, "WoW", true)]
    [InlineData(null, "Maintenance", true)]
    [InlineData("Maintenance", "WoW", true)]
    [InlineData("Maintenance", "Offline", true)]
    [InlineData("WoW", "Offline", true)]
    [InlineData("WoW", "WoW", true)]
    [InlineData("Offline", "WoW", false)]
    [InlineData("Offline", "Maintenance", false)]
    [InlineData("WoW", "Maintenance", false)]
    public void Only_stricter_switches_are_instant(string? from, string to, bool allowed) =>
        Assert.Equal(allowed, ProfilePolicy.CanSwitch(Cfg, from, to).Allowed);

    [Fact]
    public void Unknown_profile_is_refused() => Assert.False(ProfilePolicy.CanSwitch(Cfg, null, "Chrome").Allowed);

    [Fact]
    public void Test_locks_only_from_normal_internet()
    {
        Assert.True(ProfilePolicy.CanStartTest(null).Allowed);
        Assert.False(ProfilePolicy.CanStartTest("WoW").Allowed);
    }
}

public class HealTests
{
    [Fact]
    public void Nothing_to_heal_without_active_profile()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        Assert.Null(env.CreateEngine().Heal(cfg));
    }

    [Fact]
    public void In_sync_firewall_needs_no_healing()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "test", null);
        var r = engine.Heal(cfg)!;
        Assert.True(r.Plan.IsEmpty);
        Assert.DoesNotContain(engine.Log.ReadAll(), o => (string?)o["type"] == "tamper");
    }

    [Fact]
    public void Deleted_rule_and_firewall_off_are_tamper_and_get_fixed()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "test", null);

        env.Firewall.RemoveRules("NetGuard:WoW:dns-udp");
        env.Firewall.SetEnabled(FwProfileType.Public, false);
        env.Firewall.SetDefaultOutbound(FwProfileType.Private, FwAction.Allow);

        var r = engine.Heal(cfg)!;
        Assert.True(r.WasTampered);
        Assert.True(r.Result.Success);
        Assert.True(engine.PlanApply(cfg, engine.BuildDesired(cfg, "WoW")).IsEmpty);
        Assert.Contains(engine.Log.ReadAll(), o => (string?)o["type"] == "tamper");
        Assert.NotNull(engine.LoadState().LastTamperUtc);
    }

    [Fact]
    public void New_allow_rule_from_someone_else_is_tamper()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "test", null);
        env.Firewall.Rules.Add(new FwRule { Name = "Allow chrome", Direction = FwDirection.Out, Action = FwAction.Allow, Enabled = true, Program = @"C:\chrome.exe" });

        Assert.True(engine.Heal(cfg)!.WasTampered);
        Assert.False(env.Firewall.Rules.Single(r => r.Name == "Allow chrome").Enabled);
    }

    [Fact]
    public void Fake_netguard_rule_is_tamper()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "test", null);
        env.Firewall.AddRule(new RuleSpec("NetGuard:WoW:chrome.exe", FwAction.Allow, @"C:\chrome.exe", null, FwProtocol.Any, null, ""));

        Assert.True(engine.Heal(cfg)!.WasTampered);
        Assert.DoesNotContain(env.Firewall.Rules, r => r.Name == "NetGuard:WoW:chrome.exe");
    }

    [Fact]
    public void Program_update_with_new_versioned_folder_is_not_tamper()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var engine = env.CreateEngine();
        engine.ApplyProfile(cfg, "WoW", "test", null);

        // Blizzard Agent updates: new versioned folder appears, old one disappears
        env.Touch("CN=Blizzard Entertainment, Inc.", "Agent", "Agent.9100", "Agent.exe");
        Directory.Delete(env.P("Agent", "Agent.9000"), true);

        var r = engine.Heal(cfg)!;
        Assert.False(r.WasTampered);
        Assert.False(r.Plan.IsEmpty);
        Assert.Contains(env.Firewall.Rules, x => x.Program!.Contains("Agent.9100"));
        Assert.DoesNotContain(env.Firewall.Rules, x => x.Program!.Contains("Agent.9000"));
        Assert.Contains(engine.Log.ReadAll(), o => (string?)o["type"] == "rules-updated");
    }
}

public class IpcTests
{
    [Fact]
    public void Messages_round_trip_as_single_json_lines()
    {
        var req = new IpcRequest { Command = IpcCommands.Switch, Profile = "WoW" };
        var line = IpcJson.Serialize(req);
        Assert.DoesNotContain('\n', line);
        Assert.Equal("WoW", IpcJson.Deserialize<IpcRequest>(line)!.Profile);

        var resp = new IpcResponse { Ok = true, Status = new StatusDto { ActiveProfile = "WoW", Profiles = { new ProfileDto { Name = "Offline" } } } };
        var back = IpcJson.Deserialize<IpcResponse>(IpcJson.Serialize(resp))!;
        Assert.Equal("Offline", back.Status!.Profiles[0].Name);
    }
}

public class RecoveryCodeTests
{
    [Fact]
    public void Generated_codes_look_right_and_differ()
    {
        var a = Security.RecoveryCode.Generate();
        var b = Security.RecoveryCode.Generate();
        Assert.Matches("^[A-Z2-9]{5}(-[A-Z2-9]{5}){5}$", a);
        Assert.DoesNotMatch("[01OI]", a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Verifies_ignoring_case_spaces_and_dashes_and_stores_no_plain_code()
    {
        var state = new State.NetGuardState();
        var code = Security.RecoveryCode.Generate();
        Security.RecoveryCode.Store(state, code);

        Assert.DoesNotContain(code.Replace("-", ""), state.RecoveryCodeHash + state.RecoveryCodeSalt);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(Security.RecoveryCode.CheckResult.Ok, Security.RecoveryCode.Check(state, code.ToLowerInvariant().Replace("-", " "), now));
        Assert.Equal(Security.RecoveryCode.CheckResult.Wrong, Security.RecoveryCode.Check(state, "AAAAA-AAAAA", now));
    }

    [Fact]
    public void Too_many_wrong_attempts_lock_for_an_hour()
    {
        var state = new State.NetGuardState();
        var code = Security.RecoveryCode.Generate();
        Security.RecoveryCode.Store(state, code);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < Security.RecoveryCode.MaxWrongAttemptsPerHour; i++)
            Assert.Equal(Security.RecoveryCode.CheckResult.Wrong, Security.RecoveryCode.Check(state, "nope", now));

        Assert.Equal(Security.RecoveryCode.CheckResult.TooManyAttempts, Security.RecoveryCode.Check(state, code, now.AddMinutes(30)));
        Assert.Equal(Security.RecoveryCode.CheckResult.Ok, Security.RecoveryCode.Check(state, code, now.AddMinutes(61)));
    }

    [Fact]
    public void No_code_yet() =>
        Assert.Equal(Security.RecoveryCode.CheckResult.NoCode, Security.RecoveryCode.Check(new State.NetGuardState(), "x", DateTimeOffset.UtcNow));
}
