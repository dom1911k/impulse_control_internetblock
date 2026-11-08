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
