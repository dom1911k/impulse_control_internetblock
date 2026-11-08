using NetGuard.Core;
using NetGuard.Core.Config;
using NetGuard.Core.Firewall;
using NetGuard.Core.Paths;

namespace NetGuard.Core.Tests;

/// <summary>A fake "PC" in a temp folder: fake exe files, fake hosts file, fake firewall.</summary>
public sealed class TestEnv : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "netguard-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public InMemoryFirewall Firewall { get; } = new();
    public FakeVerifier Verifier { get; } = new();
    public NetGuardPaths Paths { get; }
    public string HostsPath { get; }

    public TestEnv()
    {
        Directory.CreateDirectory(Root);
        Paths = new NetGuardPaths(Path.Combine(Root, "ProgramData", "NetGuard"));
        HostsPath = Path.Combine(Root, "hosts");
        File.WriteAllText(HostsPath, "# Copyright (c) Microsoft Corp.\r\n127.0.0.1 localhost\r\n");
    }

    public string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    public string Touch(string signer, params string[] parts)
    {
        var p = P(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "fake exe " + p);
        if (signer.Length > 0) Verifier.Signers[p] = signer;
        return p;
    }

    /// <summary>A typical PC: WoW retail+classic, Battle.net with versioned folder, Agent, Spotify, Steam.</summary>
    public NetGuardConfig CreateStandardPc()
    {
        Touch("CN=Blizzard Entertainment, Inc.", "Games", "World of Warcraft", "_retail_", "Wow.exe");
        Touch("CN=Blizzard Entertainment, Inc.", "Games", "World of Warcraft", "_classic_", "WowClassic.exe");
        Touch("CN=Blizzard Entertainment, Inc.", "Battle.net", "Battle.net.exe");
        Touch("CN=Blizzard Entertainment, Inc.", "Battle.net", "Battle.net.15000", "Battle.net.exe");
        Touch("CN=Blizzard Entertainment, Inc.", "Agent", "Agent.9000", "Agent.exe");
        Touch("CN=Spotify AB", "Users", "dom", "AppData", "Roaming", "Spotify", "Spotify.exe");
        Touch("", "Steam", "steam.exe");
        Touch("", "Steam", "bin", "cef", "cef.win64", "steamwebhelper.exe");

        var cfg = DefaultConfig.Create(new DetectedPaths
        {
            WowRoots = { P("Games", "World of Warcraft") },
            BattleNetDir = P("Battle.net"),
            BattleNetAgentDir = P("Agent"),
            UsersDir = P("Users"),
            SteamRoots = { P("Steam") },
        });
        // Default config contains Windows-style paths with backslashes; convert for Linux test runs.
        foreach (var e in cfg.Profiles.SelectMany(p => p.Programs).Concat(cfg.AlwaysBlocked))
            e.Paths = e.Paths.Where(x => !x.Contains('%')).Select(x => x.Replace('\\', Path.DirectorySeparatorChar)).ToList();
        foreach (var s in cfg.BaseServiceRules) s.Program = "/windows/system32/svchost.exe";
        cfg.Save(Paths.ConfigFile);
        return cfg;
    }

    public Engine CreateEngine() => new(Firewall, Verifier, Paths, HostsPath, s => s);

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

public sealed class FakeVerifier : ISignatureVerifier
{
    public Dictionary<string, string> Signers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SignatureInfo Check(string path) =>
        Signers.TryGetValue(path, out var s) ? new(true, s, null) : new(false, null, "unsigned");
}
