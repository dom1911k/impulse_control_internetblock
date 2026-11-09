using System.Globalization;
using NetGuard.Core.Config;
using NetGuard.Core.Policy;
using NetGuard.Core.State;

namespace NetGuard.Core.Rules;

public sealed record PlayStatus(
    bool Allowed,
    DateTimeOffset? EndsUtc,
    DateTimeOffset? NextStartUtc,
    int UsedTodayMinutes,
    int? DailyLimitMinutes,
    int UsedWeekMinutes,
    int? WeeklyLimitMinutes,
    bool GraceAvailable,
    bool GraceActive,
    string Reason,
    DateTimeOffset? WindowEndsUtc = null,
    int? MinutesLeftToday = null);

public sealed record RuleEvent(string Type, object Data);

public sealed class TickResult
{
    public string? SwitchTo { get; set; }
    public string? SwitchReason { get; set; }
    public bool ConfigChanged { get; set; }
    public bool StateChanged { get; set; }
    public List<RuleEvent> Events { get; } = new();
}

/// <summary>
/// All time-based rules in one place, without any I/O: switching, unlock requests, commitments,
/// play-time plans, grace, plan changes. The service feeds it NetGuard's trusted clock and acts on
/// the results; the tests feed it any time they like.
/// </summary>
public sealed class LockRules
{
    private readonly TimeZoneInfo _tz;

    public LockRules(TimeZoneInfo? tz = null) => _tz = tz ?? TimeZoneInfo.Local;

    // ================================================================ practice mode

    /// <summary>
    /// Practice mode: all waiting times are short and play time counts 10× faster, so everything can be
    /// tried out in a few minutes. Only startable with normal internet; the service undoes its effects afterwards.
    /// </summary>
    public bool Practice { get; set; }
    public const int PracticeSpeed = 10;
    private int Speed => Practice ? PracticeSpeed : 1;

    public TimingSettings T(NetGuardConfig cfg) => Practice ? PracticeTimings(cfg.Timings) : cfg.Timings;

    public static TimingSettings PracticeTimings(TimingSettings t) => new()
    {
        TestModeMinutes = t.TestModeMinutes,
        HealIntervalSeconds = t.HealIntervalSeconds,
        CooldownMinutes = 1,
        ConfirmWindowMinutes = 3,
        MaxUnlockRequestsPerDay = t.MaxUnlockRequestsPerDay,
        MinReasonLength = t.MinReasonLength,
        ConfirmPassageLength = 60,
        MaintenanceMaxMinutes = 2,
        MaintenanceReminderDays = t.MaintenanceReminderDays,
        GraceMinutes = 1,
        PlanLoosenDelayHours = t.PlanLoosenDelayHours,
        MaxCommitmentDays = t.MaxCommitmentDays,
    };

    private TimeSpan PlanDelay(NetGuardConfig cfg) => Practice ? TimeSpan.FromMinutes(2) : TimeSpan.FromHours(cfg.Timings.PlanLoosenDelayHours);

    // ================================================================ time helpers

    private DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _tz).DateTime;

    private DateTimeOffset Utc(DateTime local) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _tz), TimeSpan.Zero);

    public static string DateKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime WeekStart(DateTime local)
    {
        var diff = ((int)local.DayOfWeek + 6) % 7; // Monday = 0
        return local.Date.AddDays(-diff);
    }

    public string LocalText(DateTimeOffset utc)
    {
        var l = Local(utc);
        return l.Date == Local(DateTimeOffset.UtcNow).Date ? l.ToString("HH:mm") : l.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    // ================================================================ play plans

    public PlayStatus GetPlayStatus(NetGuardConfig cfg, NetGuardState state, ProfileDefinition profile, DateTimeOffset now)
    {
        var plan = profile.Plan ?? throw new InvalidOperationException($"{profile.Name} has no play plan.");
        var local = Local(now);
        var today = local.Date;
        var minute = local.Hour * 60 + local.Minute;

        int Used(DateTime day) =>
            state.PlayUsage.TryGetValue(profile.Name, out var d) && d.TryGetValue(DateKey(day), out var s) ? s : 0;

        int? DailyLimit(DateTime day)
        {
            if (plan.DailyLimitMinutes is not { } lim) return null;
            var debt = state.Graces.Any(g => g.Profile.Equals(profile.Name, StringComparison.OrdinalIgnoreCase) && g.Date == DateKey(day.AddDays(-1)))
                ? T(cfg).GraceMinutes : 0;
            return Math.Max(0, lim - debt);
        }

        var usedTodaySec = Used(today);
        var weekStart = WeekStart(local);
        var usedWeekSec = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).Where(d => d <= today).Sum(Used);
        var dailyLimit = DailyLimit(today);

        var window = plan.Windows.Where(w => w.Day == local.DayOfWeek && w.StartMinute <= minute && minute < w.EndMinute)
            .OrderByDescending(w => w.EndMinute).FirstOrDefault();

        var remDaily = dailyLimit is { } dl ? dl * 60 - usedTodaySec : int.MaxValue;
        var remWeek = plan.WeeklyLimitMinutes is { } wl ? wl * 60 - usedWeekSec : int.MaxValue;

        DateTimeOffset? normalEnd = null;
        string reason;
        if (window == null) reason = "Outside your play time.";
        else if (remDaily <= 0) reason = "Today's play time is used up.";
        else if (remWeek <= 0) reason = "This week's play time is used up.";
        else
        {
            reason = "Play time.";
            var end = Utc(today.AddMinutes(window.EndMinute));
            if (remDaily != int.MaxValue) end = Min(end, now.AddSeconds((double)remDaily / Speed));
            if (remWeek != int.MaxValue) end = Min(end, now.AddSeconds((double)remWeek / Speed));
            normalEnd = end;
        }

        var grace = state.Graces.FirstOrDefault(g => g.Profile.Equals(profile.Name, StringComparison.OrdinalIgnoreCase) && g.Date == DateKey(today));
        var graceActive = grace != null && now < grace.UntilUtc;
        var allowed = normalEnd != null || graceActive;
        DateTimeOffset? ends = graceActive ? Max(normalEnd ?? grace!.UntilUtc, grace!.UntilUtc) : normalEnd;
        var graceAvailable = grace == null && normalEnd is { } ne && ne - now <= TimeSpan.FromMinutes(Practice ? 3 : 15);

        DateTimeOffset? next = null;
        if (!allowed)
        {
            for (var d = 0; d < 8 && next == null; d++)
            {
                var day = today.AddDays(d);
                var weekOk = plan.WeeklyLimitMinutes == null || WeekStart(day) != weekStart || remWeek > 0;
                var dayOk = d > 0 ? (DailyLimit(day) ?? 1) > 0 : remDaily > 0;
                if (!weekOk || !dayOk) continue;
                foreach (var w in plan.Windows.Where(w => w.Day == day.DayOfWeek).OrderBy(w => w.StartMinute))
                {
                    var start = day.AddMinutes(w.StartMinute);
                    if (d == 0 && w.EndMinute <= minute) continue;
                    next = Utc(start < local ? local : start);
                    break;
                }
            }
            if (graceActive) reason = "Grace time.";
        }

        DateTimeOffset? windowEnd = window != null ? Utc(today.AddMinutes(window.EndMinute)) : null;
        if (graceActive && (windowEnd == null || grace!.UntilUtc > windowEnd)) windowEnd = grace!.UntilUtc;
        var left = Math.Min(remDaily, remWeek);
        int? minutesLeft = left == int.MaxValue ? null : Math.Max(0, left) / 60;
        return new PlayStatus(allowed, ends, next, usedTodaySec / 60, dailyLimit, usedWeekSec / 60, plan.WeeklyLimitMinutes,
            graceAvailable, graceActive, allowed ? reason : reason + (next is { } n ? $" Next play time: {LocalText(n)}." : ""),
            windowEnd, minutesLeft);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>Program entries that must be blocked right now: games outside their play time (in every profile, Maintenance too).</summary>
    public List<ProgramEntry> BlockedGamePrograms(NetGuardConfig cfg, NetGuardState state, DateTimeOffset now)
    {
        var list = new List<ProgramEntry>();
        foreach (var p in cfg.Profiles.Where(p => p.Plan != null))
        {
            var active = string.Equals(p.Name, state.ActiveProfile, StringComparison.OrdinalIgnoreCase);
            if (active && GetPlayStatus(cfg, state, p, now).Allowed) continue;
            list.AddRange(p.Programs.Where(e => p.Plan!.GamePrograms.Contains(e.Name, StringComparer.OrdinalIgnoreCase)));
        }
        return list;
    }

    /// <summary>Is <paramref name="newPlan"/> at least as strict as <paramref name="oldPlan"/>? (null = no plan = unlimited)</summary>
    public static bool IsAtLeastAsStrict(NetGuardConfig cfg, PlayPlan? newPlan, PlayPlan? oldPlan)
    {
        if (oldPlan == null) return true;
        if (newPlan == null) return false;
        var oldMin = Minutes(oldPlan);
        var newMin = Minutes(newPlan);
        for (var i = 0; i < newMin.Length; i++) if (newMin[i] && !oldMin[i]) return false;
        if (oldPlan.DailyLimitMinutes is { } od && !(newPlan.DailyLimitMinutes is { } nd && nd <= od)) return false;
        if (oldPlan.WeeklyLimitMinutes is { } ow && !(newPlan.WeeklyLimitMinutes is { } nw && nw <= ow)) return false;
        if (oldPlan.GamePrograms.Any(g => !newPlan.GamePrograms.Contains(g, StringComparer.OrdinalIgnoreCase))) return false;
        var nf = cfg.FindProfile(newPlan.FallbackProfile);
        var of = cfg.FindProfile(oldPlan.FallbackProfile);
        return nf != null && ProfilePolicy.IsAtLeastAsStrict(nf, of);
    }

    private static bool[] Minutes(PlayPlan plan)
    {
        var m = new bool[7 * 1440];
        foreach (var w in plan.Windows)
            for (var i = w.StartMinute; i < w.EndMinute; i++) m[(int)w.Day * 1440 + i] = true;
        return m;
    }

    // ================================================================ switching

    public SwitchDecision CanSwitch(NetGuardConfig cfg, NetGuardState state, string target, DateTimeOffset now)
    {
        var t = cfg.FindProfile(target);
        if (t == null) return new(false, $"There is no profile called '{target}'.");
        var cur = state.ActiveProfile == null ? null : cfg.FindProfile(state.ActiveProfile);

        if (t.Plan != null)
        {
            var play = GetPlayStatus(cfg, state, t, now);
            if (!play.Allowed) return new(false, play.Reason);
        }

        if (state.Commitment is { } c && c.UntilUtc > now && cfg.FindProfile(c.Profile) is { } cp && !ProfilePolicy.IsAtLeastAsStrict(t, cp))
            return new(false, $"You committed to {cp.Name} until {LocalText(c.UntilUtc)}.");

        if (ProfilePolicy.IsAtLeastAsStrict(t, cur)) return new(true, cur == null ? "from normal internet" : "stricter or equal");
        if (t.Plan != null) return new(true, "play time");
        return new(false, "Looser than the current profile – use \"Request unlock\".");
    }

    // ================================================================ unlock requests

    public int UnlockRequestsToday(NetGuardState state, DateTimeOffset now) =>
        state.UnlockRequests.Count(r => Local(r.RequestedUtc).Date == Local(now).Date);

    public SwitchDecision CanRequestUnlock(NetGuardConfig cfg, NetGuardState state, string target, DateTimeOffset now)
    {
        var cur = state.ActiveProfile == null ? null : cfg.FindProfile(state.ActiveProfile);
        if (cur == null) return new(false, "No lock is active.");
        if (state.TestMode != null) return new(false, "A test lock is running.");
        var t = cfg.FindProfile(target);
        if (t == null) return new(false, $"There is no profile called '{target}'.");
        if (t.Plan != null)
        {
            var play = GetPlayStatus(cfg, state, t, now);
            return new(false, play.Allowed ? "It's play time – just switch." : $"Your play plan decides when {t.Name} is available. {play.Reason}");
        }
        if (ProfilePolicy.IsAtLeastAsStrict(t, cur)) return new(false, "That isn't looser – just switch.");
        if (state.Commitment is { } c && c.UntilUtc > now)
            return new(false, $"You committed to {c.Profile} until {LocalText(c.UntilUtc)}. Unlock requests are possible after that.");
        if (state.PendingUnlock != null) return new(false, "An unlock request is already running.");
        var used = UnlockRequestsToday(state, now);
        if (used >= T(cfg).MaxUnlockRequestsPerDay)
            return new(false, $"Today's {T(cfg).MaxUnlockRequestsPerDay} unlock requests are used up (cancelled and expired ones count too). Tomorrow you have new ones.");
        return new(true, $"{T(cfg).MaxUnlockRequestsPerDay - used} request(s) left today");
    }

    public UnlockRequest RequestUnlock(NetGuardConfig cfg, NetGuardState state, string target, string reason, DateTimeOffset now)
    {
        var d = CanRequestUnlock(cfg, state, target, now);
        if (!d.Allowed) throw new InvalidOperationException(d.Reason);
        reason = (reason ?? "").Trim();
        if (reason.Length < T(cfg).MinReasonLength)
            throw new InvalidOperationException($"Please write a reason of at least {T(cfg).MinReasonLength} characters.");
        var req = new UnlockRequest
        {
            FromProfile = state.ActiveProfile!,
            TargetProfile = cfg.FindProfile(target)!.Name,
            Reason = reason,
            RequestedUtc = now,
            ReadyUtc = now.AddMinutes(T(cfg).CooldownMinutes),
            ExpiresUtc = now.AddMinutes(T(cfg).CooldownMinutes + T(cfg).ConfirmWindowMinutes),
            Passage = Passage.Generate(T(cfg).ConfirmPassageLength),
            Status = UnlockStatus.Pending,
        };
        state.UnlockRequests.Add(req);
        if (state.UnlockRequests.Count > 300) state.UnlockRequests.RemoveRange(0, state.UnlockRequests.Count - 300);
        return req;
    }

    /// <summary>Returns the profile to switch to.</summary>
    public string ConfirmUnlock(NetGuardState state, string typed, DateTimeOffset now)
    {
        var req = state.PendingUnlock ?? throw new InvalidOperationException("There is no unlock request.");
        if (now < req.ReadyUtc) throw new InvalidOperationException($"Not yet – the waiting time ends at {LocalText(req.ReadyUtc)}.");
        if (now > req.ExpiresUtc) throw new InvalidOperationException("The confirm window is over; the request has expired.");
        if (!Passage.Matches(req.Passage, typed)) throw new InvalidOperationException("The text doesn't match exactly. Take your time and check it word by word.");
        req.Status = UnlockStatus.Confirmed;
        req.ClosedUtc = now;
        return req.TargetProfile;
    }

    public void CancelUnlock(NetGuardState state, DateTimeOffset now)
    {
        var req = state.PendingUnlock ?? throw new InvalidOperationException("There is no unlock request.");
        req.Status = UnlockStatus.Cancelled;
        req.ClosedUtc = now;
    }

    // ================================================================ commitments

    /// <summary>Validates and stores a commitment. Returns the profile to switch to now (or null).</summary>
    public string? Commit(NetGuardConfig cfg, NetGuardState state, string profile, DateTimeOffset untilUtc, DateTimeOffset now)
    {
        var p = cfg.FindProfile(profile) ?? throw new InvalidOperationException($"There is no profile called '{profile}'.");
        if (p.Kind == ProfileKind.AllowAll) throw new InvalidOperationException("You can't commit to Maintenance.");
        if (untilUtc < now.AddMinutes(Practice ? 1 : 5)) throw new InvalidOperationException($"Please choose a time at least {(Practice ? 1 : 5)} minutes from now.");
        if (untilUtc > now.AddDays(T(cfg).MaxCommitmentDays)) throw new InvalidOperationException($"At most {T(cfg).MaxCommitmentDays} days.");

        var cur = state.ActiveProfile == null ? null : cfg.FindProfile(state.ActiveProfile);
        if (cur != null && !ProfilePolicy.IsAtLeastAsStrict(p, cur))
            throw new InvalidOperationException($"You can commit to {cur.Name} or something stricter.");
        if (state.Commitment is { } old && old.UntilUtc > now)
        {
            if (untilUtc < old.UntilUtc) throw new InvalidOperationException($"A commitment can be extended, never shortened (current: until {LocalText(old.UntilUtc)}).");
            if (cfg.FindProfile(old.Profile) is { } op && !ProfilePolicy.IsAtLeastAsStrict(p, op))
                throw new InvalidOperationException($"You're committed to {op.Name}; a new commitment can't be looser.");
        }
        if (p.Plan != null && !GetPlayStatus(cfg, state, p, now).Allowed)
            throw new InvalidOperationException($"{p.Name} isn't available right now (play plan).");

        state.Commitment = new Commitment { Profile = p.Name, UntilUtc = untilUtc, CreatedUtc = now };
        if (state.PendingUnlock is { } pending) { pending.Status = UnlockStatus.Cancelled; pending.ClosedUtc = now; }
        return string.Equals(cur?.Name, p.Name, StringComparison.OrdinalIgnoreCase) ? null : p.Name;
    }

    // ================================================================ grace

    public DateTimeOffset TakeGrace(NetGuardConfig cfg, NetGuardState state, string profile, DateTimeOffset now)
    {
        var p = cfg.FindProfile(profile) ?? throw new InvalidOperationException($"There is no profile called '{profile}'.");
        if (p.Plan == null) throw new InvalidOperationException($"{p.Name} has no play plan.");
        if (!string.Equals(state.ActiveProfile, p.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{p.Name} isn't active.");
        var play = GetPlayStatus(cfg, state, p, now);
        if (!play.GraceAvailable)
            throw new InvalidOperationException(play.GraceActive || state.Graces.Any(g => g.Profile == p.Name && g.Date == DateKey(Local(now).Date))
                ? "Today's extra minutes are already used."
                : "Extra minutes are available during the last 15 minutes of play time.");
        var until = play.EndsUtc!.Value.AddMinutes(T(cfg).GraceMinutes);
        state.Graces.Add(new GraceRecord { Profile = p.Name, Date = DateKey(Local(now).Date), UntilUtc = until });
        if (state.Graces.Count > 100) state.Graces.RemoveRange(0, state.Graces.Count - 100);
        return until;
    }

    // ================================================================ plan changes

    /// <summary>
    /// Stricter: applied now (returns null). Looser: queued for the waiting time (returns the pending change) –
    /// except while no profile is active: then nothing holds you anyway, so every change applies now
    /// (that's when you set things up calmly).
    /// </summary>
    public PendingPlanChange? ChangePlan(NetGuardConfig cfg, NetGuardState state, string profile, PlayPlan? newPlan, DateTimeOffset now)
    {
        var p = cfg.FindProfile(profile) ?? throw new InvalidOperationException($"There is no profile called '{profile}'.");
        if (newPlan != null)
        {
            var copy = new ProfileDefinition { Name = p.Name, Kind = p.Kind, Programs = p.Programs, Plan = newPlan };
            cfg.ValidatePlan(copy);
        }
        state.PendingPlanChanges.RemoveAll(c => string.Equals(c.Profile, p.Name, StringComparison.OrdinalIgnoreCase));
        if (state.ActiveProfile == null || IsAtLeastAsStrict(cfg, newPlan, p.Plan))
        {
            p.Plan = newPlan;
            return null;
        }
        var change = new PendingPlanChange
        {
            Profile = p.Name, Plan = newPlan, RequestedUtc = now, EffectiveUtc = now + PlanDelay(cfg),
        };
        state.PendingPlanChanges.Add(change);
        return change;
    }

    // ================================================================ periodic work

    /// <summary>One pass: count play time, expire requests, apply due plan changes, end Maintenance / commitments / play time.</summary>
    public TickResult Tick(NetGuardConfig cfg, NetGuardState state, DateTimeOffset now, TimeSpan elapsed)
    {
        var r = new TickResult();
        var active = state.ActiveProfile == null ? null : cfg.FindProfile(state.ActiveProfile);

        // time in the active profile (any profile – it feeds both play-time limits and the statistics)
        if (active != null && elapsed > TimeSpan.Zero)
        {
            var secs = (int)Math.Min(elapsed.TotalSeconds, 300) * Speed;
            AddUsage(state.PlayUsage, active.Name, Local(now).Date, secs);
            r.StateChanged = true;
        }

        // unlock request expired
        if (state.PendingUnlock is { } req && now > req.ExpiresUtc)
        {
            req.Status = UnlockStatus.Expired;
            req.ClosedUtc = now;
            r.Events.Add(new("unlock-expired", new { profile = req.TargetProfile, reason = req.Reason }));
            r.StateChanged = true;
        }

        // looser plan changes whose waiting time is over
        foreach (var due in state.PendingPlanChanges.Where(c => c.EffectiveUtc <= now).ToList())
        {
            state.PendingPlanChanges.Remove(due);
            if (cfg.FindProfile(due.Profile) is { } p)
            {
                p.Plan = due.Plan;
                r.ConfigChanged = true;
                r.Events.Add(new("plan-changed", new { profile = p.Name, change = "looser plan took effect after the waiting time" }));
            }
            r.StateChanged = true;
        }

        // commitment over
        if (state.Commitment is { } c && c.UntilUtc <= now)
        {
            r.Events.Add(new("commitment-ended", new { profile = c.Profile }));
            state.Commitment = null;
            r.StateChanged = true;
        }

        if (active?.Kind == ProfileKind.AllowAll)
        {
            state.LastMaintenanceUtc = now;
            r.StateChanged = true;
            if (state.MaintenanceUntilUtc is { } mu && mu <= now)
            {
                r.SwitchTo = ResolveReturn(cfg, state, state.MaintenanceReturnTo, now);
                r.SwitchReason = "Maintenance time is over";
                return r;
            }
        }

        // play time over (test locks are left alone – they end by themselves)
        if (active?.Plan != null && state.TestMode == null && !GetPlayStatus(cfg, state, active, now).Allowed)
        {
            r.SwitchTo = active.Plan.FallbackProfile;
            r.SwitchReason = "play time is over";
        }
        return r;
    }

    public const int KeepUsageDays = 400;

    public static void AddUsage(Dictionary<string, Dictionary<string, int>> usage, string profile, DateTime localDay, int seconds)
    {
        if (!usage.TryGetValue(profile, out var days)) usage[profile] = days = new();
        var key = DateKey(localDay);
        days[key] = (days.TryGetValue(key, out var s) ? s : 0) + seconds;
        var cutoff = DateKey(localDay.AddDays(-KeepUsageDays));
        foreach (var old in days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) days.Remove(old);
    }

    /// <summary>Where to go back to: the given profile, or – if it's a game outside its play time – its fallback.</summary>
    public string ResolveReturn(NetGuardConfig cfg, NetGuardState state, string? returnTo, DateTimeOffset now)
    {
        var p = returnTo == null ? null : cfg.FindProfile(returnTo);
        if (p == null || p.Kind == ProfileKind.AllowAll)
            return cfg.Profiles.FirstOrDefault(x => x.Kind == ProfileKind.Allowlist && x.Plan == null && x.Programs.Count == 0)?.Name
                   ?? cfg.Profiles.First(x => x.Kind == ProfileKind.Allowlist && x.Plan == null).Name;
        if (p.Plan != null && !GetPlayStatus(cfg, state, p, now).Allowed) return p.Plan.FallbackProfile;
        return p.Name;
    }

    /// <summary>Bookkeeping after any switch.</summary>
    public void OnSwitched(NetGuardConfig cfg, NetGuardState state, string? from, string to, DateTimeOffset now, bool viaUnlock)
    {
        var t = cfg.FindProfile(to);
        if (t?.Kind == ProfileKind.AllowAll)
        {
            state.LastMaintenanceUtc = now;
            if (viaUnlock)
            {
                state.MaintenanceReturnTo = from;
                state.MaintenanceUntilUtc = now.AddMinutes(T(cfg).MaintenanceMaxMinutes);
            }
        }
        else
        {
            state.MaintenanceReturnTo = null;
            state.MaintenanceUntilUtc = null;
        }
    }

    /// <summary>When does something next happen that the service should wake up for?</summary>
    public DateTimeOffset? NextEvent(NetGuardConfig cfg, NetGuardState state, DateTimeOffset now)
    {
        var times = new List<DateTimeOffset?>
        {
            state.PendingUnlock?.ReadyUtc, state.PendingUnlock?.ExpiresUtc, state.Commitment?.UntilUtc, state.MaintenanceUntilUtc,
        };
        times.AddRange(state.PendingPlanChanges.Select(c => (DateTimeOffset?)c.EffectiveUtc));
        foreach (var p in cfg.Profiles.Where(p => p.Plan != null))
        {
            var ps = GetPlayStatus(cfg, state, p, now);
            times.Add(ps.EndsUtc);
            times.Add(ps.NextStartUtc);
        }
        return times.Where(t => t > now).Min();
    }
}
