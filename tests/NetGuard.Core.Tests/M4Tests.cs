using NetGuard.Core.Config;
using NetGuard.Core.Firewall;
using NetGuard.Core.Rules;
using NetGuard.Core.State;

namespace NetGuard.Core.Tests;

public class M4Tests
{
    // Monday 2026-11-09 is a Monday. Rules run in UTC for the tests.
    private static readonly LockRules Rules = new(TimeZoneInfo.Utc);
    private static DateTimeOffset At(int day, int h, int m = 0) => new(2026, 11, 9 + day, h, m, 0, TimeSpan.Zero);

    private static NetGuardConfig Cfg(Action<PlayPlan>? plan = null)
    {
        var cfg = DefaultConfig.Create(new DetectedPaths());
        if (plan != null)
        {
            var p = new PlayPlan { GamePrograms = { "World of Warcraft" } };
            plan(p);
            cfg.FindProfile("WoW")!.Plan = p;
            cfg.Validate();
        }
        return cfg;
    }

    private static PlayPlan Evenings(PlayPlan p)
    {
        foreach (var d in Enum.GetValues<DayOfWeek>()) p.Windows.Add(new PlayWindow { Day = d, Start = "19:00", End = "23:00" });
        return p;
    }

    // ---------------------------------------------------------------- trusted clock

    [Fact]
    public void Trusted_clock_ignores_jumps_and_never_goes_back()
    {
        var wall = At(0, 12);
        long mono = 0;
        TimeSpan? jump = null;
        var clock = new TrustedClock(() => wall, () => mono, null);
        clock.JumpDetected += j => jump = j;

        mono += 60_000; wall = wall.AddMinutes(1);
        Assert.Equal(At(0, 12, 1), clock.Now);

        mono += 60_000; wall = wall.AddHours(5);   // someone moves the clock forward
        Assert.Equal(At(0, 12, 2), clock.Now);
        Assert.NotNull(jump);

        mono += 60_000; wall = At(0, 11);          // and back
        Assert.Equal(At(0, 12, 3), clock.Now);

        var restarted = new TrustedClock(() => At(0, 10), () => 0, At(0, 12, 3)); // reboot with clock set back
        Assert.Equal(At(0, 12, 3), restarted.Now);
    }

    // ---------------------------------------------------------------- unlock

    [Fact]
    public void Unlock_takes_cooldown_then_exact_retyping_within_the_window()
    {
        var cfg = Cfg();
        var st = new NetGuardState { ActiveProfile = "WoW" };
        Assert.Throws<InvalidOperationException>(() => Rules.RequestUnlock(cfg, st, "Maintenance", "too short", At(0, 10)));
        var req = Rules.RequestUnlock(cfg, st, "Maintenance", "Windows needs its monthly updates", At(0, 10));
        Assert.True(req.Passage.Length >= 400);
        Assert.Equal(At(0, 10, 45), req.ReadyUtc);

        Assert.Throws<InvalidOperationException>(() => Rules.ConfirmUnlock(st, req.Passage, At(0, 10, 44)));     // too early
        Assert.Throws<InvalidOperationException>(() => Rules.ConfirmUnlock(st, req.Passage + " x", At(0, 10, 46))); // wrong text
        Assert.Equal("Maintenance", Rules.ConfirmUnlock(st, "  " + req.Passage.Replace(" ", "  ") + "\n", At(0, 10, 47)));
        Assert.Equal(UnlockStatus.Confirmed, req.Status);
    }

    [Fact]
    public void Unlock_expires_after_the_confirm_window()
    {
        var cfg = Cfg();
        var st = new NetGuardState { ActiveProfile = "Offline" };
        var req = Rules.RequestUnlock(cfg, st, "Maintenance", "Windows needs its monthly updates", At(0, 10));
        var r = Rules.Tick(cfg, st, At(0, 10, 56), TimeSpan.FromSeconds(30));
        Assert.Equal(UnlockStatus.Expired, req.Status);
        Assert.Contains(r.Events, e => e.Type == "unlock-expired");
        Assert.Throws<InvalidOperationException>(() => Rules.ConfirmUnlock(st, req.Passage, At(0, 10, 56)));
    }

    [Fact]
    public void Two_unlock_requests_per_day_cancelled_and_expired_count()
    {
        var cfg = Cfg();
        var st = new NetGuardState { ActiveProfile = "Offline" };
        Rules.RequestUnlock(cfg, st, "Maintenance", "Windows needs its monthly updates", At(0, 9));
        Rules.CancelUnlock(st, At(0, 9, 5));
        Rules.RequestUnlock(cfg, st, "Maintenance", "Windows needs its monthly updates", At(0, 10));
        Rules.Tick(cfg, st, At(0, 11), TimeSpan.Zero); // expires
        Assert.False(Rules.CanRequestUnlock(cfg, st, "Maintenance", At(0, 20)).Allowed);
        Assert.True(Rules.CanRequestUnlock(cfg, st, "Maintenance", At(1, 0, 1)).Allowed); // next day
    }

    [Fact]
    public void Unlock_rules_refuse_the_obvious()
    {
        var cfg = Cfg();
        Assert.False(Rules.CanRequestUnlock(cfg, new NetGuardState(), "Maintenance", At(0, 9)).Allowed);                    // nothing locked
        Assert.False(Rules.CanRequestUnlock(cfg, new NetGuardState { ActiveProfile = "WoW" }, "Offline", At(0, 9)).Allowed); // not looser
        var st = new NetGuardState { ActiveProfile = "Offline" };
        Rules.RequestUnlock(cfg, st, "Maintenance", "Windows needs its monthly updates", At(0, 9));
        Assert.False(Rules.CanRequestUnlock(cfg, st, "WoW", At(0, 9, 1)).Allowed); // one at a time
    }

    [Fact]
    public void Maintenance_via_unlock_returns_by_itself()
    {
        var cfg = Cfg();
        var st = new NetGuardState { ActiveProfile = "Maintenance" };
        Rules.OnSwitched(cfg, st, "WoW", "Maintenance", At(0, 10), viaUnlock: true);
        Assert.Null(Rules.Tick(cfg, st, At(0, 10, 44), TimeSpan.Zero).SwitchTo);
        var r = Rules.Tick(cfg, st, At(0, 10, 45), TimeSpan.Zero);
        Assert.Equal("WoW", r.SwitchTo);
    }

    // ---------------------------------------------------------------- commitments

    [Fact]
    public void Commitments_block_loosening_and_can_only_be_extended()
    {
        var cfg = Cfg();
        var st = new NetGuardState { ActiveProfile = "WoW" };
        Assert.Null(Rules.Commit(cfg, st, "WoW", At(0, 23), At(0, 18)));
        Assert.False(Rules.CanSwitch(cfg, st, "Maintenance", At(0, 19)).Allowed);
        Assert.False(Rules.CanRequestUnlock(cfg, st, "Maintenance", At(0, 19)).Allowed);
        Assert.True(Rules.CanSwitch(cfg, st, "Offline", At(0, 19)).Allowed);

        Assert.Throws<InvalidOperationException>(() => Rules.Commit(cfg, st, "WoW", At(0, 22), At(0, 19)));     // shorter
        Assert.Throws<InvalidOperationException>(() => Rules.Commit(cfg, st, "Maintenance", At(1, 0), At(0, 19)));
        Assert.Equal("Offline", Rules.Commit(cfg, st, "Offline", At(1, 18), At(0, 19)));                       // stricter + longer → switch

        st.ActiveProfile = "Offline";
        Assert.False(Rules.CanSwitch(cfg, st, "WoW", At(1, 12)).Allowed);
        Rules.Tick(cfg, st, At(1, 18), TimeSpan.Zero);
        Assert.Null(st.Commitment);
    }

    [Fact]
    public void Commitment_cancels_a_pending_unlock()
    {
        var cfg = Cfg();
        var st = new NetGuardState { ActiveProfile = "Offline" };
        var req = Rules.RequestUnlock(cfg, st, "Maintenance", "Windows needs its monthly updates", At(0, 9));
        Rules.Commit(cfg, st, "Offline", At(0, 20), At(0, 9, 10));
        Assert.Equal(UnlockStatus.Cancelled, req.Status);
    }

    // ---------------------------------------------------------------- play plans

    [Fact]
    public void Play_only_inside_windows()
    {
        var cfg = Cfg(p => Evenings(p));
        var st = new NetGuardState { ActiveProfile = "Offline" };
        Assert.False(Rules.CanSwitch(cfg, st, "WoW", At(0, 18)).Allowed);
        var s = Rules.GetPlayStatus(cfg, st, cfg.FindProfile("WoW")!, At(0, 18));
        Assert.Equal(At(0, 19), s.NextStartUtc);
        Assert.True(Rules.CanSwitch(cfg, st, "WoW", At(0, 19, 30)).Allowed); // looser, but it's play time
        Assert.False(Rules.CanRequestUnlock(cfg, st, "WoW", At(0, 18)).Allowed); // the plan decides, no unlock route
    }

    [Fact]
    public void Daily_limit_counts_active_time_and_switches_to_offline()
    {
        var cfg = Cfg(p => { Evenings(p); p.DailyLimitMinutes = 60; });
        var st = new NetGuardState { ActiveProfile = "WoW" };
        var t = At(0, 19);
        for (var i = 0; i < 59; i++) { t = t.AddMinutes(1); Assert.Null(Rules.Tick(cfg, st, t, TimeSpan.FromMinutes(1)).SwitchTo); }
        var st2 = Rules.GetPlayStatus(cfg, st, cfg.FindProfile("WoW")!, t);
        Assert.Equal(59, st2.UsedTodayMinutes);
        Assert.Equal(t.AddMinutes(1), st2.EndsUtc);
        Assert.True(st2.GraceAvailable);
        var r = Rules.Tick(cfg, st, t.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.Equal("Offline", r.SwitchTo);
        Assert.Equal(At(1, 19), Rules.GetPlayStatus(cfg, st, cfg.FindProfile("WoW")!, t.AddMinutes(1)).NextStartUtc);
    }

    [Fact]
    public void Weekly_limit()
    {
        var cfg = Cfg(p => { Evenings(p); p.WeeklyLimitMinutes = 120; });
        var st = new NetGuardState { ActiveProfile = "WoW" };
        Rules.Tick(cfg, st, At(0, 20), TimeSpan.FromMinutes(5)); // ensure dict exists
        st.PlayUsage["WoW"][LockRules.DateKey(At(0, 0).DateTime)] = 120 * 60; // the whole week used up on Monday
        Assert.False(Rules.GetPlayStatus(cfg, st, cfg.FindProfile("WoW")!, At(1, 20)).Allowed);
        Assert.True(Rules.GetPlayStatus(cfg, st, cfg.FindProfile("WoW")!, At(7, 20)).Allowed); // next Monday
    }

    [Fact]
    public void Grace_once_per_day_and_it_comes_off_tomorrow()
    {
        var cfg = Cfg(p => { Evenings(p); p.DailyLimitMinutes = 120; });
        var st = new NetGuardState { ActiveProfile = "WoW" };
        var wow = cfg.FindProfile("WoW")!;
        Assert.Throws<InvalidOperationException>(() => Rules.TakeGrace(cfg, st, "WoW", At(0, 20)));   // not near the end
        var until = Rules.TakeGrace(cfg, st, "WoW", At(0, 22, 50));
        Assert.Equal(At(0, 23, 10), until);
        Assert.True(Rules.GetPlayStatus(cfg, st, wow, At(0, 23, 5)).Allowed);
        Assert.Null(Rules.Tick(cfg, st, At(0, 23, 5), TimeSpan.Zero).SwitchTo);
        Assert.Equal("Offline", Rules.Tick(cfg, st, At(0, 23, 11), TimeSpan.Zero).SwitchTo);
        Assert.Throws<InvalidOperationException>(() => Rules.TakeGrace(cfg, st, "WoW", At(0, 22, 55)));
        Assert.Equal(110, Rules.GetPlayStatus(cfg, st, wow, At(1, 19)).DailyLimitMinutes); // the next day: 10 minutes less
    }

    [Fact]
    public void Games_are_blocked_everywhere_outside_play_time_even_in_maintenance()
    {
        var cfg = Cfg(p => Evenings(p));
        var inMaintenance = new NetGuardState { ActiveProfile = "Maintenance" };
        Assert.Contains(Rules.BlockedGamePrograms(cfg, inMaintenance, At(0, 20)), e => e.Name == "World of Warcraft");
        var playing = new NetGuardState { ActiveProfile = "WoW" };
        Assert.Empty(Rules.BlockedGamePrograms(cfg, playing, At(0, 20)));
        Assert.NotEmpty(Rules.BlockedGamePrograms(cfg, playing, At(0, 23, 30)));
    }

    [Fact]
    public void Game_block_rules_end_up_in_the_firewall()
    {
        using var env = new TestEnv();
        var cfg = env.CreateStandardPc();
        cfg.FindProfile("WoW")!.Plan = Evenings(new PlayPlan { GamePrograms = { "World of Warcraft" } });
        var engine = env.CreateEngine();
        engine.ExtraBlocked = c => Rules.BlockedGamePrograms(c, new NetGuardState { ActiveProfile = "Maintenance" }, At(0, 12));
        engine.ApplyProfile(cfg, "Maintenance", "test", null);
        Assert.Contains(env.Firewall.Rules, r => r.Name == "NetGuard:PlayTime:Wow.exe" && r.Action == FwAction.Block);
        Assert.DoesNotContain(env.Firewall.Rules, r => r.Name.Contains("Spotify") && r.Action == FwAction.Block);
    }

    // ---------------------------------------------------------------- plan changes

    [Fact]
    public void Stricter_plan_now_looser_plan_after_72_hours()
    {
        var cfg = Cfg(p => { Evenings(p); p.DailyLimitMinutes = 120; });
        var st = new NetGuardState { ActiveProfile = "Offline" };

        var stricter = Evenings(new PlayPlan { GamePrograms = { "World of Warcraft" }, DailyLimitMinutes = 60 });
        stricter.Windows.RemoveAll(w => w.Day == DayOfWeek.Monday);
        Assert.Null(Rules.ChangePlan(cfg, st, "WoW", stricter, At(0, 12)));
        Assert.Equal(60, cfg.FindProfile("WoW")!.Plan!.DailyLimitMinutes);

        var looser = Evenings(new PlayPlan { GamePrograms = { "World of Warcraft" }, DailyLimitMinutes = 180 });
        var pending = Rules.ChangePlan(cfg, st, "WoW", looser, At(0, 12));
        Assert.NotNull(pending);
        Assert.Equal(At(3, 12), pending!.EffectiveUtc);
        Assert.Equal(60, cfg.FindProfile("WoW")!.Plan!.DailyLimitMinutes);
        Assert.False(Rules.Tick(cfg, st, At(3, 11), TimeSpan.Zero).ConfigChanged);
        Assert.True(Rules.Tick(cfg, st, At(3, 12), TimeSpan.Zero).ConfigChanged);
        Assert.Equal(180, cfg.FindProfile("WoW")!.Plan!.DailyLimitMinutes);

        Assert.NotNull(Rules.ChangePlan(cfg, st, "WoW", null, At(4, 0)));   // removing the plan = looser
        Assert.True(LockRules.IsAtLeastAsStrict(cfg, looser, null));        // adding one = stricter
    }

    [Fact]
    public void Plan_validation()
    {
        var cfg = Cfg();
        var bad = new PlayPlan { Windows = { new PlayWindow { Day = DayOfWeek.Monday, Start = "22:00", End = "21:00" } } };
        Assert.ThrowsAny<Exception>(() => Rules.ChangePlan(cfg, new NetGuardState(), "WoW", bad, At(0, 0)));
        var badGame = new PlayPlan { GamePrograms = { "Chrome" } };
        Assert.ThrowsAny<Exception>(() => Rules.ChangePlan(cfg, new NetGuardState(), "WoW", badGame, At(0, 0)));
        Assert.Equal(1440, PlayWindow.ParseTime("24:00"));
    }
}
