using System.Globalization;
using System.Text.Json.Nodes;
using NetGuard.Core.Logging;
using NetGuard.Core.State;

namespace NetGuard.Core.Reporting;

public sealed class SummaryDto
{
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd { get; set; }
    public List<PlaySummary> Play { get; set; } = new();
    /// <summary>Minutes spent in each profile ("Normal internet" when none was active).</summary>
    public Dictionary<string, int> MinutesPerProfile { get; set; } = new();
    public List<UnlockSummary> Unlocks { get; set; } = new();
    public int Commitments { get; set; }
    public int ExtraMinutesTaken { get; set; }
    public int TamperUndone { get; set; }
    public int EmergencyRestores { get; set; }
    public int WrongCodes { get; set; }
    public int ClockChanges { get; set; }
    public int AutoSwitches { get; set; }
}

public sealed class PlaySummary
{
    public string Profile { get; set; } = "";
    /// <summary>Monday … Sunday.</summary>
    public List<int> MinutesPerDay { get; set; } = new();
    public int TotalMinutes { get; set; }
    public int? WeeklyLimitMinutes { get; set; }
    public int? DailyLimitMinutes { get; set; }
}

public sealed class UnlockSummary
{
    public DateTime RequestedLocal { get; set; }
    public string Target { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Outcome { get; set; } = "";
}

/// <summary>Builds the weekly summary from the state and the log. Practice-mode periods are left out.</summary>
public static class WeeklySummary
{
    public const string NormalInternet = "Normal internet";

    public static DateTime WeekStartOf(DateTime local) => local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7));

    public static SummaryDto Build(Config.NetGuardConfig? cfg, NetGuardState state, IEnumerable<JsonObject> log, DateTimeOffset nowUtc,
        int weekOffset = 0, TimeZoneInfo? tz = null)
    {
        tz ??= TimeZoneInfo.Local;
        DateTime Local(DateTimeOffset u) => TimeZoneInfo.ConvertTime(u, tz).DateTime;
        DateTimeOffset Utc(DateTime l) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(l, DateTimeKind.Unspecified), tz), TimeSpan.Zero);

        var start = WeekStartOf(Local(nowUtc)).AddDays(7 * weekOffset);
        var end = start.AddDays(7);
        var startUtc = Utc(start);
        var endUtc = Utc(end);
        var dto = new SummaryDto { WeekStart = start, WeekEnd = end.AddDays(-1) };

        // ---- play time (state)
        foreach (var (profile, days) in state.PlayUsage)
        {
            var perDay = Enumerable.Range(0, 7)
                .Select(i => days.TryGetValue(start.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var s) ? s / 60 : 0).ToList();
            var plan = cfg?.FindProfile(profile)?.Plan;
            if (perDay.Sum() == 0 && plan == null) continue;
            dto.Play.Add(new PlaySummary
            {
                Profile = profile, MinutesPerDay = perDay, TotalMinutes = perDay.Sum(),
                WeeklyLimitMinutes = plan?.WeeklyLimitMinutes, DailyLimitMinutes = plan?.DailyLimitMinutes,
            });
        }
        foreach (var p in cfg?.Profiles.Where(p => p.Plan != null && !dto.Play.Any(x => x.Profile.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) ?? Enumerable.Empty<Config.ProfileDefinition>())
            dto.Play.Add(new PlaySummary { Profile = p.Name, MinutesPerDay = Enumerable.Repeat(0, 7).ToList(), WeeklyLimitMinutes = p.Plan!.WeeklyLimitMinutes, DailyLimitMinutes = p.Plan.DailyLimitMinutes });

        // ---- unlock requests (state; practice ones were rolled back)
        foreach (var u in state.UnlockRequests.Where(u => u.RequestedUtc >= startUtc && u.RequestedUtc < endUtc).OrderBy(u => u.RequestedUtc))
            dto.Unlocks.Add(new UnlockSummary
            {
                RequestedLocal = Local(u.RequestedUtc), Target = u.TargetProfile, Reason = u.Reason,
                Outcome = u.Status switch
                {
                    UnlockStatus.Confirmed => "confirmed",
                    UnlockStatus.Expired => "let it expire",
                    UnlockStatus.Cancelled => "cancelled",
                    _ => "waiting",
                },
            });

        // ---- the log: counts and time per profile, without practice periods
        var entries = log.Select(o => (ts: DateTimeOffset.TryParse((string?)o["ts"], out var t) ? t : DateTimeOffset.MinValue, type: (string?)o["type"] ?? "", data: o["data"] as JsonObject))
            .Where(e => e.ts != DateTimeOffset.MinValue).OrderBy(e => e.ts).ToList();

        var practice = new List<(DateTimeOffset from, DateTimeOffset to)>();
        DateTimeOffset? practiceStart = null;
        foreach (var e in entries)
        {
            if (e.type == LogTypes.PracticeStarted) practiceStart = e.ts;
            else if (e.type == LogTypes.PracticeEnded && practiceStart != null) { practice.Add((practiceStart.Value, e.ts)); practiceStart = null; }
        }
        if (practiceStart != null) practice.Add((practiceStart.Value, DateTimeOffset.MaxValue));
        bool InPractice(DateTimeOffset t) => practice.Any(p => t >= p.from && t <= p.to);

        string? current = null;
        var since = DateTimeOffset.MinValue;
        void Add(string? profile, DateTimeOffset from, DateTimeOffset to)
        {
            var a = from < startUtc ? startUtc : from;
            var b = to > endUtc ? endUtc : to;
            if (b <= a) return;
            var minutes = (int)(b - a).TotalMinutes;
            var key = profile ?? NormalInternet;
            dto.MinutesPerProfile[key] = (dto.MinutesPerProfile.TryGetValue(key, out var m) ? m : 0) + minutes;
        }

        foreach (var e in entries)
        {
            if (e.ts >= endUtc) break;
            var inWeek = e.ts >= startUtc;
            var practising = InPractice(e.ts);

            if (e.type == LogTypes.ProfileApplied || e.type == LogTypes.Recovery)
            {
                if (since != DateTimeOffset.MinValue) Add(current, since, e.ts);
                current = e.type == LogTypes.Recovery ? null : (string?)e.data?["profile"];
                since = e.ts;
            }
            if (!inWeek || practising) continue;

            switch (e.type)
            {
                case LogTypes.CommitmentMade: dto.Commitments++; break;
                case LogTypes.GraceTaken: dto.ExtraMinutesTaken++; break;
                case LogTypes.Tamper: dto.TamperUndone++; break;
                case LogTypes.RecoveryCodeWrong: dto.WrongCodes++; break;
                case LogTypes.ClockJump: dto.ClockChanges++; break;
                case LogTypes.AutoSwitch: dto.AutoSwitches++; break;
                case LogTypes.Recovery when ((string?)e.data?["reason"])?.Contains("emergency", StringComparison.OrdinalIgnoreCase) == true:
                    dto.EmergencyRestores++; break;
            }
        }
        if (since != DateTimeOffset.MinValue) Add(current, since, nowUtc < endUtc ? nowUtc : endUtc);
        return dto;
    }
}
