using System.Runtime.Versioning;
using System.Text;
using NetGuard.Core;
using NetGuard.Core.Config;
using NetGuard.Core.Hosts;
using NetGuard.Core.Ipc;
using NetGuard.Core.Logging;
using NetGuard.Core.Policy;
using NetGuard.Core.Reporting;
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

    public DateTimeOffset? LastCheckUtc { get; private set; }

    private readonly IHostApplicationLifetime _lifetime;

    public Guard(ILogger<Guard> logger, IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _lifetime = lifetime;
        _engine = new Engine(new ComFirewall(), new AuthenticodeVerifier(), new NetGuardPaths(), HostsFile.DefaultPath);
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
        return interval;
    }

    /// <summary>One self-healing pass.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            try
            {
                var state = _engine.LoadState();
                if (state.ActiveProfile == null) return;

                // A test lock whose time is up ends here (the dead-man task is only the backup).
                if (state.TestMode is { } tm && tm.DeadlineUtc <= DateTimeOffset.UtcNow)
                {
                    TestMode.End(_engine, ServiceActor, endedEarly: false);
                    Protect();
                    return;
                }

                var cfg = TryLoadConfig();
                if (cfg == null) return;

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
        };
        if (cfg == null) return dto;

        foreach (var p in cfg.Profiles)
        {
            var sw = ProfilePolicy.CanSwitch(cfg, state.ActiveProfile, p.Name);
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
            return Refuse(caller, req, "Create your recovery code first (Setup step 3). Without it there's no way out of a lock in an emergency.");
        var decision = ProfilePolicy.CanSwitch(cfg, state.ActiveProfile, target);
        if (!decision.Allowed) return Refuse(caller, req, decision.Reason);

        // A permanent lock must not be undone by a leftover test-mode safety task.
        DeadManSwitch.Disarm();
        var (desired, _, result) = _engine.ApplyProfile(cfg, target, caller.Name, null);
        Protect();
        return result.Success
            ? IpcResponse.Success($"{desired.ProfileName} is now active.\n\n" + Report.Result(result))
            : IpcResponse.Fail(Report.Result(result));
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
}
