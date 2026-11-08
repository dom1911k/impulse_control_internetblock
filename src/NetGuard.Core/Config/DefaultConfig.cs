namespace NetGuard.Core.Config;

/// <summary>What the detector found on this PC. Used to build the default config.</summary>
public sealed class DetectedPaths
{
    public List<string> WowRoots { get; set; } = new();          // e.g. C:\Program Files (x86)\World of Warcraft
    public string? BattleNetDir { get; set; }                     // e.g. C:\Program Files (x86)\Battle.net
    public string? BattleNetAgentDir { get; set; }                // e.g. C:\ProgramData\Battle.net\Agent
    public string? UsersDir { get; set; }                         // e.g. C:\Users
    public List<string> SpotifyExes { get; set; } = new();        // desktop Spotify.exe files found
    public List<string> SpotifyStoreInstalls { get; set; } = new(); // Microsoft Store packages found (warn!)
    public List<string> SteamRoots { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public static class DefaultConfig
{
    public const string Offline = "Offline";
    public const string WoW = "WoW";
    public const string Maintenance = "Maintenance";

    public const string BlizzardSigner = "Blizzard Entertainment";
    public const string SpotifySigner = "Spotify AB";

    public static NetGuardConfig Create(DetectedPaths d)
    {
        var wowPaths = new List<string>();
        foreach (var root in d.WowRoots.DefaultIfEmpty(@"%ProgramFiles(x86)%\World of Warcraft"))
        {
            // _retail_\Wow.exe, _classic_\WowClassic.exe, _classic_era_\WowClassic.exe, voice proxy, PTRs…
            wowPaths.Add(Path.Join(root, "_*_", "Wow*.exe").Replace('/', '\\'));
        }

        var bnet = d.BattleNetDir ?? @"%ProgramFiles(x86)%\Battle.net";
        var agent = d.BattleNetAgentDir ?? @"%ProgramData%\Battle.net\Agent";
        var users = d.UsersDir ?? @"%SystemDrive%\Users";

        var steamPaths = new List<string>();
        foreach (var s in d.SteamRoots.DefaultIfEmpty(@"%ProgramFiles(x86)%\Steam"))
        {
            steamPaths.Add(s + @"\*.exe");          // steam.exe, steamerrorreporter.exe …
            steamPaths.Add(s + @"\bin\**\*.exe");   // steamwebhelper.exe, steamservice.exe …
        }
        steamPaths.Add(@"%ProgramFiles(x86)%\Common Files\Steam\*.exe");

        return new NetGuardConfig
        {
            Profiles =
            {
                new ProfileDefinition { Name = Offline, Kind = ProfileKind.Allowlist },
                new ProfileDefinition
                {
                    Name = WoW,
                    Kind = ProfileKind.Allowlist,
                    Programs =
                    {
                        new ProgramEntry { Name = "World of Warcraft", Paths = wowPaths, Pin = PinMode.Signature, Signer = BlizzardSigner },
                        // Battle.net keeps its real exe in versioned sub-folders (Battle.net.12345\...), so scan recursively.
                        new ProgramEntry { Name = "Battle.net", Paths = { bnet + @"\**\*.exe" }, Pin = PinMode.Signature, Signer = BlizzardSigner },
                        // Agent lives in Agent\Agent.<version>\Agent.exe; the folder name changes with each update.
                        new ProgramEntry { Name = "Blizzard Agent", Paths = { agent + @"\**\*.exe" }, Pin = PinMode.Signature, Signer = BlizzardSigner },
                        new ProgramEntry { Name = "Spotify", Paths = { users + @"\*\AppData\Roaming\Spotify\Spotify.exe" }, Pin = PinMode.Signature, Signer = SpotifySigner },
                    },
                },
                new ProfileDefinition { Name = Maintenance, Kind = ProfileKind.AllowAll },
            },
            BaseServiceRules =
            {
                new ServiceRuleEntry { Id = "dns-udp", Service = "Dnscache", Protocol = "UDP", RemotePorts = "53" },
                new ServiceRuleEntry { Id = "dns-tcp", Service = "Dnscache", Protocol = "TCP", RemotePorts = "53" },
                new ServiceRuleEntry { Id = "time-sync", Service = "W32Time", Protocol = "UDP", RemotePorts = "123" },
                // Network Connectivity Status Indicator: stops Windows (and some apps) believing there is "No internet".
                new ServiceRuleEntry { Id = "connectivity-check", Service = "NlaSvc", Protocol = "TCP", RemotePorts = "80" },
            },
            AlwaysBlocked =
            {
                new ProgramEntry { Name = "Steam", Paths = steamPaths },
            },
            AlwaysBlockedHosts =
            {
                "steampowered.com", "www.steampowered.com", "store.steampowered.com", "help.steampowered.com",
                "login.steampowered.com", "checkout.steampowered.com", "api.steampowered.com",
                "steamcommunity.com", "www.steamcommunity.com", "s.team", "steam.tv", "www.steam.tv",
            },
            ProtectedRuleGroups = { "@FirewallAPI.dll,-25000", "Core Networking" },
        };
    }
}
