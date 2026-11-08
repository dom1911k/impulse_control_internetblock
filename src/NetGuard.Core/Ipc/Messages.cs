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
}

public sealed class IpcRequest
{
    public string Command { get; set; } = "";
    public string? Profile { get; set; }
    public int? Minutes { get; set; }
    public bool? On { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Text { get; set; }
    public StatusDto? Status { get; set; }
    public List<LogEntryDto>? History { get; set; }

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
    public bool CallerIsAdmin { get; set; }
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
