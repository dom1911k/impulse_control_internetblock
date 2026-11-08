namespace NetGuard.Core;

/// <summary>Where NetGuard keeps its files (C:\ProgramData\NetGuard on Windows).</summary>
public sealed class NetGuardPaths
{
    public string DataDir { get; }
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string StateFile => Path.Combine(DataDir, "state.json");
    public string LogFile => Path.Combine(DataDir, "log.jsonl");

    public NetGuardPaths(string? dataDir = null) =>
        DataDir = dataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetGuard");
}
