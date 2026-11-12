using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetGuard.Core.Ipc;

/// <summary>
/// Messages between the NetGuard window (any user) and the NetGuard service (SYSTEM) over the named pipe.
/// One JSON line in, one JSON line out, per connection. The service decides everything;
/// the window can only ask.
/// </summary>
public static class IpcCommands
{
    public const string Status = "status";
    public const string StatusText = "status-text";
    public const string Switch = "switch";
    public const string TestStart = "test-start";
    public const string TestEnd = "test-end";
    public const string Recover = "recover";
    public const string Preview = "preview";
    public const string Programs = "programs";
    public const string History = "history";
    public const string Detect = "detect";
    public const string Init = "init";
    public const string Audit = "audit";
    public const string Blocked = "blocked";
    public const string RecoveryCreate = "recovery-create";
    public const string Update = "update";
    public const string PrepareUninstall = "prepare-uninstall";
    public const string ConfigGet = "config-get";
    public const string InspectFile = "inspect-file";
    public const string ProgramAdd = "program-add";
    public const string ProgramRemove = "program-remove";
    public const string ProfileAdd = "profile-add";
    public const string ProfileRemove = "profile-remove";
    public const string UnlockRequest = "unlock-request";
    public const string UnlockConfirm = "unlock-confirm";
    public const string UnlockCancel = "unlock-cancel";
    public const string Commit = "commit";
    public const string Grace = "grace";
    public const string PlanGet = "plan-get";
    public const string PlanSet = "plan-set";
    public const string PlanCancel = "plan-cancel";
    public const string ProgramReapprove = "program-reapprove";
    public const string PracticeStart = "practice-start";
    public const string PracticeEnd = "practice-end";
    public const string Summary = "summary";
    public const string Stats = "stats";
    public const string WindowStart = "window-start";
    public const string UpdatesStart = "updates-start";
    public const string UpdateCheck = "update-check";
    public const string WindowEnd = "window-end";
    public const string WebsiteAdd = "website-add";
    public const string WebsiteRemove = "website-remove";
}

public sealed class IpcRequest
{
    public string Command { get; set; } = "";
    public string? Profile { get; set; }
    public int? Minutes { get; set; }
    public bool? On { get; set; }
    /// <summary>Recovery code, for requests that need it.</summary>
    public string? Code { get; set; }
    /// <summary>A file path (program editor).</summary>
    public string? Path { get; set; }
    /// <summary>A program entry's name (program editor).</summary>
    public string? Entry { get; set; }
    /// <summary>Free text: unlock reason, or the retyped passage.</summary>
    public string? Text { get; set; }
    public DateTimeOffset? Until { get; set; }
    public Config.PlayPlan? Plan { get; set; }
    /// <summary>0 = this week, -1 = last week …</summary>
    public int? WeekOffset { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Text { get; set; }
    public StatusDto? Status { get; set; }
    public List<LogEntryDto>? History { get; set; }
    public ConfigDto? Config { get; set; }
    public List<PlanDto>? Plans { get; set; }
    public Reporting.SummaryDto? Summary { get; set; }
    public Reporting.StatsDto? Stats { get; set; }
    public ProgramDto? Program { get; set; }

    public static IpcResponse Success(string? text = null) => new() { Ok = true, Text = text };
    public static IpcResponse Fail(string error) => new() { Ok = false, Error = error };
}

public sealed class StatusDto
{
    public string ServiceVersion { get; set; } = "";
    public bool ConfigExists { get; set; }
    public string? ActiveProfile { get; set; }
    public DateTimeOffset? ActiveSinceUtc { get; set; }
    public DateTimeOffset? TestDeadlineUtc { get; set; }
    public DateTimeOffset? LastCheckUtc { get; set; }
    public DateTimeOffset? LastTamperUtc { get; set; }
    public string CallerName { get; set; } = "";
    public bool RecoveryCodeExists { get; set; }
    public DateTimeOffset? RecoveryCodeCreatedUtc { get; set; }
    public bool SelfProtectionActive { get; set; }
    /// <summary>NetGuard's trusted "now" – countdowns use this, not the PC clock.</summary>
    public DateTimeOffset ServerNowUtc { get; set; }
    public string? CommitmentProfile { get; set; }
    public DateTimeOffset? CommitmentUntilUtc { get; set; }
    public UnlockDto? Unlock { get; set; }
    public int UnlocksLeftToday { get; set; }
    public DateTimeOffset? MaintenanceUntilUtc { get; set; }
    public bool MaintenanceReminder { get; set; }
    public List<string> RejectedFiles { get; set; } = new();
    /// <summary>Practice mode is running until then (short waiting times; normal internet afterwards).</summary>
    public DateTimeOffset? PracticeUntilUtc { get; set; }
    /// <summary>How many extra minutes the grace gives right now (1 in practice mode).</summary>
    public int GraceMinutes { get; set; }
    /// <summary>The short window ("Bass check").</summary>
    public string WindowName { get; set; } = "Bass check";
    public int WindowMinutes { get; set; }
    public DateTimeOffset? WindowUntilUtc { get; set; }
    public bool WindowAllowed { get; set; }
    public string WindowHint { get; set; } = "";
    public DateTimeOffset? NightGuardUntilUtc { get; set; }
    public string NightProfile { get; set; } = "Offline";
    /// <summary>"Update &amp; shut down" is running.</summary>
    public string? UpdatePhase { get; set; }
    public bool UpdateDue { get; set; }
    public int? DaysSinceUpdate { get; set; }
    public string? UpdateAvailableVersion { get; set; }
    public int BassTodayMinutes { get; set; }
    public int BassEarnedTodayMinutes { get; set; }
    public int BassBonusCapMinutes { get; set; }
    public int PenaltyMinutesToday { get; set; }
    public List<ProfileDto> Profiles { get; set; } = new();
}

public sealed class ProfileDto
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Programs { get; set; } = new();
    public bool IsActive { get; set; }
    public bool SwitchAllowed { get; set; }
    public string SwitchHint { get; set; } = "";
    public bool TestAllowed { get; set; }
    public string TestHint { get; set; } = "";
    public bool UnlockAllowed { get; set; }
    public string UnlockHint { get; set; } = "";
    public PlayDto? Play { get; set; }
}

public sealed class LogEntryDto
{
    public DateTimeOffset Ts { get; set; }
    public string Type { get; set; } = "";
    public string What { get; set; } = "";
    public string? Actor { get; set; }
    public string Details { get; set; } = "";
}

public static class IpcJson
{
    public const string PipeName = "NetGuard";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string line) => JsonSerializer.Deserialize<T>(line, Options);
}

public sealed class ConfigDto
{
    /// <summary>Adding programs (loosening) is possible right now.</summary>
    public bool CanLoosen { get; set; }
    public string LoosenHint { get; set; } = "";
    public List<ProfileConfigDto> Profiles { get; set; } = new();
    public ProfileConfigDto AlwaysBlocked { get; set; } = new();
    public ProfileConfigDto Window { get; set; } = new();
    public List<string> Websites { get; set; } = new();
    public string WindowName { get; set; } = "Bass check";
}

public sealed class ProfileConfigDto
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool IsActive { get; set; }
    public List<ProgramDto> Programs { get; set; } = new();
}

public sealed class ProgramDto
{
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = new();
    public string Pin { get; set; } = "";
    public string? Signer { get; set; }
    public string PinDescription { get; set; } = "";
    public List<FileDto> Files { get; set; } = new();
}

public sealed class FileDto
{
    public string Path { get; set; } = "";
    public bool Allowed { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class UnlockDto
{
    public string TargetProfile { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset RequestedUtc { get; set; }
    public DateTimeOffset ReadyUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    /// <summary>Only sent once the waiting time is over.</summary>
    public string? Passage { get; set; }
}

public sealed class PlayDto
{
    public bool Allowed { get; set; }
    public DateTimeOffset? EndsUtc { get; set; }
    public DateTimeOffset? NextStartUtc { get; set; }
    public int UsedTodayMinutes { get; set; }
    public int? DailyLimitMinutes { get; set; }
    public int UsedWeekMinutes { get; set; }
    public int? WeeklyLimitMinutes { get; set; }
    public bool GraceAvailable { get; set; }
    public bool GraceActive { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>End of the current play window (independent of how much time is left).</summary>
    public DateTimeOffset? WindowEndsUtc { get; set; }
    /// <summary>Play time left today/this week, whichever is smaller (null = no limit).</summary>
    public int? MinutesLeftToday { get; set; }
}

public sealed class PlanDto
{
    public string Profile { get; set; } = "";
    public Config.PlayPlan? Plan { get; set; }
    public List<string> Programs { get; set; } = new();
    public Config.PlayPlan? PendingPlan { get; set; }
    public bool HasPending { get; set; }
    public DateTimeOffset? PendingEffectiveUtc { get; set; }
    public List<string> FallbackChoices { get; set; } = new();
    /// <summary>A lock is active: looser changes wait 72 hours (otherwise every change applies at once).</summary>
    public bool LockActive { get; set; }
}
