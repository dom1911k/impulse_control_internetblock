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

    public const string LoosenHint =
        "Looser than the current profile. The calm unlock process comes in milestone 4; until then only the emergency restore (recovery code) ends a lock.";

    /// <summary>Permanent switch to <paramref name="target"/>.</summary>
    public static SwitchDecision CanSwitch(NetGuardConfig cfg, string? activeProfile, string target)
    {
        var t = cfg.FindProfile(target);
        if (t == null) return new(false, $"There is no profile called '{target}'.");
        var cur = activeProfile == null ? null : cfg.FindProfile(activeProfile);
        return IsAtLeastAsStrict(t, cur)
            ? new(true, cur == null ? "from normal internet" : "stricter or equal")
            : new(false, LoosenHint);
    }

    /// <summary>Test locks end by themselves, so starting one while a real lock is active would be a way out.</summary>
    public static SwitchDecision CanStartTest(string? activeProfile) =>
        activeProfile == null
            ? new(true, "no lock active")
            : new(false, "Test locks end by themselves, so they're only possible with normal internet.");
}
