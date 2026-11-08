using System.Runtime.Versioning;
using System.Text;
using NetGuard.Core;
using NetGuard.Core.Config;
using NetGuard.Core.Editing;
using NetGuard.Core.Paths;
using NetGuard.Core.Hosts;
using NetGuard.Core.Ipc;
using NetGuard.Core.Logging;
using NetGuard.Core.Policy;
using NetGuard.Core.Reporting;
using NetGuard.Core.Rules;
using NetGuard.Core.State;
using NetGuard.Core.Security;
using NetGuard.Core.Updates;
using NetGuard.Windows;

namespace NetGuard.Service;

public sealed record CallerInfo(string Name);

/// <summary>
/// The service's brain: self-healing ticks and the answers to requests from the window app.
/// Every decision about what is allowed is made here, never in the app.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Guard
{
    private const string ServiceActor = "NetGuard service";
    private readonly Engine _engine;
    private readonly ILogger<Guard> _logger;
    private readonly object _gate = new();
    private readonly AuthenticodeVerifier _verifier = new();

    public DateTimeOffset? LastCheckUtc { get; private set; }

    private readonly IHostApplicationLifetime _lifetime;

    private readonly LockRules _rules = new();
    private readonly TrustedClock _clock;
    private DateTimeOffset _lastTick;

    /// <summary>NetGuard's trusted time (ignores changes to the Windows clock).</summary>
    public DateTimeOffset Now => _clock.Now;

    public Guard(ILogger<Guard> logger, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _lifetime = lifetime;
        _engine = new Engine(new ComFirewall(), _verifier, new NetGuardPaths(), HostsFile.DefaultPath);
        NetGuardState initial;
        try { initial = _engine.LoadState(); } catch { initial = new NetGuardState(); }
        _clock = TrustedClock.System(initial.TrustedNowUtc);
        _clock.JumpDetected += jump =>
        {
            try { _engine.Log.Append(LogTypes.ClockJump, new { reason = $"The Windows clock was changed by {jump:g}; NetGuard keeps its own time." }, ServiceActor); } catch { }
        };
        _lastTick = _clock.Now;
        // Games outside their play time are blocked in every profile.
        _engine.ExtraBlocked = cfg =>
        {
            var st = _engine.LoadState();
            SyncPractice(st);
            return _rules.BlockedGamePrograms(cfg, st, _clock.Now);
        };
    }

    public Engine Engine => _engine;

    public NetGuardConfig? TryLoadConfig()
    {
        try { return File.Exists(_engine.Paths.ConfigFile) ? _engine.LoadConfig() : null; }
        catch (Exception e) { _logger.LogError(e, "Config can't be read"); return null; }
    }

    /// <summary>Seconds until the next check is due.</summary>
    public TimeSpan NextDelay()
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(TryLoadConfig()?.Timings.HealIntervalSeconds ?? 30, 5, 300));
        var state = _engine.LoadState();
        if (state.TestMode is { } tm && state.ActiveProfile != null)
        {
            var untilDeadline = tm.DeadlineUtc - DateTimeOffset.UtcNow;
            if (untilDeadline < interval) return untilDeadline < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : untilDeadline;
        }
        // Wake up exactly when play time ends, a cooldown is over, a commitment ends, practice ends…
        SyncPractice(state);
        if (state.Practice is { } pr && pr.EndsUtc - Now < interval)
            return pr.EndsUtc - Now < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : pr.EndsUtc - Now + TimeSpan.FromSeconds(1);
        if (TryLoadConfig() is { } cfg && _rules.NextEvent(cfg, state, Now) is { } next)
        {
            var until = next - Now + TimeSpan.FromSeconds(1);
            if (until < interval) return until < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : until;
        }
        return interval;
    }

    /// <summary>One self-healing pass.</summary>
    /// <summary>Short timings while practice mode runs.</summary>
    private void SyncPractice(NetGuardState? state = null)
    {
        state ??= _engine.LoadState();
        _rules.Practice = state.Practice is { } p && p.EndsUtc > Now;
    }

    /// <summary>Time-based rules: count play time, expire unlocks, apply due plan changes, switch when time is up.</summary>
    private void RunRules(NetGuardConfig cfg)
    {
        var now = Now;
        var pre = _engine.LoadState();
        if (pre.Practice is { } practice && practice.EndsUtc <= now)
        {
            EndPractice("practice time is over", ServiceActor);
            return;
        }
        SyncPractice(pre);
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (elapsed > TimeSpan.FromMinutes(5) || elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero; // after sleep/hibernate: don't count it

        var state = _engine.LoadState();
        var r = _rules.Tick(cfg, state, now, elapsed);
        state.TrustedNowUtc = now;
        _engine.SaveState(state);
        if (r.ConfigChanged) cfg.Save(_engine.Paths.ConfigFile);
        foreach (var e in r.Events) _engine.Log.Append(e.Type, e.Data, ServiceActor);
        if (r.SwitchTo != null) SwitchInternal(cfg, r.SwitchTo, ServiceActor, viaUnlock: false, autoReason: r.SwitchReason);
    }

    private void SwitchInternal(NetGuardConfig cfg, string target, string actor, bool viaUnlock, string? autoReason = null)
    {
        var before = _engine.LoadState();
        var from = before.ActiveProfile;
        // A permanent lock must not be undone by a leftover test-mode safety task – but in practice mode that
        // task is exactly what guarantees normal internet comes back, so it stays.
        if (before.Practice == null) DeadManSwitch.Disarm();
        _engine.ApplyProfile(cfg, target, actor, null);
        var state = _engine.LoadState();
        _rules.OnSwitched(cfg, state, from, cfg.FindProfile(target)?.Name ?? target, Now, viaUnlock);
        _engine.SaveState(state);
        if (autoReason != null) _engine.Log.Append(LogTypes.AutoSwitch, new { profile = target, from, reason = autoReason }, actor);
        Protect();
    }

    public void Tick()
    {
        lock (_gate)
        {
            try
            {
                var state = _engine.LoadState();

                // A test lock whose time is up ends here (the dead-man task is only the backup).
                if (state.ActiveProfile != null && state.TestMode is { } tm && tm.DeadlineUtc <= DateTimeOffset.UtcNow)
                {
                    TestMode.End(_engine, ServiceActor, endedEarly: false);
                    Protect();
                    return;
                }

                var cfg = TryLoadConfig();
                if (cfg == null) return;

                RunRules(cfg);
                state = _engine.LoadState();
                if (state.ActiveProfile == null) return;

                // Windows Firewall itself must run: if its services are stopped, no rule applies.
                var found = new List<string>();
                foreach (var svc in new[] { "BFE", "MpsSvc" })
                {
                    try { if (ServiceControl.EnsureRunning(svc) is { } what) found.Add(what); }
                    catch (Exception e) { _logger.LogWarning(e, "Could not check {Service}", svc); }
                }
                if (found.Count > 0)
                    _engine.Log.Append(LogTypes.Tamper, new { profile = state.ActiveProfile, found, changes = found.Select(f => "restarted: " + f).ToList() }, ServiceActor);

                var heal = _engine.Heal(cfg, ServiceActor);
                if (heal is { Plan.IsEmpty: false })
                    _logger.LogWarning("Healed {Count} difference(s); tamper={Tamper}", heal.Plan.Actions.Count, heal.WasTampered);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Self-healing failed");
                try { _engine.Log.Append(LogTypes.ApplyError, new { request = "self-heal", errors = new[] { e.Message } }, ServiceActor); } catch { }
            }
            finally
            {
                LastCheckUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    // ================================================================ self-protection

    public static string ProgramDir => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;

    private bool _uninstallPrepared;
    private bool _protectionOn;

    /// <summary>
    /// Self-protection follows the lock: while a lock is active, NetGuard's service, registry key and
    /// folders are protected from administrators. With normal internet there's nothing to protect,
    /// so NetGuard can be updated, repaired or uninstalled like any other program.
    /// </summary>
    public void Protect()
    {
        lock (_gate)
        {
            if (_uninstallPrepared) return;
            try
            {
                if (!Directory.Exists(_engine.Paths.DataDir)) DataDirSecurity.Secure(_engine.Paths);
                var locked = _engine.LoadState().ActiveProfile != null;
                if (locked)
                {
                    var changed = SelfProtection.Apply(_engine.Paths, ProgramDir);
                    if (changed.Count > 0) _logger.LogInformation("Self-protection applied: {Changed}", string.Join(", ", changed));
                    var failed = changed.Where(c => c.Contains("FAILED")).ToList();
                    if (failed.Count > 0) ReportServiceError("self-protection", string.Join("; ", failed));
                }
                else
                {
                    SelfProtection.Relax(_engine.Paths, ProgramDir);
                }
                _protectionOn = locked;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Self-protection failed");
                ReportServiceError("self-protection", e.Message);
            }
        }
    }

    private readonly HashSet<string> _reportedErrors = new();

    /// <summary>Write a service-side problem to NetGuard's history (once per distinct message).</summary>
    public void ReportServiceError(string where, string message)
    {
        lock (_reportedErrors)
        {
            if (!_reportedErrors.Add(where + message) || _reportedErrors.Count > 50) return;
        }
        try { _engine.Log.Append(LogTypes.ApplyError, new { request = where, errors = new[] { message } }, ServiceActor); } catch { }
    }

    // ================================================================ requests from the app

    public IpcResponse Handle(IpcRequest req, CallerInfo caller)
    {
        lock (_gate)
        {
            try
            {
                SyncPractice();
                return req.Command switch
                {
                    IpcCommands.Status => new IpcResponse { Ok = true, Status = BuildStatus(caller) },
                    IpcCommands.StatusText => IpcResponse.Success(
                        $"Service:          running (version {GitHubReleases.CurrentVersion}), last check {LastCheckUtc?.ToLocalTime():T}\n" +
                        $"Self-protection:  {(SelfProtection.IsServiceHardened() ? "on (a lock is active)" : "standby (only while a lock is active)")}\n" +
                        StatusReader.Read(_engine).Describe()),
                    IpcCommands.Switch => Switch(req, caller),
                    IpcCommands.TestStart => TestStart(req, caller),
                    IpcCommands.TestEnd => TestEnd(caller),
                    IpcCommands.Recover => Recover(req, caller),
                    IpcCommands.Preview => Preview(req),
                    IpcCommands.Programs => Programs(),
                    IpcCommands.History => History(),
                    IpcCommands.Detect => IpcResponse.Success(Report.Detected(AppDetector.Detect())),
                    IpcCommands.Init => Init(req, caller),
                    IpcCommands.RecoveryCreate => RecoveryCreate(req, caller),
                    IpcCommands.Audit => Audit(req),
                    IpcCommands.Blocked => Blocked(),
                    IpcCommands.Update => Update(caller),
                    IpcCommands.PrepareUninstall => PrepareUninstall(req, caller),
                    IpcCommands.ConfigGet => new IpcResponse { Ok = true, Config = BuildConfigDto() },
                    IpcCommands.InspectFile => InspectFile(req),
                    IpcCommands.ProgramAdd or IpcCommands.ProgramRemove or IpcCommands.ProfileAdd or IpcCommands.ProfileRemove
                        => EditConfig(req, caller),
                    IpcCommands.UnlockRequest => UnlockRequest(req, caller),
                    IpcCommands.UnlockConfirm => UnlockConfirm(req, caller),
                    IpcCommands.UnlockCancel => UnlockCancel(caller),
                    IpcCommands.Commit => Commit(req, caller),
                    IpcCommands.Grace => Grace(req, caller),
                    IpcCommands.PlanGet => PlanGet(),
                    IpcCommands.PlanSet => PlanSet(req, caller),
                    IpcCommands.PlanCancel => PlanCancel(req, caller),
                    IpcCommands.ProgramReapprove => Reapprove(req, caller),
                    IpcCommands.PracticeStart => PracticeStart(req, caller),
                    IpcCommands.PracticeEnd => EndPractice("ended by you", caller.Name),
                    IpcCommands.Summary => new IpcResponse
                    {
                        Ok = true,
                        Summary = WeeklySummary.Build(TryLoadConfig(), _engine.LoadState(), _engine.Log.ReadAll(), Now, Math.Clamp(req.WeekOffset ?? 0, -52, 0)),
                    },
                    _ => IpcResponse.Fail($"Unknown request '{req.Command}'."),
                };
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Request {Command} failed", req.Command);
                return IpcResponse.Fail(e.Message);
            }
        }
    }

    private IpcResponse Refuse(CallerInfo caller, IpcRequest req, string reason)
    {
        _engine.Log.Append(LogTypes.Refused, new { request = req.Command, profile = req.Profile, reason }, caller.Name);
        return IpcResponse.Fail(reason);
    }

    /// <summary>Null if the request carries the right recovery code; otherwise the refusal to send back.</summary>
    private IpcResponse? CheckCode(IpcRequest req, CallerInfo caller)
    {
        var state = _engine.LoadState();
        var result = RecoveryCode.Check(state, req.Code, DateTimeOffset.UtcNow);
        _engine.SaveState(state);
        switch (result)
        {
            case RecoveryCode.CheckResult.Ok:
                return null;
            case RecoveryCode.CheckResult.NoCode:
                return Refuse(caller, req, "No recovery code has been created yet.");
            case RecoveryCode.CheckResult.TooManyAttempts:
                return Refuse(caller, req, $"Too many wrong recovery codes. Try again in an hour.");
            default:
                _engine.Log.Append(LogTypes.RecoveryCodeWrong, new { request = req.Command }, caller.Name);
                var left = RecoveryCode.MaxWrongAttemptsPerHour - state.WrongCodeAttemptsUtc.Count;
                return IpcResponse.Fail($"That recovery code is not right. {left} attempt(s) left this hour.");
        }
    }

    private NetGuardConfig RequireConfig() =>
        TryLoadConfig() ?? throw new InvalidOperationException("NetGuard isn't set up yet: Setup → Create configuration.");

    private StatusDto BuildStatus(CallerInfo caller)
    {
        var state = _engine.LoadState();
        var cfg = TryLoadConfig();
        var dto = new StatusDto
        {
            ServiceVersion = GitHubReleases.CurrentVersion.ToString(),
            ConfigExists = cfg != null,
            ActiveProfile = state.ActiveProfile,
            ActiveSinceUtc = state.ActiveSinceUtc,
            TestDeadlineUtc = state.ActiveProfile != null ? state.TestMode?.DeadlineUtc : null,
            LastCheckUtc = LastCheckUtc,
            LastTamperUtc = state.LastTamperUtc,
            CallerName = caller.Name,
            RecoveryCodeExists = RecoveryCode.Exists(state),
            RecoveryCodeCreatedUtc = state.RecoveryCodeCreatedUtc,
            SelfProtectionActive = _protectionOn,
            ServerNowUtc = Now,
            CommitmentProfile = state.Commitment is { } c && c.UntilUtc > Now ? c.Profile : null,
            CommitmentUntilUtc = state.Commitment is { } c2 && c2.UntilUtc > Now ? c2.UntilUtc : null,
            MaintenanceUntilUtc = state.MaintenanceUntilUtc,
            PracticeUntilUtc = state.Practice?.EndsUtc,
        };
        if (cfg == null) return dto;

        var now = dto.ServerNowUtc;
        dto.GraceMinutes = _rules.T(cfg).GraceMinutes;
        dto.UnlocksLeftToday = Math.Max(0, cfg.Timings.MaxUnlockRequestsPerDay - _rules.UnlockRequestsToday(state, now));
        if (state.PendingUnlock is { } u)
            dto.Unlock = new UnlockDto
            {
                TargetProfile = u.TargetProfile, Reason = u.Reason, RequestedUtc = u.RequestedUtc, ReadyUtc = u.ReadyUtc, ExpiresUtc = u.ExpiresUtc,
                Passage = now >= u.ReadyUtc ? u.Passage : null,
            };
        if (state.ActiveProfile != null)
        {
            var since = state.LastMaintenanceUtc ?? state.ActiveSinceUtc ?? now;
            dto.MaintenanceReminder = now - since > TimeSpan.FromDays(cfg.Timings.MaintenanceReminderDays);
        }
        if (_engine.LastDesired is { } ld && string.Equals(ld.ProfileName, state.ActiveProfile, StringComparison.OrdinalIgnoreCase))
            dto.RejectedFiles = ld.RejectedPrograms.Select(r => $"{r.EntryName}: {r.Path} – {r.Detail}").ToList();

        foreach (var p in cfg.Profiles)
        {
            var sw = _rules.CanSwitch(cfg, state, p.Name, now);
            if (sw.Allowed && !RecoveryCode.Exists(state))
                sw = new SwitchDecision(false, "Create your recovery code first (Setup step 3). It's your way out in an emergency.");
            var test = ProfilePolicy.CanStartTest(state.ActiveProfile);
            dto.Profiles.Add(new ProfileDto
            {
                Name = p.Name,
                Kind = p.Kind.ToString(),
                Description = Report.ProfileDescription(p),
                Programs = p.Programs.Select(x => x.Name).ToList(),
                IsActive = string.Equals(p.Name, state.ActiveProfile, StringComparison.OrdinalIgnoreCase),
                SwitchAllowed = sw.Allowed,
                SwitchHint = sw.Reason,
                TestAllowed = test.Allowed,
                TestHint = test.Reason,
                UnlockAllowed = !sw.Allowed && _rules.CanRequestUnlock(cfg, state, p.Name, now).Allowed,
                UnlockHint = sw.Allowed ? "" : _rules.CanRequestUnlock(cfg, state, p.Name, now).Reason,
                Play = p.Plan == null ? null : ToDto(_rules.GetPlayStatus(cfg, state, p, now)),
            });
        }
        return dto;
    }

    private IpcResponse Switch(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var target = req.Profile ?? throw new ArgumentException("Which profile?");
        if (!RecoveryCode.Exists(state))
            return Refuse(caller, req, "Create your recovery code first (Setup). Without it there's no way out of a lock in an emergency.");
        var decision = _rules.CanSwitch(cfg, state, target, Now);
        if (!decision.Allowed) return Refuse(caller, req, decision.Reason);
        SwitchInternal(cfg, target, caller.Name, viaUnlock: false);
        return IpcResponse.Success($"{cfg.FindProfile(target)!.Name} is now active.");
    }

    private IpcResponse TestStart(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var decision = ProfilePolicy.CanStartTest(state.ActiveProfile);
        if (!decision.Allowed) return Refuse(caller, req, decision.Reason);
        var minutes = req.Minutes ?? cfg.Timings.TestModeMinutes;
        var (info, result) = TestMode.Start(_engine, cfg, req.Profile ?? throw new ArgumentException("Which profile?"), minutes, caller.Name);
        Protect();
        return IpcResponse.Success(
            $"Test lock until {info.DeadlineUtc.ToLocalTime():T}. A separate safety task also restores internet shortly after that.\n\n" +
            Report.Result(result));
    }

    private IpcResponse TestEnd(CallerInfo caller)
    {
        var state = _engine.LoadState();
        if (state.ActiveProfile == null || state.TestMode == null) return IpcResponse.Fail("No test lock is running.");
        var result = TestMode.End(_engine, caller.Name, endedEarly: true);
        Protect();
        return IpcResponse.Success("Normal internet is back.\n\n" + Report.Result(result));
    }

    /// <summary>Emergency restore: needs the recovery code while a lock is active.</summary>
    private IpcResponse Recover(IpcRequest req, CallerInfo caller)
    {
        if (_engine.LoadState().ActiveProfile != null && CheckCode(req, caller) is { } refused) return refused;
        var before = _engine.LoadState();
        before.Commitment = null;
        if (before.PendingUnlock is { } pu) { pu.Status = UnlockStatus.Cancelled; pu.ClosedUtc = Now; }
        _engine.SaveState(before);
        var (plan, result) = _engine.Recover("emergency restore with recovery code", caller.Name);
        try { DeadManSwitch.Disarm(); } catch { }
        Protect();
        return IpcResponse.Success(Report.Plan(plan) + "\n" + Report.Result(result) + (result.Success ? "\nNormal internet is back." : ""));
    }

    private IpcResponse Preview(IpcRequest req)
    {
        var cfg = RequireConfig();
        var d = _engine.BuildDesired(cfg, req.Profile ?? throw new ArgumentException("Which profile?"));
        var plan = _engine.PlanApply(cfg, d);
        return IpcResponse.Success(Report.Desired(d) + "\n" + Report.Plan(plan) + "\nThis was only a preview – nothing was changed.");
    }

    private IpcResponse Programs()
    {
        var cfg = RequireConfig();
        var sb = new StringBuilder();
        foreach (var p in cfg.Profiles) sb.AppendLine(Report.Desired(_engine.BuildDesired(cfg, p.Name)));
        return IpcResponse.Success(sb.ToString());
    }

    private IpcResponse History()
    {
        var rows = _engine.Log.ReadAll().Reverse().Take(500).Select(o => new LogEntryDto
        {
            Ts = DateTimeOffset.TryParse((string?)o["ts"], out var t) ? t : DateTimeOffset.MinValue,
            Type = (string?)o["type"] ?? "",
            What = Report.EventName((string?)o["type"]),
            Actor = (string?)o["actor"],
            Details = Report.LogSummary(o),
        }).ToList();
        return new IpcResponse { Ok = true, History = rows };
    }

    /// <summary>Re-creating the config could add programs, i.e. loosen: only with normal internet (or the code).</summary>
    private IpcResponse Init(IpcRequest req, CallerInfo caller)
    {
        if (_engine.LoadState().ActiveProfile != null && CheckCode(req, caller) is { } refused) return refused;
        var d = AppDetector.Detect();
        var cfg = DefaultConfig.Create(d);
        DataDirSecurity.Secure(_engine.Paths);
        cfg.Save(_engine.Paths.ConfigFile);
        _engine.Log.Append(LogTypes.ConfigCreated, new { detected = d }, caller.Name);
        Protect();
        return IpcResponse.Success(Report.Detected(d) + $"\nConfiguration saved.\n\nNext: step 3, create your recovery code.");
    }

    /// <summary>
    /// A new code may only be created with normal internet: otherwise "make a new code, then use it"
    /// would be a way out of a lock. Returned to the app exactly once.
    /// </summary>
    private IpcResponse RecoveryCreate(IpcRequest req, CallerInfo caller)
    {
        var state = _engine.LoadState();
        if (state.ActiveProfile != null)
            return Refuse(caller, req, "A new recovery code can only be created while you have normal internet.");
        var code = RecoveryCode.Generate();
        var replaced = RecoveryCode.Exists(state);
        RecoveryCode.Store(state, code);
        _engine.SaveState(state);
        _engine.Log.Append(LogTypes.RecoveryCodeCreated, new { reason = replaced ? "replaced the old code" : "first code" }, caller.Name);
        return IpcResponse.Success(code);
    }

    private static IpcResponse Audit(IpcRequest req)
    {
        var on = req.On ?? true;
        BlockedConnections.SetAuditing(on);
        return IpcResponse.Success(on
            ? "Windows now records blocked connections.\nReproduce the problem, then click \"Show blocked\". Click \"Stop recording\" when you're done."
            : "Recording stopped.");
    }

    private static IpcResponse Blocked()
    {
        var apps = BlockedConnections.Query(TimeSpan.FromMinutes(10));
        if (apps.Count == 0) return IpcResponse.Success("None found. Is recording on? Click \"Start recording\" and try again.");
        var sb = new StringBuilder();
        foreach (var a in apps)
            sb.AppendLine($"{a.Count,6}x  {a.Application}\n        last {a.LastSeen:T} → {string.Join(", ", a.Destinations)}");
        return IpcResponse.Success(sb.ToString());
    }

    /// <summary>
    /// Updates are installed by the service (as SYSTEM), because NetGuard's folders are read-only for
    /// administrators. Only with normal internet or in Maintenance – and the download needs internet anyway.
    /// </summary>
    private IpcResponse Update(CallerInfo caller)
    {
        var state = _engine.LoadState();
        var cfg = TryLoadConfig();
        var active = state.ActiveProfile == null ? null : cfg?.FindProfile(state.ActiveProfile);
        if (active != null && active.Kind != ProfileKind.AllowAll)
            return IpcResponse.Fail("Updates can be installed with normal internet or in Maintenance.");

        var release = Updater.CheckLatestAsync().GetAwaiter().GetResult();
        if (release.Version <= GitHubReleases.CurrentVersion)
            return IpcResponse.Success($"NetGuard is up to date ({GitHubReleases.CurrentVersion}).");
        var path = Updater.DownloadInstallerAsync(release).GetAwaiter().GetResult();
        _engine.Log.Append(LogTypes.UpdateStarted, new { from = GitHubReleases.CurrentVersion.ToString(), to = release.Version.ToString() }, caller.Name);

        // The installer stops this service, replaces the files and starts it again.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return IpcResponse.Success($"Installing version {release.Version}. NetGuard closes now; open it again in about a minute.");
    }

    /// <summary>
    /// Uninstalling: allowed with normal internet, or during a lock with the recovery code. Restores normal
    /// internet, gives administrators their rights back, and stops this service, so the normal Windows
    /// uninstaller can run.
    /// </summary>
    private IpcResponse PrepareUninstall(IpcRequest req, CallerInfo caller)
    {
        if (_engine.LoadState().ActiveProfile != null && CheckCode(req, caller) is { } refused) return refused;

        _engine.Recover("preparing to uninstall", caller.Name);
        try { DeadManSwitch.Disarm(); } catch { }
        _uninstallPrepared = true;
        SelfProtection.Relax(_engine.Paths, ProgramDir);
        ProcessRunner.Run("sc.exe", $"config {ServiceControl.ServiceName} start= demand");
        ProcessRunner.Run("sc.exe", $"failure {ServiceControl.ServiceName} reset= 0 actions= \"\"");
        _engine.Log.Append(LogTypes.ProtectionRelaxed, new { reason = "uninstall" }, caller.Name);

        // Stop ourselves shortly after answering.
        _ = Task.Run(async () => { await Task.Delay(1500); _lifetime.StopApplication(); });
        return IpcResponse.Success("Normal internet is back and NetGuard's protection is off. The uninstaller starts now.");
    }

    // ================================================================ program editor

    private ConfigDto BuildConfigDto()
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var resolver = new ProgramResolver(_verifier);
        var loosen = ProfilePolicy.CanLoosenConfig(cfg, state.ActiveProfile);

        ProgramDto ToDto(ProgramEntry e, bool blockList)
        {
            var files = resolver.Resolve(e, checkPins: !blockList);
            return new ProgramDto
            {
                Name = e.Name,
                Paths = e.Paths.ToList(),
                Pin = e.Pin.ToString(),
                Signer = e.Signer,
                PinDescription = blockList ? "blocked wherever it is"
                    : e.Pin switch
                    {
                        PinMode.Signature => $"only if signed by {e.Signer}",
                        PinMode.Sha256 => "only this exact version (re-add it after the program updates)",
                        _ => "any file at this location",
                    },
                Files = files.Select(f => new FileDto { Path = f.Path, Allowed = f.Status == ResolvedStatus.Allowed, Detail = f.Detail }).ToList(),
            };
        }

        return new ConfigDto
        {
            CanLoosen = loosen.Allowed,
            LoosenHint = loosen.Reason,
            Profiles = cfg.Profiles.Select(p => new ProfileConfigDto
            {
                Name = p.Name,
                Kind = p.Kind.ToString(),
                IsActive = string.Equals(p.Name, state.ActiveProfile, StringComparison.OrdinalIgnoreCase),
                Programs = p.Programs.Select(e => ToDto(e, false)).ToList(),
            }).ToList(),
            AlwaysBlocked = new ProfileConfigDto
            {
                Name = ConfigEditor.AlwaysBlockedList,
                Kind = "Block",
                Programs = cfg.AlwaysBlocked.Select(e => ToDto(e, true)).ToList(),
            },
        };
    }

    private ProgramEntry SuggestEntry(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"File not found: {path}");
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Please pick a program (.exe).");
        string? description = null;
        try { description = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
        return ConfigEditor.Suggest(Path.GetFullPath(path), description, _verifier.Check(path), ProgramResolver.Sha256Of);
    }

    private IpcResponse InspectFile(IpcRequest req)
    {
        var e = SuggestEntry(req.Path ?? throw new ArgumentException("Which file?"));
        return new IpcResponse
        {
            Ok = true,
            Program = new ProgramDto
            {
                Name = e.Name, Paths = e.Paths, Pin = e.Pin.ToString(), Signer = e.Signer,
                PinDescription = e.Pin == PinMode.Signature
                    ? $"Signed by {e.Signer}. It stays allowed after updates, and a renamed other program can't pretend to be it."
                    : "Not digitally signed. NetGuard pins this exact version: after an update you'll need to add it again (with normal internet or in Maintenance).",
            },
        };
    }

    private IpcResponse EditConfig(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var list = req.Profile ?? throw new ArgumentException("Which profile?");

        if (ConfigEditor.IsLoosening(req.Command, list))
        {
            var d = ProfilePolicy.CanLoosenConfig(cfg, state.ActiveProfile);
            if (!d.Allowed) return Refuse(caller, req, d.Reason);
        }

        string change;
        switch (req.Command)
        {
            case IpcCommands.ProgramAdd:
                var entry = SuggestEntry(req.Path ?? throw new ArgumentException("Which file?"));
                ConfigEditor.AddProgram(cfg, list, entry);
                change = $"added {entry.Name} to {list}";
                break;
            case IpcCommands.ProgramRemove:
                ConfigEditor.RemoveProgram(cfg, list, req.Entry ?? throw new ArgumentException("Which program?"));
                change = $"removed {req.Entry} from {list}";
                break;
            case IpcCommands.ProfileAdd:
                ConfigEditor.AddProfile(cfg, list);
                change = $"new profile {list}";
                break;
            default:
                ConfigEditor.RemoveProfile(cfg, list, state.ActiveProfile);
                change = $"deleted profile {list}";
                break;
        }
        cfg.Save(_engine.Paths.ConfigFile);
        _engine.Log.Append(LogTypes.ConfigChanged, new { change, path = req.Path }, caller.Name);

        // A lock is active: apply the change right away (e.g. a removed program goes offline now).
        if (state.ActiveProfile != null) _engine.Heal(cfg, caller.Name);
        return IpcResponse.Success(char.ToUpper(change[0]) + change[1..] + ".");
    }

    // ================================================================ milestone 4: unlock, commitments, play time

    private static PlayDto ToDto(PlayStatus p) => new()
    {
        Allowed = p.Allowed, EndsUtc = p.EndsUtc, NextStartUtc = p.NextStartUtc, UsedTodayMinutes = p.UsedTodayMinutes,
        DailyLimitMinutes = p.DailyLimitMinutes, UsedWeekMinutes = p.UsedWeekMinutes, WeeklyLimitMinutes = p.WeeklyLimitMinutes,
        GraceAvailable = p.GraceAvailable, GraceActive = p.GraceActive, Reason = p.Reason,
    };

    private IpcResponse UnlockRequest(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var r = _rules.RequestUnlock(cfg, state, req.Profile ?? throw new ArgumentException("Which profile?"), req.Text ?? "", Now);
        _engine.SaveState(state);
        _engine.Log.Append(LogTypes.UnlockRequested, new { target = r.TargetProfile, from = r.FromProfile, reason = r.Reason }, caller.Name);
        return IpcResponse.Success($"Unlock requested. You can confirm it from {_rules.LocalText(r.ReadyUtc)} to {_rules.LocalText(r.ExpiresUtc)}.");
    }

    private IpcResponse UnlockConfirm(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var pending = state.PendingUnlock;
        string target;
        try { target = _rules.ConfirmUnlock(state, req.Text ?? "", Now); }
        finally { _engine.SaveState(state); }
        _engine.Log.Append(LogTypes.UnlockConfirmed, new { target, reason = pending?.Reason }, caller.Name);
        SwitchInternal(cfg, target, caller.Name, viaUnlock: true);
        var after = _engine.LoadState();
        return IpcResponse.Success(after.MaintenanceUntilUtc is { } mu
            ? $"{target} is active until {_rules.LocalText(mu)}; then NetGuard goes back to {after.MaintenanceReturnTo ?? "your previous profile"}."
            : $"{target} is now active.");
    }

    private IpcResponse UnlockCancel(CallerInfo caller)
    {
        var state = _engine.LoadState();
        var pending = state.PendingUnlock;
        _rules.CancelUnlock(state, Now);
        _engine.SaveState(state);
        _engine.Log.Append(LogTypes.UnlockCancelled, new { target = pending?.TargetProfile }, caller.Name);
        return IpcResponse.Success("Unlock request cancelled. (It still counts towards today's requests.)");
    }

    private IpcResponse Commit(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        if (!RecoveryCode.Exists(state)) return Refuse(caller, req, "Create your recovery code first (Setup).");
        var until = req.Until ?? throw new ArgumentException("Until when?");
        var switchTo = _rules.Commit(cfg, state, req.Profile ?? throw new ArgumentException("Which profile?"), until, Now);
        _engine.SaveState(state);
        _engine.Log.Append(LogTypes.CommitmentMade, new { profile = state.Commitment!.Profile, until = _rules.LocalText(until) }, caller.Name);
        if (switchTo != null) SwitchInternal(cfg, switchTo, caller.Name, viaUnlock: false);
        return IpcResponse.Success($"Committed to {state.Commitment!.Profile} until {_rules.LocalText(until)}.");
    }

    private IpcResponse Grace(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var until = _rules.TakeGrace(cfg, state, req.Profile ?? state.ActiveProfile ?? "", Now);
        _engine.SaveState(state);
        _engine.Log.Append(LogTypes.GraceTaken, new { profile = state.ActiveProfile, until = _rules.LocalText(until) }, caller.Name);
        return IpcResponse.Success($"{cfg.Timings.GraceMinutes} more minutes, until {_rules.LocalText(until)}. Tomorrow's play time is {cfg.Timings.GraceMinutes} minutes shorter.");
    }

    private IpcResponse PlanGet()
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var plans = cfg.Profiles.Where(p => p.Kind == ProfileKind.Allowlist && p.Programs.Count > 0).Select(p =>
        {
            var pending = state.PendingPlanChanges.FirstOrDefault(c => string.Equals(c.Profile, p.Name, StringComparison.OrdinalIgnoreCase));
            return new PlanDto
            {
                Profile = p.Name,
                Plan = p.Plan,
                Programs = p.Programs.Select(e => e.Name).ToList(),
                HasPending = pending != null,
                PendingPlan = pending?.Plan,
                PendingEffectiveUtc = pending?.EffectiveUtc,
                FallbackChoices = cfg.Profiles.Where(x => x.Kind == ProfileKind.Allowlist && x.Plan == null && x.Name != p.Name).Select(x => x.Name).ToList(),
            };
        }).ToList();
        return new IpcResponse { Ok = true, Plans = plans };
    }

    private IpcResponse PlanSet(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var profile = req.Profile ?? throw new ArgumentException("Which profile?");
        var pending = _rules.ChangePlan(cfg, state, profile, req.Plan, Now);
        _engine.SaveState(state);
        if (pending == null)
        {
            cfg.Validate();
            cfg.Save(_engine.Paths.ConfigFile);
            var how = state.ActiveProfile == null ? "applied now (no lock active)" : "stricter plan, applied now";
            _engine.Log.Append(LogTypes.PlanChanged, new { profile, change = req.Plan == null ? $"plan removed – {how}" : how }, caller.Name);
            if (state.ActiveProfile != null) _engine.Heal(cfg, caller.Name);
            return IpcResponse.Success(state.ActiveProfile == null
                ? "Saved and applied right away – no profile is active, so you can adjust freely. Once you start a lock, looser changes take 72 hours."
                : "Saved – the plan is stricter (or equal), so it applies right away.");
        }
        _engine.Log.Append(LogTypes.PlanChangePending, new { profile, effective = _rules.LocalText(pending.EffectiveUtc) }, caller.Name);
        return IpcResponse.Success($"This plan is looser than the current one, so it takes effect in {cfg.Timings.PlanLoosenDelayHours} hours " +
                                   $"({_rules.LocalText(pending.EffectiveUtc)}). You can cancel it until then.");
    }

    private IpcResponse PlanCancel(IpcRequest req, CallerInfo caller)
    {
        var state = _engine.LoadState();
        var removed = state.PendingPlanChanges.RemoveAll(c => string.Equals(c.Profile, req.Profile, StringComparison.OrdinalIgnoreCase));
        _engine.SaveState(state);
        if (removed > 0) _engine.Log.Append(LogTypes.PlanChanged, new { profile = req.Profile, change = "waiting change cancelled" }, caller.Name);
        return IpcResponse.Success(removed > 0 ? "The waiting change was cancelled." : "There was no waiting change.");
    }

    /// <summary>An unsigned program changed (update or swap): accept its new version. Loosening → normal internet or Maintenance only.</summary>
    private IpcResponse Reapprove(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var d = ProfilePolicy.CanLoosenConfig(cfg, state.ActiveProfile);
        if (!d.Allowed) return Refuse(caller, req, d.Reason.Replace("Adding programs", "Re-approving a changed program"));
        var p = cfg.FindProfile(req.Profile ?? "") ?? throw new InvalidOperationException("Which profile?");
        var e = p.Programs.FirstOrDefault(x => string.Equals(x.Name, req.Entry, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Which program?");
        if (e.Pin != PinMode.Sha256) throw new InvalidOperationException("Only programs pinned to an exact version can be re-approved.");
        var file = e.Paths.SelectMany(x => NetGuard.Core.Paths.PathGlob.Expand(x)).FirstOrDefault() ?? throw new InvalidOperationException("The file wasn't found.");
        e.Sha256 = ProgramResolver.Sha256Of(file);
        cfg.Save(_engine.Paths.ConfigFile);
        _engine.Log.Append(LogTypes.ConfigChanged, new { change = $"re-approved the new version of {e.Name}", path = file }, caller.Name);
        if (state.ActiveProfile != null) _engine.Heal(cfg, caller.Name);
        return IpcResponse.Success($"The current version of {e.Name} is approved.");
    }

    // ================================================================ practice mode

    private sealed class PracticeSnapshot
    {
        public List<UnlockRequest> UnlockRequests { get; set; } = new();
        public Commitment? Commitment { get; set; }
        public Dictionary<string, Dictionary<string, int>> PlayUsage { get; set; } = new();
        public List<GraceRecord> Graces { get; set; } = new();
        public List<PendingPlanChange> PendingPlanChanges { get; set; } = new();
        public DateTimeOffset? LastMaintenanceUtc { get; set; }
    }

    /// <summary>
    /// Practice mode: only from normal internet (so it can't shorten a real lock), at most 60 minutes.
    /// The same SYSTEM safety task as test locks makes sure normal internet comes back.
    /// </summary>
    private IpcResponse PracticeStart(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        if (state.ActiveProfile != null) return Refuse(caller, req, "Practice mode can only be started with normal internet (no profile active).");
        if (state.Practice != null) return IpcResponse.Fail("Practice mode is already running.");
        if (!RecoveryCode.Exists(state)) return Refuse(caller, req, "Create your recovery code first (Setup).");
        var minutes = Math.Clamp(req.Minutes ?? 30, 5, 60);
        var now = Now;
        var snap = new PracticeSnapshot
        {
            UnlockRequests = state.UnlockRequests, Commitment = state.Commitment, PlayUsage = state.PlayUsage,
            Graces = state.Graces, PendingPlanChanges = state.PendingPlanChanges, LastMaintenanceUtc = state.LastMaintenanceUtc,
        };
        state.Practice = new PracticeSession
        {
            StartedUtc = now,
            EndsUtc = now.AddMinutes(minutes),
            Snapshot = System.Text.Json.JsonSerializer.Serialize(snap, NetGuardConfig.JsonOptions),
        };
        _engine.SaveState(state);
        if (File.Exists(TestMode.NgctlPath))
            DeadManSwitch.Arm(DateTime.Now.AddMinutes(minutes).AddSeconds(45), TestMode.NgctlPath, ProgramDir);
        _engine.Log.Append(LogTypes.PracticeStarted, new { minutes }, caller.Name);
        _ = cfg;
        return IpcResponse.Success($"Practice mode for {minutes} minutes: unlock wait 1 min, confirm window 3 min, short passage, Maintenance 2 min, " +
                                   "grace 1 min, play time counts 10× faster, looser plan changes after 2 min. " +
                                   "Afterwards normal internet comes back and nothing you did counts.");
    }

    private IpcResponse EndPractice(string reason, string actor)
    {
        var state = _engine.LoadState();
        if (state.Practice == null) return IpcResponse.Fail("Practice mode isn't running.");
        if (state.ActiveProfile != null) _engine.Recover("practice mode ended", actor);
        state = _engine.LoadState();
        try
        {
            var snap = System.Text.Json.JsonSerializer.Deserialize<PracticeSnapshot>(state.Practice!.Snapshot, NetGuardConfig.JsonOptions) ?? new PracticeSnapshot();
            state.UnlockRequests = snap.UnlockRequests;
            state.Commitment = snap.Commitment;
            state.PlayUsage = new Dictionary<string, Dictionary<string, int>>(snap.PlayUsage, StringComparer.OrdinalIgnoreCase);
            state.Graces = snap.Graces;
            state.PendingPlanChanges = snap.PendingPlanChanges;
            state.LastMaintenanceUtc = snap.LastMaintenanceUtc;
        }
        catch (Exception e) { _logger.LogWarning(e, "Practice snapshot could not be restored"); }
        state.Practice = null;
        state.MaintenanceReturnTo = null;
        state.MaintenanceUntilUtc = null;
        _engine.SaveState(state);
        try { DeadManSwitch.Disarm(); } catch { }
        _rules.Practice = false;
        _engine.Log.Append(LogTypes.PracticeEnded, new { reason }, actor);
        Protect();
        return IpcResponse.Success("Practice mode ended. Normal internet is back, and nothing from practice counts.");
    }
}
