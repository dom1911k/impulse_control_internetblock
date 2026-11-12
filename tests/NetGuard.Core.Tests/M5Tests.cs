using System.Text.Json.Nodes;
using NetGuard.Core.Config;
using NetGuard.Core.Reporting;
using NetGuard.Core.State;

namespace NetGuard.Core.Tests;

public class WeeklySummaryTests
{
    private static DateTimeOffset At(int day, int h) => new(2026, 11, 9 + day, h, 0, 0, TimeSpan.Zero); // Monday 9.11.

    private static JsonObject Log(DateTimeOffset ts, string type, object? data = null) => new()
    {
        ["ts"] = ts.ToString("o"),
        ["type"] = type,
        ["data"] = data == null ? null : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(data)),
    };

    [Fact]
    public void Summarises_a_week_and_leaves_out_practice()
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        cfg.FindProfile("WoW")!.Plan = new PlayPlan { WeeklyLimitMinutes = 600 };
        var st = new NetGuardState
        {
            PlayUsage = { ["WoW"] = new Dictionary<string, int> { ["2026-11-09"] = 90 * 60, ["2026-11-11"] = 30 * 60, ["2026-11-02"] = 999 } },
            UnlockRequests =
            {
                new UnlockRequest { TargetProfile = "Maintenance", Reason = "Windows update please", RequestedUtc = At(1, 10), Status = UnlockStatus.Expired },
                new UnlockRequest { TargetProfile = "Maintenance", Reason = "last week", RequestedUtc = At(-3, 10), Status = UnlockStatus.Confirmed },
            },
        };
        var log = new List<JsonObject>
        {
            Log(At(-1, 0), "profile-applied", new { profile = "Offline" }),   // Sunday before: Offline from then on
            Log(At(0, 19), "profile-applied", new { profile = "WoW" }),
            Log(At(0, 21), "profile-applied", new { profile = "Offline" }),
            Log(At(1, 12), "tamper"),
            Log(At(2, 12), "practice-started"),
            Log(At(2, 12), "commitment"),                                      // inside practice: not counted
            Log(At(2, 13), "practice-ended"),
            Log(At(3, 12), "commitment"),
            Log(At(4, 12), "recovery", new { reason = "emergency restore with recovery code" }),
        };

        var s = WeeklySummary.Build(cfg, st, log, At(6, 12), 0, TimeZoneInfo.Utc);

        Assert.Equal(new DateTime(2026, 11, 9), s.WeekStart);
        var wow = s.Play.Single(p => p.Profile == "WoW");
        Assert.Equal(new[] { 90, 0, 30, 0, 0, 0, 0 }, wow.MinutesPerDay);
        Assert.Equal(600, wow.WeeklyLimitMinutes);
        Assert.Single(s.Unlocks);
        Assert.Equal("let it expire", s.Unlocks[0].Outcome);
        Assert.Equal(1, s.Commitments);
        Assert.Equal(1, s.TamperUndone);
        Assert.Equal(1, s.EmergencyRestores);
        Assert.Equal(120, s.MinutesPerProfile["WoW"]);
        Assert.Equal(19 * 60 + (4 * 24 - 9) * 60, s.MinutesPerProfile["Offline"]);  // Mon 0–19 + Mon 21 … Fri 12
        Assert.True(s.MinutesPerProfile[WeeklySummary.NormalInternet] > 0);          // after the emergency restore

        var last = WeeklySummary.Build(cfg, st, log, At(6, 12), -1, TimeZoneInfo.Utc);
        Assert.Equal("Maintenance", last.Unlocks.Single().Target);
    }
}

public class StatsTests
{
    private static DateTimeOffset At(int day, int h) => new DateTimeOffset(2026, 11, 9, h, 0, 0, TimeSpan.Zero).AddDays(day); // Monday 9.11.

    private static JsonObject Log(DateTimeOffset ts, string type, object? data = null) => new()
    {
        ["ts"] = ts.ToString("o"),
        ["type"] = type,
        ["data"] = data == null ? null : JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(data)),
    };

    [Fact]
    public void Dashboard_numbers()
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        cfg.FindProfile("WoW")!.Plan = new PlayPlan { DailyLimitMinutes = 120 };
        var st = new NetGuardState
        {
            PlayUsage =
            {
                ["WoW"] = new() { ["2026-11-11"] = 60 * 60, ["2026-11-10"] = 30 * 60, ["2026-11-01"] = 120 * 60, ["2026-09-01"] = 600 * 60 },
                ["Offline"] = new() { ["2026-11-11"] = 300 * 60 },
            },
            GameRunning = { ["WoW"] = new() { ["2026-11-11"] = 45 * 60 } },
            UnlockRequests = { new UnlockRequest { RequestedUtc = At(-5, 10), Status = UnlockStatus.Expired } },
        };
        var log = new List<JsonObject>
        {
            Log(At(-10, 9), "profile-applied", new { profile = "Offline" }),
            Log(At(1, 19), "profile-applied", new { profile = "WoW" }),
            Log(At(1, 20), "profile-applied", new { profile = "Offline" }),
            Log(At(2, 18), "profile-applied", new { profile = "WoW" }),
            Log(At(2, 19), "practice-started"),
            Log(At(2, 19), "commitment"),
            Log(At(2, 20), "practice-ended"),
            Log(At(2, 21), "profile-applied", new { profile = "Offline" }),
        };

        var s = StatsBuilder.Build(cfg, st, log, At(2, 22), TimeZoneInfo.Utc);   // Wednesday 11.11.

        var wow = s.Games.Single();
        Assert.Equal(60, wow.TodayMinutes);
        Assert.Equal(90, wow.WeekMinutes);
        Assert.Equal(210, wow.MonthMinutes);
        Assert.Equal(810, wow.AllTimeMinutes);
        Assert.Equal(7, wow.Average30DaysMinutes);   // 210 / 30
        Assert.Equal(45, wow.RunningTodayMinutes);
        Assert.Equal(30, wow.Last30Days.Count);
        Assert.Equal(60, wow.Last30Days[^1].Minutes);
        Assert.Equal(2, wow.Sessions30Days);
        Assert.Equal(180, wow.LongestSessionMinutes);
        Assert.Equal(300, s.ProfileMinutes7Days["Offline"]);
        Assert.Equal(7, s.DaysWithoutUnlockRequest);
        Assert.Equal(0, s.Commitments30Days);          // the practice one doesn't count
        Assert.Equal(new DateTime(2026, 10, 30, 9, 0, 0), s.ProtectingSince);
    }
}

public class BrowseWindowTests
{
    private static readonly NetGuard.Core.Rules.LockRules Rules = new(TimeZoneInfo.Utc);
    private static DateTimeOffset At(int h, int m = 0) => new(2026, 11, 12, h, m, 0, TimeSpan.Zero);

    [Fact]
    public void Twenty_minutes_twice_a_day_on_top_of_a_lock()
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        Assert.False(Rules.CanStartBrowseWindow(cfg, new NetGuardState(), At(10)).Allowed);                                   // normal internet
        Assert.False(Rules.CanStartBrowseWindow(cfg, new NetGuardState { ActiveProfile = "Maintenance" }, At(10)).Allowed);

        var st = new NetGuardState { ActiveProfile = "WoW" };
        Assert.Equal(At(10, 20), Rules.StartBrowseWindow(cfg, st, At(10)));
        Assert.True(Rules.BrowseWindowOpen(st, At(10, 19)));
        Assert.False(Rules.CanStartBrowseWindow(cfg, st, At(10, 5)).Allowed);   // already open
        var r = Rules.Tick(cfg, st, At(10, 20), TimeSpan.Zero);
        Assert.Contains(r.Events, e => e.Type == "window-ended");
        Assert.Null(st.BrowseWindowUntilUtc);

        Rules.StartBrowseWindow(cfg, st, At(14));
        st.BrowseWindowUntilUtc = null;
        Assert.False(Rules.CanStartBrowseWindow(cfg, st, At(18)).Allowed);      // 2 per day used
        Assert.True(Rules.CanStartBrowseWindow(cfg, st, At(18).AddDays(1)).Allowed);
    }

    [Fact]
    public void Not_during_a_commitment()
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        var st = new NetGuardState { ActiveProfile = "Offline", Commitment = new Commitment { Profile = "Offline", UntilUtc = At(20) } };
        Assert.False(Rules.CanStartBrowseWindow(cfg, st, At(10)).Allowed);
    }

    [Theory]
    [InlineData("https://www.songsterr.com/a/wsa/foo-tab", "songsterr.com")]
    [InlineData("ignition4.customsforge.com", "ignition4.customsforge.com")]
    [InlineData("Dropbox.com/s/abc?dl=1", "dropbox.com")]
    public void Website_entries_are_normalised(string input, string expected) =>
        Assert.Equal(expected, NetGuard.Core.Editing.ConfigEditor.NormalizeSite(input));

    [Fact]
    public void Nonsense_websites_are_refused() =>
        Assert.Throws<InvalidOperationException>(() => NetGuard.Core.Editing.ConfigEditor.NormalizeSite("not a site"));

    [Fact]
    public void Window_programs_get_allow_rules_on_top_of_the_profile()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        var chrome = env.Touch("CN=Google LLC, O=Google LLC", "Google", "chrome.exe");
        cfg.BrowseWindow = new BrowseWindowConfig { Programs = { new ProgramEntry { Name = "Chrome", Paths = { chrome }, Pin = PinMode.Signature, Signer = "Google LLC" } } };
        var engine = env.CreateEngine();
        engine.ExtraAllowed = c => c.BrowseWindow!.Programs;
        engine.ApplyProfile(cfg, "Offline", "test", null);
        Assert.Contains(env.Firewall.Rules, r => r.Name == "NetGuard:Window:chrome.exe" && r.Action == NetGuard.Core.Firewall.FwAction.Allow);

        engine.ExtraAllowed = null;
        var heal = engine.Heal(cfg)!;
        Assert.False(heal.WasTampered);
        Assert.DoesNotContain(env.Firewall.Rules, r => r.Name.StartsWith("NetGuard:Window:"));
    }
}
