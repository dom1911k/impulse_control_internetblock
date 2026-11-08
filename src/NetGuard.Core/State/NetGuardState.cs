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

    public static NetGuardState Load(string path)
    {
        if (!File.Exists(path)) return new NetGuardState();
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return new NetGuardState();
        return JsonSerializer.Deserialize<NetGuardState>(json, NetGuardConfig.JsonOptions) ?? new NetGuardState();
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, NetGuardConfig.JsonOptions));
}
