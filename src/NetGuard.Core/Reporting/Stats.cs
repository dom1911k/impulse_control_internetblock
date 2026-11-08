using System.Globalization;
using System.Text.Json.Nodes;
using NetGuard.Core.Config;
using NetGuard.Core.Logging;
using NetGuard.Core.State;

namespace NetGuard.Core.Reporting;

public sealed class StatsDto
{
    public DateTime? ProtectingSince { get; set; }
    public List<GameStats> Games { get; set; } = new();
    /// <summary>Minutes per profile over the last 7 days.</summary>
    public Dictionary<string, int> ProfileMinutes7Days { get; set; } = new();
    public int UnlockRequestsTotal { get; set; }
    public int UnlockRequests30Days { get; set; }
    public int UnlocksConfirmed { get; set; }
    public int UnlocksExpiredOrCancelled { get; set; }
    /// <summary>Days in a row without an unlock request (today included).</summary>
    public int DaysWithoutUnlockRequest { get; set; }
    public int Commitments30Days { get; set; }
    public int TamperUndone30Days { get; set; }
    public int EmergencyRestores { get; set; }
    public int? DaysSinceEmergencyRestore { get; set; }
}

public sealed class GameStats
{
    public string Profile { get; set; } = "";
    public int TodayMinutes { get; set; }
    public int WeekMinutes { get; set; }
    public int MonthMinutes { get; set; }
    public int AllTimeMinutes { get; set; }
    public int Average30DaysMinutes { get; set; }
    public int? DailyLimitMinutes { get; set; }
    public int? WeeklyLimitMinutes { get; set; }
    /// <summary>Oldest first, 30 entries ending today.</summary>
    public List<DayStat> Last30Days { get; set; } = new();
    public int RunningTodayMinutes { get; set; }
    public int RunningWeekMinutes { get; set; }
    public int RunningAllTimeMinutes { get; set; }
    public int Sessions30Days { get; set; }
    public int LongestSessionMinutes { get; set; }
}

public sealed class DayStat
{
    public DateTime Date { get; set; }
    /// <summary>Profile active.</summary>
    public int Minutes { get; set; }
    /// <summary>Game actually running.</summary>
    public int RunningMinutes { get; set; }
}

/// <summary>Numbers for the dashboard. Practice-mode periods are left out.</summary>
public static class StatsBuilder
{
    public static bool IsGameProfile(ProfileDefinition p) =>
        p.Plan != null || p.Programs.Any(e => e.Name.Contains("Warcraft", StringComparison.OrdinalIgnoreCase));

    public static StatsDto Build(NetGuardConfig? cfg, NetGuardState state, IEnumerable<JsonObject> log, DateTimeOffset nowUtc, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        DateTime Local(DateTimeOffset u) => TimeZoneInfo.ConvertTime(u, tz).DateTime;
        var today = Local(nowUtc).Date;
        var weekStart = WeeklySummary.WeekStartOf(today);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        static int Min(Dictionary<string, Dictionary<string, int>> u, string p, DateTime d) =>
            u.TryGetValue(p, out var days) && days.TryGetValue(Key(d), out var s) ? s / 60 : 0;
        static int Sum(Dictionary<string, Dictionary<string, int>> u, string p, Func<DateTime, bool> include) =>
            u.TryGetValue(p, out var days)
                ? days.Where(kv => DateTime.TryParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && include(d)).Sum(kv => kv.Value) / 60
                : 0;

        var entries = log.Select(o => (ts: DateTimeOffset.TryParse((string?)o["ts"], out var t) ? t : DateTimeOffset.MinValue, type: (string?)o["type"] ?? "", data: o["data"] as JsonObject))
            .Where(e => e.ts != DateTimeOffset.MinValue).OrderBy(e => e.ts).ToList();
        var practice = WeeklySummary.PracticeIntervals(entries.Select(e => (e.ts, e.type)));
        bool InPractice(DateTimeOffset t) => practice.Any(p => t >= p.from && t <= p.to);
        var real = entries.Where(e => !InPractice(e.ts)).ToList();

        var dto = new StatsDto { ProtectingSince = entries.Count > 0 ? Local(entries[0].ts) : null };

        // ---- games
        foreach (var p in cfg?.Profiles.Where(IsGameProfile) ?? Enumerable.Empty<ProfileDefinition>())
        {
            var g = new GameStats
            {
                Profile = p.Name,
                TodayMinutes = Min(state.PlayUsage, p.Name, today),
                WeekMinutes = Sum(state.PlayUsage, p.Name, d => d >= weekStart && d <= today),
                MonthMinutes = Sum(state.PlayUsage, p.Name, d => d >= monthStart && d <= today),
                AllTimeMinutes = Sum(state.PlayUsage, p.Name, _ => true),
                Average30DaysMinutes = Sum(state.PlayUsage, p.Name, d => d > today.AddDays(-30) && d <= today) / 30,
                DailyLimitMinutes = p.Plan?.DailyLimitMinutes,
                WeeklyLimitMinutes = p.Plan?.WeeklyLimitMinutes,
                RunningTodayMinutes = Min(state.GameRunning, p.Name, today),
                RunningWeekMinutes = Sum(state.GameRunning, p.Name, d => d >= weekStart && d <= today),
                RunningAllTimeMinutes = Sum(state.GameRunning, p.Name, _ => true),
            };
            for (var i = 29; i >= 0; i--)
            {
                var d = today.AddDays(-i);
                g.Last30Days.Add(new DayStat { Date = d, Minutes = Min(state.PlayUsage, p.Name, d), RunningMinutes = Min(state.GameRunning, p.Name, d) });
            }

            // sessions: stretches where this profile was active (from the log)
            DateTimeOffset? start = null;
            var from30 = nowUtc.AddDays(-30);
            foreach (var e in real.Where(e => e.type is LogTypes.ProfileApplied or LogTypes.Recovery))
            {
                var isThis = e.type == LogTypes.ProfileApplied && string.Equals((string?)e.data?["profile"], p.Name, StringComparison.OrdinalIgnoreCase);
                if (isThis && start == null) start = e.ts;
                else if (!isThis && start != null)
                {
                    if (e.ts >= from30) { g.Sessions30Days++; g.LongestSessionMinutes = Math.Max(g.LongestSessionMinutes, (int)(e.ts - start.Value).TotalMinutes); }
                    start = null;
                }
            }
            if (start != null) { g.Sessions30Days++; g.LongestSessionMinutes = Math.Max(g.LongestSessionMinutes, (int)(nowUtc - start.Value).TotalMinutes); }
            dto.Games.Add(g);
        }

        // ---- time per profile, last 7 days
        foreach (var (profile, _) in state.PlayUsage)
        {
            var m = Sum(state.PlayUsage, profile, d => d > today.AddDays(-7) && d <= today);
            if (m > 0) dto.ProfileMinutes7Days[profile] = m;
        }

        // ---- unlocks (state: practice ones were rolled back)
        var unlocks = state.UnlockRequests;
        dto.UnlockRequestsTotal = unlocks.Count;
        dto.UnlockRequests30Days = unlocks.Count(u => u.RequestedUtc > nowUtc.AddDays(-30));
        dto.UnlocksConfirmed = unlocks.Count(u => u.Status == UnlockStatus.Confirmed);
        dto.UnlocksExpiredOrCancelled = unlocks.Count(u => u.Status is UnlockStatus.Expired or UnlockStatus.Cancelled);
        var lastRequest = unlocks.Count > 0 ? Local(unlocks.Max(u => u.RequestedUtc)).Date : (DateTime?)null;
        var firstDay = dto.ProtectingSince?.Date ?? today;
        dto.DaysWithoutUnlockRequest = lastRequest is { } lr ? (today - lr).Days : (today - firstDay).Days + 1;

        // ---- the log
        dto.Commitments30Days = real.Count(e => e.type == LogTypes.CommitmentMade && e.ts > nowUtc.AddDays(-30));
        dto.TamperUndone30Days = real.Count(e => e.type == LogTypes.Tamper && e.ts > nowUtc.AddDays(-30));
        var emergencies = real.Where(e => e.type == LogTypes.Recovery &&
                                          ((string?)e.data?["reason"])?.Contains("emergency", StringComparison.OrdinalIgnoreCase) == true).ToList();
        dto.EmergencyRestores = emergencies.Count;
        dto.DaysSinceEmergencyRestore = emergencies.Count > 0 ? (today - Local(emergencies[^1].ts).Date).Days : null;
        return dto;
    }
}
