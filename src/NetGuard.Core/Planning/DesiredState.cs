using NetGuard.Core.Config;
using NetGuard.Core.Firewall;
using NetGuard.Core.Paths;

namespace NetGuard.Core.Planning;

public sealed class DesiredState
{
    public string ProfileName { get; init; } = "";
    public ProfileKind Kind { get; init; }
    public FwAction DefaultOutbound { get; init; }
    public bool DisableForeignAllowRules { get; init; }
    public List<RuleSpec> Rules { get; } = new();
    public List<string> BlockedHosts { get; } = new();
    public List<ResolvedProgram> AllowedPrograms { get; } = new();
    public List<ResolvedProgram> RejectedPrograms { get; } = new();
    public List<ResolvedProgram> BlockedPrograms { get; } = new();
    public List<string> Warnings { get; } = new();
}

public static class DesiredStateBuilder
{
    public const string ManagedDescription = "Managed by NetGuard. Manual changes are undone automatically.";

    public const string GameBlockProfile = "PlayTime";

    public const string BrowseProfile = "Window";

    public static DesiredState Build(NetGuardConfig cfg, string profileName, ProgramResolver resolver, Func<string, string>? expandEnv = null,
        IEnumerable<ProgramEntry>? extraBlocked = null, IEnumerable<ProgramEntry>? extraAllowed = null,
        IEnumerable<ProgramEntry>? alwaysAllowed = null, IEnumerable<ServiceRuleEntry>? extraServiceRules = null)
    {
        expandEnv ??= Environment.ExpandEnvironmentVariables;
        var profile = cfg.FindProfile(profileName)
                      ?? throw new ArgumentException($"There is no profile called '{profileName}'. Known: {string.Join(", ", cfg.Profiles.Select(p => p.Name))}");

        var allowList = profile.Kind == ProfileKind.Allowlist;
        var d = new DesiredState
        {
            ProfileName = profile.Name,
            Kind = profile.Kind,
            DefaultOutbound = allowList ? FwAction.Block : FwAction.Allow,
            DisableForeignAllowRules = allowList,
        };

        var names = new RuleNamer();

        // 1) base service rules (DNS, time sync, connectivity check)
        foreach (var s in cfg.BaseServiceRules)
        {
            d.Rules.Add(new RuleSpec(
                $"{NetGuardNames.Prefix}{profile.Name}:{s.Id}",
                FwAction.Allow,
                expandEnv(s.Program),
                s.Service,
                FwProtocol.Parse(s.Protocol),
                string.IsNullOrWhiteSpace(s.RemotePorts) ? null : s.RemotePorts,
                ManagedDescription));
        }

        // 2) allowed programs
        if (allowList)
        {
            foreach (var entry in profile.Programs)
            {
                var resolved = resolver.Resolve(entry);
                if (resolved.Count == 0)
                    d.Warnings.Add($"{entry.Name}: no file found for {string.Join(" ; ", entry.Paths)}");
                foreach (var r in resolved)
                {
                    if (r.Status == ResolvedStatus.Allowed)
                    {
                        d.AllowedPrograms.Add(r);
                        d.Rules.Add(new RuleSpec(names.Next(profile.Name, r.Path), FwAction.Allow, r.Path, null,
                            FwProtocol.Any, null, $"{ManagedDescription} ({entry.Name})"));
                    }
                    else
                    {
                        d.RejectedPrograms.Add(r);
                        d.Warnings.Add($"{entry.Name}: NOT allowed {r.Path} – {r.Detail}");
                    }
                }
            }
        }

        // 1b) system services for a while (Windows Update during "Update & shut down")
        if (allowList)
            foreach (var s in extraServiceRules ?? Enumerable.Empty<ServiceRuleEntry>())
                d.Rules.Add(new RuleSpec($"{NetGuardNames.Prefix}Updates:{s.Id}", FwAction.Allow, expandEnv(s.Program), s.Service,
                    FwProtocol.Parse(s.Protocol), string.IsNullOrWhiteSpace(s.RemotePorts) ? null : s.RemotePorts, $"{ManagedDescription} (updates)"));

        // 1c) NetGuard's own service (update checks) – always
        if (allowList)
            foreach (var entry in alwaysAllowed ?? Enumerable.Empty<ProgramEntry>())
                foreach (var r in resolver.Resolve(entry, checkPins: false))
                    d.Rules.Add(new RuleSpec(names.Next("Self", r.Path), FwAction.Allow, r.Path, null, FwProtocol.Tcp, "443",
                        $"{ManagedDescription} (NetGuard updates)"));

        // 2b) extra programs for a short window ("Bass check", updates) on top of the profile
        if (allowList)
        {
            foreach (var entry in extraAllowed ?? Enumerable.Empty<ProgramEntry>())
            {
                foreach (var r in resolver.Resolve(entry))
                {
                    if (r.Status != ResolvedStatus.Allowed) { d.RejectedPrograms.Add(r); continue; }
                    if (d.AllowedPrograms.Any(a => FwRule.PathEq(a.Path, r.Path))) continue;
                    d.AllowedPrograms.Add(r);
                    d.Rules.Add(new RuleSpec(names.Next(BrowseProfile, r.Path), FwAction.Allow, r.Path, null,
                        FwProtocol.Any, null, $"{ManagedDescription} (short window: {entry.Name})"));
                }
            }
        }

        // 3) always-blocked programs (Steam) – block rules beat allow rules, also in Maintenance
        foreach (var entry in cfg.AlwaysBlocked)
        {
            foreach (var r in resolver.Resolve(entry, checkPins: false))
            {
                d.BlockedPrograms.Add(r);
                d.Rules.Add(new RuleSpec(names.Next(NetGuardNames.AlwaysProfile, r.Path), FwAction.Block, r.Path, null,
                    FwProtocol.Any, null, $"{ManagedDescription} (always blocked: {entry.Name})"));
                if (d.AllowedPrograms.Any(a => FwRule.PathEq(a.Path, r.Path)))
                    d.Warnings.Add($"{r.Path} is both allowed and always-blocked; it stays BLOCKED.");
            }
        }

        // 4) games outside their play time – blocked in every profile, Maintenance included
        foreach (var entry in extraBlocked ?? Enumerable.Empty<ProgramEntry>())
        {
            foreach (var r in resolver.Resolve(entry, checkPins: false))
            {
                d.BlockedPrograms.Add(r);
                d.Rules.Add(new RuleSpec(names.Next(GameBlockProfile, r.Path), FwAction.Block, r.Path, null,
                    FwProtocol.Any, null, $"{ManagedDescription} (outside play time: {entry.Name})"));
            }
        }

        d.BlockedHosts.AddRange(cfg.AlwaysBlockedHosts
            .Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().OrderBy(h => h, StringComparer.Ordinal));
        return d;
    }

    /// <summary>Produces "NetGuard:WoW:Wow.exe", then "NetGuard:WoW:Wow.exe (2)" for a second file with the same name.</summary>
    private sealed class RuleNamer
    {
        private readonly Dictionary<string, int> _seen = new(StringComparer.OrdinalIgnoreCase);

        public string Next(string profile, string path)
        {
            var exe = path.Replace('/', '\\').Split('\\')[^1];
            var baseName = $"{NetGuardNames.Prefix}{profile}:{exe}";
            var n = _seen.TryGetValue(baseName, out var c) ? c + 1 : 1;
            _seen[baseName] = n;
            return n == 1 ? baseName : $"{baseName} ({n})";
        }
    }
}
