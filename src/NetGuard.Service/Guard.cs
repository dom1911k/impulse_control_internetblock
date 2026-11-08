using System.Runtime.Versioning;
using System.Text;
using NetGuard.Core;
using NetGuard.Core.Config;
using NetGuard.Core.Hosts;
using NetGuard.Core.Ipc;
using NetGuard.Core.Logging;
using NetGuard.Core.Policy;
using NetGuard.Core.Reporting;
using NetGuard.Core.Updates;
using NetGuard.Windows;

namespace NetGuard.Service;

public sealed record CallerInfo(string Name, bool IsAdmin);

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

    public Guard(ILogger<Guard> logger)
    {
        _logger = logger;
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
                        StatusReader.Read(_engine).Describe()),
                    IpcCommands.Switch => Switch(req, caller),
                    IpcCommands.TestStart => TestStart(req, caller),
                    IpcCommands.TestEnd => TestEnd(caller),
                    IpcCommands.Recover => AdminOnly(caller, req, Recover),
                    IpcCommands.Preview => Preview(req),
                    IpcCommands.Programs => Programs(),
                    IpcCommands.History => History(),
                    IpcCommands.Detect => AdminOnly(caller, req, _ => IpcResponse.Success(Report.Detected(AppDetector.Detect()))),
                    IpcCommands.Init => AdminOnly(caller, req, Init),
                    IpcCommands.Audit => AdminOnly(caller, req, _ => Audit(req)),
                    IpcCommands.Blocked => AdminOnly(caller, req, _ => Blocked()),
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

    private IpcResponse AdminOnly(CallerInfo caller, IpcRequest req, Func<CallerInfo, IpcResponse> action)
    {
        if (caller.IsAdmin) return action(caller);
        return Refuse(caller, req, "This needs an administrator. Use \"Restart as administrator\" in Tools (Windows will ask for the admin password).");
    }

    private IpcResponse Refuse(CallerInfo caller, IpcRequest req, string reason)
    {
        _engine.Log.Append(LogTypes.Refused, new { request = req.Command, profile = req.Profile, reason }, caller.Name);
        return IpcResponse.Fail(reason);
    }

    private NetGuardConfig RequireConfig() =>
        TryLoadConfig() ?? throw new InvalidOperationException("NetGuard isn't set up yet. An administrator needs to run Setup → Create configuration.");

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
            CallerIsAdmin = caller.IsAdmin,
        };
        if (cfg == null) return dto;

        var testActive = state.ActiveProfile != null && state.TestMode != null;
        foreach (var p in cfg.Profiles)
        {
            var sw = ProfilePolicy.CanSwitch(cfg, state.ActiveProfile, p.Name, caller.IsAdmin);
            var test = ProfilePolicy.CanStartTest(state.ActiveProfile, testActive, caller.IsAdmin);
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
        var decision = ProfilePolicy.CanSwitch(cfg, state.ActiveProfile, target, caller.IsAdmin);
        if (!decision.Allowed) return Refuse(caller, req, decision.Reason);

        // A permanent lock must not be undone by a leftover test-mode safety task.
        DeadManSwitch.Disarm();
        var (desired, _, result) = _engine.ApplyProfile(cfg, target, caller.Name, null);
        return result.Success
            ? IpcResponse.Success($"{desired.ProfileName} is now active.\n\n" + Report.Result(result))
            : IpcResponse.Fail(Report.Result(result));
    }

    private IpcResponse TestStart(IpcRequest req, CallerInfo caller)
    {
        var cfg = RequireConfig();
        var state = _engine.LoadState();
        var decision = ProfilePolicy.CanStartTest(state.ActiveProfile, state.TestMode != null, caller.IsAdmin);
        if (!decision.Allowed) return Refuse(caller, req, decision.Reason);
        var minutes = req.Minutes ?? cfg.Timings.TestModeMinutes;
        var (info, result) = TestMode.Start(_engine, cfg, req.Profile ?? throw new ArgumentException("Which profile?"), minutes, caller.Name);
        return IpcResponse.Success(
            $"Test lock until {info.DeadlineUtc.ToLocalTime():T}. A separate safety task also restores internet shortly after that.\n\n" +
            Report.Result(result));
    }

    private IpcResponse TestEnd(CallerInfo caller)
    {
        var state = _engine.LoadState();
        if (state.ActiveProfile == null || state.TestMode == null) return IpcResponse.Fail("No test lock is running.");
        var result = TestMode.End(_engine, caller.Name, endedEarly: true);
        return IpcResponse.Success("Normal internet is back.\n\n" + Report.Result(result));
    }

    private IpcResponse Recover(CallerInfo caller)
    {
        var (plan, result) = _engine.Recover("restored by an administrator from the NetGuard window", caller.Name);
        try { DeadManSwitch.Disarm(); } catch { }
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

    private IpcResponse Init(CallerInfo caller)
    {
        var d = AppDetector.Detect();
        var cfg = DefaultConfig.Create(d);
        DataDirSecurity.Secure(_engine.Paths);
        cfg.Save(_engine.Paths.ConfigFile);
        _engine.Log.Append(LogTypes.ConfigCreated, new { detected = d }, caller.Name);
        return IpcResponse.Success(Report.Detected(d) + $"\nConfiguration saved to {_engine.Paths.ConfigFile}.\n" +
                                   "Only SYSTEM and administrators can change NetGuard's folder.");
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
}
