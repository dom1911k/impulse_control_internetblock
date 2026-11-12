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

/// <summary>Practice mode: short timings for a limited time; afterwards everything it produced is undone.</summary>
public sealed class PracticeSession
{
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset EndsUtc { get; set; }
    /// <summary>JSON of the time-based state before practice started (restored at the end).</summary>
    public string Snapshot { get; set; } = "";
}

public sealed class PenaltyRecord
{
    public DateTimeOffset Utc { get; set; }
    public int Minutes { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>"Update &amp; shut down": Windows Update and Defender get internet for a while; the service runs the updates.</summary>
public sealed class UpdateRunState
{
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset UntilUtc { get; set; }
    public bool ShutdownWhenDone { get; set; } = true;
    public string Phase { get; set; } = "starting";
    public string Message { get; set; } = "";
    public bool Done { get; set; }
    public bool Failed { get; set; }
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
    /// <summary>Seconds each profile was active, per profile and local date (yyyy-MM-dd). Play-time limits and statistics.</summary>
    public Dictionary<string, Dictionary<string, int>> PlayUsage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Seconds the game itself (e.g. Wow.exe) was actually running, per game profile and local date.</summary>
    public Dictionary<string, Dictionary<string, int>> GameRunning { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GraceRecord> Graces { get; set; } = new();
    public List<PendingPlanChange> PendingPlanChanges { get; set; } = new();
    /// <summary>NetGuard's own clock (ignores jumps of the Windows clock), saved across restarts.</summary>
    public DateTimeOffset? TrustedNowUtc { get; set; }

    public PracticeSession? Practice { get; set; }

    // ---- night guard, penalties, bass practice, updates
    public List<PenaltyRecord> Penalties { get; set; } = new();
    /// <summary>Seconds Rocksmith was running, per local date (yyyy-MM-dd).</summary>
    public Dictionary<string, int> BassPractice { get; set; } = new();
    public UpdateRunState? UpdateRun { get; set; }
    public DateTimeOffset? LastUpdateRunUtc { get; set; }
    /// <summary>False while the service runs; true after a clean stop. Used to notice the service being killed.</summary>
    public bool ServiceStoppedCleanly { get; set; } = true;
    public long LastTickUptimeMs { get; set; }
    /// <summary>Scheduled tasks known to NetGuard (new SYSTEM tasks during a lock are checked).</summary>
    public List<string> KnownTasks { get; set; } = new();
    public string? LatestVersionSeen { get; set; }
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>The "Bass check" window is open until then.</summary>
    public DateTimeOffset? BrowseWindowUntilUtc { get; set; }
    public List<DateTimeOffset> BrowseWindowStartsUtc { get; set; } = new();

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
