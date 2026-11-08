using System.Text.Json;
using NetGuard.Core.Config;

namespace NetGuard.Core.State;

public sealed class DisabledRuleRecord
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset DisabledAtUtc { get; set; }
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

    public static NetGuardState Load(string path)
    {
        if (!File.Exists(path)) return new NetGuardState();
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return new NetGuardState();
        return JsonSerializer.Deserialize<NetGuardState>(json, NetGuardConfig.JsonOptions) ?? new NetGuardState();
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, NetGuardConfig.JsonOptions));
}
