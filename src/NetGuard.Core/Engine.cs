using NetGuard.Core.Config;
using NetGuard.Core.Firewall;
using NetGuard.Core.Hosts;
using NetGuard.Core.Logging;
using NetGuard.Core.Paths;
using NetGuard.Core.Planning;
using NetGuard.Core.State;

namespace NetGuard.Core;

public sealed record ActionError(string Action, string Error);

public sealed class ExecutionResult
{
    public List<PlanAction> Done { get; } = new();
    public List<ActionError> Errors { get; } = new();
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Turns a profile into firewall changes. Shared by ngctl (milestone 1) and the service (milestone 2).
/// The engine never deletes or edits rules that aren't NetGuard's, except switching foreign
/// outbound allow rules off (and back on during recovery) – every one of those is recorded in state.
/// </summary>
public sealed class Engine
{
    private readonly IFirewall _fw;
    private readonly ProgramResolver _resolver;
    private readonly NetGuardPaths _paths;
    private readonly string _hostsPath;
    private readonly JsonlLog _log;
    private readonly Func<string, string>? _expandEnv;

    public Engine(IFirewall fw, ISignatureVerifier verifier, NetGuardPaths paths, string hostsPath, Func<string, string>? expandEnv = null)
    {
        _fw = fw;
        _expandEnv = expandEnv;
        _resolver = new ProgramResolver(verifier, expandEnv);
        _paths = paths;
        _hostsPath = hostsPath;
        _log = new JsonlLog(paths.LogFile);
    }

    public JsonlLog Log => _log;
    public NetGuardPaths Paths => _paths;

    public NetGuardConfig LoadConfig() => NetGuardConfig.Load(_paths.ConfigFile);
    public NetGuardState LoadState() => NetGuardState.Load(_paths.StateFile);
    public void SaveState(NetGuardState s) => s.Save(_paths.StateFile);

    public DesiredState BuildDesired(NetGuardConfig cfg, string profile) =>
        DesiredStateBuilder.Build(cfg, profile, _resolver, _expandEnv);

    public Plan PlanApply(NetGuardConfig cfg, DesiredState desired) =>
        Planner.Diff(desired, _fw.GetSnapshot(), HostsFile.IsUpToDate(ReadHosts(), desired.BlockedHosts), cfg.ProtectedRuleGroups);

    public Plan PlanRecovery(NetGuardState state) =>
        Planner.Recovery(_fw.GetSnapshot(), state, ReadHosts().Contains(HostsFile.BeginMarker));

    /// <summary>Make the firewall match a profile and record it as active.</summary>
    public (DesiredState desired, Plan plan, ExecutionResult result) ApplyProfile(
        NetGuardConfig cfg, string profile, string actor, TestModeInfo? testMode)
    {
        using var _ = EngineLock.Acquire(_paths);
        var desired = BuildDesired(cfg, profile);
        var plan = PlanApply(cfg, desired);
        var state = LoadState();
        var result = Execute(plan, state);

        state.ActiveProfile = desired.ProfileName;
        state.ActiveSinceUtc = DateTimeOffset.UtcNow;
        state.TestMode = testMode;
        SaveState(state);

        foreach (var r in desired.RejectedPrograms)
            _log.Append(LogTypes.Rejected, new { r.EntryName, r.Path, r.Detail }, actor);

        _log.Append(LogTypes.ProfileApplied, new
        {
            profile = desired.ProfileName,
            testModeUntil = testMode?.DeadlineUtc,
            changes = result.Done.Select(a => a.Describe()).ToList(),
            allowed = desired.AllowedPrograms.Select(p => p.Path).ToList(),
            warnings = desired.Warnings,
        }, actor);
        if (!result.Success)
            _log.Append(LogTypes.ApplyError, new { profile = desired.ProfileName, errors = result.Errors }, actor);

        return (desired, plan, result);
    }

    /// <summary>Normal internet again: undo everything NetGuard did.</summary>
    public (Plan plan, ExecutionResult result) Recover(string reason, string actor)
    {
        // If another process is restoring right now, we wait and then find little or nothing left to do.
        using var _ = EngineLock.Acquire(_paths);
        var state = LoadState();
        var plan = PlanRecovery(state);
        var result = Execute(plan, state);

        // Forget records whose rule no longer exists or is enabled again.
        var snap = _fw.GetSnapshot();
        state.DisabledForeignRules.RemoveAll(rec => !snap.Rules.Any(r => r.Key == rec.Key && !r.Enabled));
        state.ActiveProfile = null;
        state.ActiveSinceUtc = null;
        state.TestMode = null;
        SaveState(state);

        _log.Append(LogTypes.Recovery, new
        {
            reason,
            changes = result.Done.Select(a => a.Describe()).ToList(),
            errors = result.Errors,
        }, actor);
        return (plan, result);
    }

    public ExecutionResult Execute(Plan plan, NetGuardState state)
    {
        var result = new ExecutionResult();
        foreach (var action in plan.Actions)
        {
            try
            {
                switch (action)
                {
                    case EnableFirewallAction a: _fw.SetEnabled(a.Profile, true); break;
                    case SetDefaultOutboundAction a: _fw.SetDefaultOutbound(a.Profile, a.Action); break;
                    case RemoveRuleAction a:
                        if (!a.Name.StartsWith(NetGuardNames.Prefix, StringComparison.Ordinal))
                            throw new InvalidOperationException("Refusing to remove a rule that isn't NetGuard's.");
                        _fw.RemoveRules(a.Name);
                        break;
                    case AddRuleAction a: _fw.AddRule(a.Rule); break;
                    case DisableForeignRuleAction a:
                        // Record first, so a crash between the two steps can never "lose" a disabled rule.
                        if (!state.DisabledForeignRules.Any(r => r.Key == a.Key))
                        {
                            state.DisabledForeignRules.Add(new DisabledRuleRecord { Key = a.Key, Name = a.Name, DisabledAtUtc = DateTimeOffset.UtcNow });
                            SaveState(state);
                        }
                        _fw.SetRuleEnabled(a.Name, a.Key, false);
                        break;
                    case EnableForeignRuleAction a: _fw.SetRuleEnabled(a.Name, a.Key, true); break;
                    case WriteHostsAction a: WriteHosts(a.Hosts); break;
                    default: throw new NotSupportedException(action.GetType().Name);
                }
                result.Done.Add(action);
            }
            catch (Exception e)
            {
                result.Errors.Add(new ActionError(action.Describe(), e.Message));
            }
        }
        return result;
    }

    private string ReadHosts()
    {
        try { return File.Exists(_hostsPath) ? File.ReadAllText(_hostsPath) : ""; }
        catch (IOException) { return ""; }
    }

    private void WriteHosts(IReadOnlyList<string> hosts)
    {
        var text = ReadHosts();
        var updated = HostsFile.Rewrite(text, hosts);
        // Hosts is often briefly locked by antivirus scanning – retry a few times.
        for (var attempt = 1; ; attempt++)
        {
            try { File.WriteAllText(_hostsPath, updated); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(200 * attempt); }
        }
    }
}
