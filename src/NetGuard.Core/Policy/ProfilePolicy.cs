using NetGuard.Core.Config;

namespace NetGuard.Core.Policy;

public sealed record SwitchDecision(bool Allowed, string Reason);

/// <summary>
/// Asymmetric friction: moving to a stricter (or equally strict) profile is instant;
/// anything looser needs an administrator (milestone 4 adds the cooldown unlock process).
/// </summary>
public static class ProfilePolicy
{
    /// <summary>True if <paramref name="target"/> lets nothing online that <paramref name="current"/> doesn't.</summary>
    public static bool IsAtLeastAsStrict(ProfileDefinition target, ProfileDefinition? current)
    {
        if (current == null) return true;                        // from normal internet, every profile is stricter
        if (current.Kind == ProfileKind.AllowAll) return true;   // nothing is looser than "everything"
        if (target.Kind == ProfileKind.AllowAll) return false;
        var allowed = current.Programs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return target.Programs.All(p => allowed.Contains(p.Name));
    }

    /// <summary>Permanent switch to <paramref name="target"/>.</summary>
    public static SwitchDecision CanSwitch(NetGuardConfig cfg, string? activeProfile, string target, bool callerIsAdmin)
    {
        var t = cfg.FindProfile(target);
        if (t == null) return new(false, $"There is no profile called '{target}'.");
        var cur = activeProfile == null ? null : cfg.FindProfile(activeProfile);
        if (IsAtLeastAsStrict(t, cur)) return new(true, cur == null ? "from normal internet" : "stricter or equal");
        if (callerIsAdmin) return new(true, "looser, allowed for an administrator");
        return new(false, $"{t.Name} is looser than {cur!.Name}. Loosening will need the unlock process (coming in milestone 4); until then an administrator has to do it.");
    }

    /// <summary>Test locks end by themselves, so starting one while a real lock is active would be a way out.</summary>
    public static SwitchDecision CanStartTest(string? activeProfile, bool testActive, bool callerIsAdmin)
    {
        if (activeProfile == null) return new(true, "no lock active");
        if (testActive) return new(false, "A test lock is already running. End it first.");
        if (callerIsAdmin) return new(true, "administrator");
        return new(false, "A real lock is active. Test locks end by themselves, so they're only possible with normal internet.");
    }
}
