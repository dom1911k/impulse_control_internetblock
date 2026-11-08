using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetGuard.Core.Logging;

public static class LogTypes
{
    public const string ProfileApplied = "profile-applied";
    public const string ApplyError = "apply-error";
    public const string Recovery = "recovery";
    public const string TestModeStarted = "test-mode-started";
    public const string TestModeEnded = "test-mode-ended";
    public const string Tamper = "tamper";
    public const string ConfigCreated = "config-created";
    public const string Rejected = "program-rejected";
    public const string Updated = "updated";
    public const string RulesUpdated = "rules-updated";
    public const string ServiceStarted = "service-started";
    public const string ServiceStopped = "service-stopped";
    public const string Refused = "request-refused";
}

/// <summary>Append-only JSON-lines log (one JSON object per line).</summary>
public sealed class JsonlLog
{
    private static readonly object Gate = new();
    public string FilePath { get; }

    public JsonlLog(string filePath) => FilePath = filePath;

    public void Append(string type, object? data = null, string? actor = null)
    {
        var obj = new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
            ["type"] = type,
        };
        if (actor != null) obj["actor"] = actor;
        if (data != null) obj["data"] = JsonSerializer.SerializeToNode(data, data.GetType(), Config.NetGuardConfig.JsonOptions);
        var line = obj.ToJsonString() + "\n";
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
        }
    }

    public IEnumerable<JsonObject> ReadAll()
    {
        if (!File.Exists(FilePath)) yield break;
        using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? o = null;
            try { o = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { }
            if (o != null) yield return o;
        }
    }
}
