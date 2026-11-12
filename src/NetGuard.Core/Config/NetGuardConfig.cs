using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetGuard.Core.Config;

public enum ProfileKind
{
    /// <summary>Default outbound = Block; only listed programs (plus base service rules) may connect.</summary>
    Allowlist,
    /// <summary>Default outbound = Allow (Maintenance). Always-blocked programs stay blocked.</summary>
    AllowAll,
}

public enum PinMode
{
    /// <summary>Any file at the path is allowed. Only sensible for admin-only folders.</summary>
    None,
    /// <summary>File must carry a valid Authenticode signature whose subject contains <see cref="ProgramEntry.Signer"/>.</summary>
    Signature,
    /// <summary>File's SHA-256 must equal <see cref="ProgramEntry.Sha256"/> (breaks on every update).</summary>
    Sha256,
}

public sealed class ProgramEntry
{
    /// <summary>Friendly name, e.g. "Spotify".</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Path patterns. Environment variables (%ProgramData%) are expanded.
    /// '*' and '?' work inside a path segment; a segment of '**' matches any number of folders.
    /// </summary>
    public List<string> Paths { get; set; } = new();

    public PinMode Pin { get; set; } = PinMode.None;

    /// <summary>Text the certificate subject must contain, e.g. "Spotify AB".</summary>
    public string? Signer { get; set; }

    public string? Sha256 { get; set; }
}

/// <summary>A rule for a Windows service hosted in svchost (DNS, time sync…). Present in every profile.</summary>
public sealed class ServiceRuleEntry
{
    public string Id { get; set; } = "";          // short id used in the rule name, e.g. "dns-udp"
    public string Program { get; set; } = @"%SystemRoot%\System32\svchost.exe";
    public string Service { get; set; } = "";
    public string Protocol { get; set; } = "UDP"; // TCP | UDP | Any
    public string? RemotePorts { get; set; }
}

public sealed class ProfileDefinition
{
    public string Name { get; set; } = "";
    public ProfileKind Kind { get; set; } = ProfileKind.Allowlist;
    public List<ProgramEntry> Programs { get; set; } = new();

    /// <summary>Optional play-time plan: when this profile may be active, and for how long.</summary>
    public PlayPlan? Plan { get; set; }
}

/// <summary>A weekly timetable plus daily/weekly limits for a game profile.</summary>
public sealed class PlayPlan
{
    public List<PlayWindow> Windows { get; set; } = new();
    public int? DailyLimitMinutes { get; set; }
    public int? WeeklyLimitMinutes { get; set; }
    /// <summary>Program entries of the profile that count as "the game": blocked everywhere outside play time.</summary>
    public List<string> GamePrograms { get; set; } = new();
    /// <summary>Where NetGuard switches when play time is over.</summary>
    public string FallbackProfile { get; set; } = "Offline";
}

public sealed class PlayWindow
{
    public DayOfWeek Day { get; set; }
    public string Start { get; set; } = "19:00";
    /// <summary>"24:00" means midnight at the end of the day.</summary>
    public string End { get; set; } = "23:00";

    public int StartMinute => ParseTime(Start);
    public int EndMinute => ParseTime(End);

    public static int ParseTime(string s)
    {
        var parts = s.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) || h < 0 || h > 24 || m < 0 || m > 59 || (h == 24 && m != 0))
            throw new InvalidDataException($"'{s}' is not a time like 19:30.");
        return h * 60 + m;
    }
}

/// <summary>
/// "Bass check": a few minutes, a few times a day, on top of the active profile. Extra programs get internet,
/// and Chrome is limited to <see cref="Websites"/> by Chrome's own website policy.
/// </summary>
public sealed class BrowseWindowConfig
{
    public string Name { get; set; } = "Bass check";
    public int Minutes { get; set; } = 20;
    public int PerDay { get; set; } = 2;
    public List<ProgramEntry> Programs { get; set; } = new();
    /// <summary>Chrome URL-allowlist entries (e.g. "songsterr.com" covers all its subdomains).</summary>
    public List<string> Websites { get; set; } = new();
}

public sealed class TimingSettings
{
    public int TestModeMinutes { get; set; } = 3;
    public int HealIntervalSeconds { get; set; } = 30;
    public int CooldownMinutes { get; set; } = 45;
    public int ConfirmWindowMinutes { get; set; } = 10;
    public int MaxUnlockRequestsPerDay { get; set; } = 2;
    public int MinReasonLength { get; set; } = 20;
    public int ConfirmPassageLength { get; set; } = 400;
    public int MaintenanceMaxMinutes { get; set; } = 45;
    public int MaintenanceReminderDays { get; set; } = 14;
    public int GraceMinutes { get; set; } = 10;
    /// <summary>Looser play-plan changes take effect only after this many hours.</summary>
    public int PlanLoosenDelayHours { get; set; } = 72;
    public int MaxCommitmentDays { get; set; } = 30;

    /// <summary>Night guard: from this hour to <see cref="NightGuardEndHour"/> only Offline (or stricter).</summary>
    public int NightGuardStartHour { get; set; } = 1;
    public int NightGuardEndHour { get; set; } = 10;
    /// <summary>Each real tampering attempt during a lock extends the commitment by this much.</summary>
    public int TamperPenaltyMinutes { get; set; } = 120;
    public int MaxPenaltyMinutesPerDay { get; set; } = 720;
    /// <summary>Bass practice (Rocksmith running) adds play time 1:1, up to this many minutes a day.</summary>
    public int PracticeBonusCapMinutes { get; set; } = 60;
    public int UpdateReminderDays { get; set; } = 30;
    public int UpdateWindowMaxMinutes { get; set; } = 180;
}

public sealed class NetGuardConfig
{
    public int SchemaVersion { get; set; } = 1;

    public List<ProfileDefinition> Profiles { get; set; } = new();

    /// <summary>Allow rules for system services, added to every profile (DNS, NTP, connectivity check).</summary>
    public List<ServiceRuleEntry> BaseServiceRules { get; set; } = new();

    /// <summary>Programs blocked in every profile, even Maintenance (Steam).</summary>
    public List<ProgramEntry> AlwaysBlocked { get; set; } = new();

    /// <summary>Host names sent to 0.0.0.0 via the hosts file in every profile.</summary>
    public List<string> AlwaysBlockedHosts { get; set; } = new();

    /// <summary>
    /// Firewall rule groups NetGuard never disables (Core Networking: DHCP, IPv6 neighbour discovery…).
    /// Matched against the rule's Grouping (resource string or display name).
    /// </summary>
    public List<string> ProtectedRuleGroups { get; set; } = new();

    public TimingSettings Timings { get; set; } = new();

    /// <summary>A short window ("Bass check") with a few extra programs and a website allowlist for Chrome.</summary>
    public BrowseWindowConfig? BrowseWindow { get; set; }

    public ProfileDefinition? FindProfile(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static NetGuardConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<NetGuardConfig>(json, JsonOptions)
                  ?? throw new InvalidDataException("Config file is empty.");
        cfg.Validate();
        return cfg;
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public void Validate()
    {
        if (Profiles.Count == 0) throw new InvalidDataException("Config has no profiles.");
        var dup = Profiles.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidDataException($"Profile '{dup.Key}' is defined twice.");
        foreach (var p in Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Contains(':'))
                throw new InvalidDataException($"Invalid profile name '{p.Name}'.");
            foreach (var prog in p.Programs)
            {
                if (prog.Pin == PinMode.Signature && string.IsNullOrWhiteSpace(prog.Signer))
                    throw new InvalidDataException($"Program '{prog.Name}' uses signature pinning but has no signer.");
                if (prog.Pin == PinMode.Sha256 && string.IsNullOrWhiteSpace(prog.Sha256))
                    throw new InvalidDataException($"Program '{prog.Name}' uses SHA-256 pinning but has no hash.");
            }
        }
        foreach (var r in BaseServiceRules)
            if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Service))
                throw new InvalidDataException("Base service rules need an id and a service name.");
        foreach (var p in Profiles.Where(p => p.Plan != null)) ValidatePlan(p);
    }

    public void ValidatePlan(ProfileDefinition p)
    {
        var plan = p.Plan!;
        if (p.Kind != ProfileKind.Allowlist) throw new InvalidDataException("Only locking profiles can have a play plan.");
        foreach (var w in plan.Windows)
            if (w.EndMinute <= w.StartMinute) throw new InvalidDataException($"{w.Day} {w.Start}–{w.End}: the end must be after the start.");
        if (plan.DailyLimitMinutes is < 0 or > 1440) throw new InvalidDataException("The daily limit must be between 0 and 24 hours.");
        if (plan.WeeklyLimitMinutes is < 0 or > 10080) throw new InvalidDataException("The weekly limit must be between 0 and 168 hours.");
        var fb = FindProfile(plan.FallbackProfile) ?? throw new InvalidDataException($"Fallback profile '{plan.FallbackProfile}' doesn't exist.");
        if (fb.Plan != null || fb.Kind != ProfileKind.Allowlist || string.Equals(fb.Name, p.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The fallback must be a locking profile without its own play plan (usually Offline).");
        foreach (var g in plan.GamePrograms)
            if (!p.Programs.Any(e => string.Equals(e.Name, g, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"'{g}' is not one of {p.Name}'s programs.");
    }
}

public static class AtomicFile
{
    /// <summary>Write to a temp file then move over the target, so a crash never leaves half a file.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp");
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
