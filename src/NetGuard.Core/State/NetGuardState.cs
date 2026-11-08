using System.Text.Json;
using NetGuard.Core.Config;

namespace NetGuard.Core.State;

public sealed class DisabledRuleRecord
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset DisabledAtUtc { get; set; }
}

public sealed class AppliedRule
{
    public string Name { get; set; } = "";
    public string Program { get; set; } = "";
    public string Action { get; set; } = "";
}

public enum UnlockStatus { Pending, Confirmed, Expired, Cancelled }

public sealed class UnlockRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FromProfile { get; set; } = "";
    public string TargetProfile { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset RequestedUtc { get; set; }
    public DateTimeOffset ReadyUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public string Passage { get; set; } = "";
    public UnlockStatus Status { get; set; }
    public DateTimeOffset? ClosedUtc { get; set; }
}

/// <summary>"No looser than <see cref="Profile"/> until <see cref="UntilUtc"/>."</summary>
public sealed class Commitment
{
    public string Profile { get; set; } = "";
    public DateTimeOffset UntilUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class GraceRecord
{
    public string Profile { get; set; } = "";
    public string Date { get; set; } = "";
    public DateTimeOffset UntilUtc { get; set; }
}

public sealed class PendingPlanChange
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Profile { get; set; } = "";
    public Config.PlayPlan? Plan { get; set; }
    public DateTimeOffset RequestedUtc { get; set; }
    public DateTimeOffset EffectiveUtc { get; set; }
}

public sealed class TestModeInfo
{
    public DateTimeOffset DeadlineUtc { get; set; }
    public string? TaskName { get; set; }
}

/// <summary>What NetGuard has done to this machine. Lives in C:\ProgramData\NetGuard\state.json.</summary>
public sealed class NetGuardState
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Name of the active profile, or null when NetGuard is not enforcing anything.</summary>
    public string? ActiveProfile { get; set; }

    public DateTimeOffset? ActiveSinceUtc { get; set; }

    public TestModeInfo? TestMode { get; set; }

    /// <summary>Foreign (non-NetGuard) outbound allow rules NetGuard switched off. Re-enabled on recovery.</summary>
    public List<DisabledRuleRecord> DisabledForeignRules { get; set; } = new();

    /// <summary>NetGuard rules as last applied. Lets self-healing tell "changed by someone" from "new file after an update".</summary>
    public List<AppliedRule> AppliedRules { get; set; } = new();

    public DateTimeOffset? LastTamperUtc { get; set; }

    // Recovery code: only a salted PBKDF2 hash is kept, never the code itself.
    public string? RecoveryCodeHash { get; set; }
    public string? RecoveryCodeSalt { get; set; }
    public DateTimeOffset? RecoveryCodeCreatedUtc { get; set; }
    public List<DateTimeOffset> WrongCodeAttemptsUtc { get; set; } = new();

    // ---- milestone 4
    public List<UnlockRequest> UnlockRequests { get; set; } = new();
    public Commitment? Commitment { get; set; }
    /// <summary>Maintenance reached through an unlock returns here automatically.</summary>
    public string? MaintenanceReturnTo { get; set; }
    public DateTimeOffset? MaintenanceUntilUtc { get; set; }
    public DateTimeOffset? LastMaintenanceUtc { get; set; }
    /// <summary>Seconds a planned profile was active, per profile and local date (yyyy-MM-dd).</summary>
    public Dictionary<string, Dictionary<string, int>> PlayUsage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GraceRecord> Graces { get; set; } = new();
    public List<PendingPlanChange> PendingPlanChanges { get; set; } = new();
    /// <summary>NetGuard's own clock (ignores jumps of the Windows clock), saved across restarts.</summary>
    public DateTimeOffset? TrustedNowUtc { get; set; }

    public UnlockRequest? PendingUnlock => UnlockRequests.LastOrDefault(r => r.Status == UnlockStatus.Pending);

    public static NetGuardState Load(string path)
    {
        if (!File.Exists(path)) return new NetGuardState();
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return new NetGuardState();
        return JsonSerializer.Deserialize<NetGuardState>(json, NetGuardConfig.JsonOptions) ?? new NetGuardState();
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, NetGuardConfig.JsonOptions));
}
