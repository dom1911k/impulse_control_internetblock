using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using NetGuard.Core;
using NetGuard.Core.Config;
using NetGuard.Core.Hosts;
using NetGuard.Core.Logging;
using NetGuard.Core.Planning;
using NetGuard.Core.State;
using NetGuard.Core.Updates;
using NetGuard.Core.Ipc;
using NetGuard.Windows;

namespace NetGuard.Cli;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const string Help = """
NetGuard admin console (ngctl)

Usage (from an ADMINISTRATOR terminal):
  ngctl detect                         Find WoW, Battle.net, Agent, Spotify, Steam on this PC
  ngctl init [--force]                 Create C:\ProgramData\NetGuard\config.json from what was detected
  ngctl programs [<profile>]           Show which exact files each profile allows/rejects/blocks
  ngctl status                         Show firewall state, NetGuard rules and drift
  ngctl apply <profile> --dry-run      Print exactly what would change. Changes nothing.
  ngctl apply <profile> --test-mode [--minutes N] [--yes]
                                       Apply for N minutes (default 3, max 15), then automatically
                                       restore normal internet (dead-man switch).
  ngctl apply <profile> --permanent    Apply with NO automatic revert (only use from milestone 2 on)
  ngctl recover --code <recovery code>
                                       EMERGENCY: normal internet again (asks the running service).
                                       If the service isn't running, no code is needed: NetGuard's
                                       rules are removed and default outbound set back to Allow directly.
  ngctl service status|on|off          Show / start (and enable) / stop (and disable) the service
  ngctl diag audit on|off              Turn Windows' logging of blocked connections on/off
  ngctl diag blocked [--minutes N]     List programs whose connections were blocked (default 10 min)
  ngctl log [--tail N]                 Show the last N log entries (default 20)
  ngctl version                        Show this program's version
  ngctl update [--check]               Download the newest NetGuardSetup.exe and run it
                                       (--check: only look). Needs normal internet or Maintenance.

Profiles in the default config: Offline, WoW, Maintenance
""";

    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (UnauthorizedAccessException)
        {
            Err("Access denied: NetGuard protects its files and service, even from administrators.");
            Console.WriteLine("Use the NetGuard window instead (emergency restore: Settings → Emergency restore, with your recovery code).");
            return 1;
        }
        catch (Exception e)
        {
            Err("Error: " + e.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
        {
            Console.WriteLine($"ngctl {GitHubReleases.CurrentVersion}");
            Console.WriteLine(Help);
            return 0;
        }
        if (args[0] is "version" or "--version")
        {
            Console.WriteLine($"ngctl {GitHubReleases.CurrentVersion}");
            return 0;
        }
        if (OperatingSystem.IsWindows()) Updater.CleanupOldFiles();
        if (!OperatingSystem.IsWindows()) { Err("ngctl only runs on Windows."); return 1; }
        if (args[0].Equals("recover", StringComparison.OrdinalIgnoreCase) && args.Any(x => x.Equals("--pause", StringComparison.OrdinalIgnoreCase)))
            return EmergencyWindow(args);
        if (!WinEnv.IsElevatedAdmin())
        {
            Err("ngctl must run in an ADMINISTRATOR terminal.");
            Console.WriteLine("Right-click 'Terminal' (or 'Command Prompt') → 'Run as administrator', then run the command again.");
            return 2;
        }

        var paths = new NetGuardPaths();
        var engine = new Engine(new ComFirewall(), new AuthenticodeVerifier(), paths, HostsFile.DefaultPath);
        var a = new Args(args);

        return a.Command switch
        {
            "detect" => Detect(),
            "init" => Init(engine, a),
            "programs" => Programs(engine, a),
            "status" => Status(engine),
            "apply" => Apply(engine, a),
            "recover" => Recover(engine, a),
            "diag" => Diag(a),
            "log" => ShowLog(engine, a),
            "update" => Update(engine, a),
            "service" => Service(a),
            _ => Unknown(a.Command),
        };
    }

    private static int Unknown(string cmd)
    {
        Err($"Unknown command '{cmd}'.");
        Console.WriteLine(Help);
        return 1;
    }

    // ------------------------------------------------------------------ detect / init

    private static int Detect()
    {
        var d = AppDetector.Detect();
        PrintDetected(d);
        return 0;
    }

    private static void PrintDetected(DetectedPaths d)
    {
        Head("Detected on this PC");
        Line("World of Warcraft", d.WowRoots.Count > 0 ? string.Join("\n                     ", d.WowRoots) : "(not found)");
        Line("Battle.net app", d.BattleNetDir ?? "(not found)");
        Line("Blizzard Agent", d.BattleNetAgentDir ?? "(not found)");
        Line("Spotify (desktop)", d.SpotifyExes.Count > 0 ? string.Join("\n                     ", d.SpotifyExes) : "(not found)");
        Line("Spotify (Store)", d.SpotifyStoreInstalls.Count > 0 ? string.Join("\n                     ", d.SpotifyStoreInstalls) : "(none – good)");
        Line("Steam (blocked)", d.SteamRoots.Count > 0 ? string.Join("\n                     ", d.SteamRoots) : "(not found)");
        foreach (var w in d.Warnings) Warn(w);
    }

    private static int Init(Engine engine, Args a)
    {
        var paths = engine.Paths;
        if (File.Exists(paths.ConfigFile) && !a.Has("--force"))
        {
            Err($"{paths.ConfigFile} already exists. Use 'ngctl init --force' to overwrite it.");
            return 1;
        }
        var d = AppDetector.Detect();
        PrintDetected(d);
        var cfg = DefaultConfig.Create(d);
        DataDirSecurity.Secure(paths);
        cfg.Save(paths.ConfigFile);
        engine.Log.Append(LogTypes.ConfigCreated, new { detected = d }, WinEnv.CurrentUser());
        Console.WriteLine();
        Ok($"Config written to {paths.ConfigFile}");
        Console.WriteLine("Folder permissions set: only SYSTEM and Administrators can change NetGuard's files.");
        Console.WriteLine();
        Console.WriteLine("Next:  ngctl programs WoW        (check which files will be allowed)");
        Console.WriteLine("       ngctl apply WoW --dry-run");
        return 0;
    }

    private static NetGuardConfig RequireConfig(Engine engine)
    {
        if (!File.Exists(engine.Paths.ConfigFile))
            throw new InvalidOperationException("No config yet. Run 'ngctl init' first.");
        return engine.LoadConfig();
    }

    // ------------------------------------------------------------------ programs / status

    private static int Programs(Engine engine, Args a)
    {
        var cfg = RequireConfig(engine);
        var names = a.Positional(1) is { } p ? new[] { p } : cfg.Profiles.Select(x => x.Name).ToArray();
        foreach (var name in names)
        {
            var desired = engine.BuildDesired(cfg, name);
            PrintDesired(desired);
        }
        return 0;
    }

    private static void PrintDesired(DesiredState d)
    {
        Head($"Profile {d.ProfileName}  ({(d.Kind == ProfileKind.AllowAll ? "everything allowed except always-blocked" : "only listed programs may connect")})");
        if (d.Kind == ProfileKind.Allowlist)
        {
            if (d.AllowedPrograms.Count == 0) Console.WriteLine("  Allowed programs:  none (DNS / time sync only)");
            else
            {
                Console.WriteLine("  Allowed programs:");
                foreach (var p in d.AllowedPrograms) Console.WriteLine($"    + {p.Path}   [{p.Detail}]");
            }
            foreach (var p in d.RejectedPrograms)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    x {p.Path}   [REJECTED: {p.Detail}]");
                Console.ResetColor();
            }
        }
        Console.WriteLine($"  Always blocked:    {d.BlockedPrograms.Count} Steam program files" +
                          (d.BlockedPrograms.Count > 0 ? $" (e.g. {Path.GetFileName(d.BlockedPrograms[0].Path)})" : ""));
        Console.WriteLine($"  Hosts file:        {d.BlockedHosts.Count} Steam web addresses blocked");
        foreach (var w in d.Warnings.Where(w => !w.Contains("NOT allowed"))) Warn(w);
    }

    private static int Status(Engine engine)
    {
        var state = engine.LoadState();
        var fw = new ComFirewall().GetSnapshot();

        Head("NetGuard status");
        Line("ngctl version", GitHubReleases.CurrentVersion.ToString());
        Line("Service", ServiceControl.Describe());
        Line("Active profile", state.ActiveProfile ?? "none (normal internet)");
        if (state.ActiveSinceUtc is { } since) Line("Active since", since.ToLocalTime().ToString("g"));
        if (state.TestMode is { } tm) Line("Test mode", $"auto-restore at {tm.DeadlineUtc.ToLocalTime():T}");
        Line("Dead-man task", DeadManSwitch.IsArmed() ? "armed" : "not present");
        Line("Disabled rules", $"{state.DisabledForeignRules.Count} non-NetGuard allow rules switched off by NetGuard");

        Head("Windows Firewall");
        foreach (var (p, s) in fw.Profiles.OrderBy(k => k.Key))
            Line(p.ToString(), $"firewall {(s.Enabled ? "ON" : "OFF")}, default outbound {s.DefaultOutbound.ToString().ToUpperInvariant()}");
        var ng = fw.NetGuardRules.ToList();
        Line("NetGuard rules", ng.Count.ToString());
        foreach (var r in ng.OrderBy(r => r.Name))
            Console.WriteLine($"    {(r.Enabled ? " " : "!")} {r.Action.ToString().ToUpperInvariant(),-5}  {r.Name}");

        if (state.ActiveProfile != null && File.Exists(engine.Paths.ConfigFile))
        {
            var cfg = engine.LoadConfig();
            var plan = engine.PlanApply(cfg, engine.BuildDesired(cfg, state.ActiveProfile));
            Console.WriteLine();
            if (plan.IsEmpty) Ok("In sync: the firewall matches the active profile.");
            else
            {
                Warn($"{plan.Actions.Count} difference(s) from the active profile (drift):");
                foreach (var act in plan.Actions) Console.WriteLine("    - " + act.Describe());
            }
        }
        return 0;
    }

    // ------------------------------------------------------------------ apply

    private static int Apply(Engine engine, Args a)
    {
        var profile = a.Positional(1) ?? throw new ArgumentException("Which profile? e.g. 'ngctl apply WoW --dry-run'");
        var modes = new[] { "--dry-run", "--test-mode", "--permanent" }.Where(a.Has).ToList();
        if (modes.Count != 1)
        {
            Err("Choose exactly one of --dry-run, --test-mode or --permanent.");
            return 1;
        }

        var cfg = RequireConfig(engine);
        var desired = engine.BuildDesired(cfg, profile);
        var plan = engine.PlanApply(cfg, desired);

        PrintDesired(desired);
        PrintPlan(plan);

        if (a.Has("--dry-run"))
        {
            Console.WriteLine();
            Ok("DRY RUN – nothing was changed.");
            return 0;
        }

        var actor = WinEnv.CurrentUser();

        if (a.Has("--permanent"))
        {
            Console.WriteLine();
            Warn("PERMANENT mode: there is NO automatic revert. To undo: 'ngctl recover' (admin terminal).");
            if (!a.Has("--yes") && !Confirm("Type YES to apply")) return 1;
            DeadManSwitch.Disarm();
            var (_, _, res) = engine.ApplyProfile(cfg, profile, actor, null);
            PrintResult(res);
            return res.Success ? 0 : 1;
        }

        // ---- test mode
        var minutes = a.Int("--minutes") ?? cfg.Timings.TestModeMinutes;
        if (minutes < 1 || minutes > TestMode.MaxMinutes) { Err($"--minutes must be between 1 and {TestMode.MaxMinutes}."); return 1; }

        var deadline = DateTimeOffset.UtcNow.AddMinutes(minutes);
        Console.WriteLine();
        Console.WriteLine($"TEST MODE: profile '{desired.ProfileName}' for {minutes} minute(s).");
        Console.WriteLine($"Normal internet comes back automatically at {deadline.ToLocalTime():T}.");
        Console.WriteLine("Restore early: press R or Ctrl+C in this window. If this window is closed, a SYSTEM scheduled task");
        Console.WriteLine("restores internet ~30 seconds after the deadline (or at the next boot).");
        if (!a.Has("--yes") && !Confirm("Type YES to start")) return 1;

        var (info, result) = TestMode.Start(engine, cfg, profile, minutes, actor);
        Ok("Dead-man switch armed (scheduled task \\NetGuard\\DeadManSwitch).");
        PrintResult(result);

        var endedEarly = Countdown(info.DeadlineUtc);
        Console.WriteLine();
        PrintResult(TestMode.End(engine, actor, endedEarly));
        Ok("Normal internet restored.");
        return 0;
    }

    /// <summary>Wait until the deadline. Returns true if the user ended it early.</summary>
    private static bool Countdown(DateTimeOffset deadline)
    {
        var early = false;
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; early = true; };
        Console.CancelKeyPress += handler;
        try
        {
            while (!early && DateTimeOffset.UtcNow < deadline)
            {
                var left = deadline - DateTimeOffset.UtcNow;
                Console.Write($"\r  Locked. Internet comes back in {left:mm\\:ss}   (press R to restore now)   ");
                if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.R) early = true;
                Thread.Sleep(250);
            }
        }
        finally { Console.CancelKeyPress -= handler; }
        return early;
    }

    // ------------------------------------------------------------------ recover

    /// <summary>
    /// The Start menu's "NetGuard Emergency Restore": asks the running service, with the recovery code
    /// (no admin rights needed). If the service is broken, it asks Windows for admin rights and restores directly.
    /// The window stays open so the result can be read.
    /// </summary>
    private static int EmergencyWindow(string[] args)
    {
        int Done(int rc)
        {
            Console.WriteLine();
            Console.Write("Press Enter to close this window.");
            Console.ReadLine();
            return rc;
        }

        Console.Title = "NetGuard – Emergency restore";
        Head("NetGuard – Emergency restore");
        Console.WriteLine("This ends any NetGuard lock right now and brings back normal internet. It is logged.");
        Console.WriteLine();

        if (ServiceControl.IsRunning())
        {
            Console.Write("Recovery code (or just Enter to cancel): ");
            var code = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(code)) { Console.WriteLine("Cancelled – nothing was changed."); return Done(0); }
            try
            {
                var r = new GuardClient().SendAsync(new NetGuard.Core.Ipc.IpcRequest { Command = NetGuard.Core.Ipc.IpcCommands.Recover, Code = code })
                    .GetAwaiter().GetResult();
                if (!r.Ok) { Err(r.Error ?? "Refused."); return Done(1); }
                Console.WriteLine(r.Text);
                Ok("Normal internet restored.");
                return Done(0);
            }
            catch (Exception e)
            {
                Err("The service didn't answer: " + e.Message);
                Console.WriteLine("If this keeps happening, see RECOVERY.md (Start menu → NetGuard - manual recovery steps).");
                return Done(1);
            }
        }

        Warn("The NetGuard service isn't running, so no code is needed – but changing the firewall needs administrator rights.");
        if (!WinEnv.IsElevatedAdmin())
        {
            Console.Write("Press Enter to ask Windows for administrator rights (or close this window): ");
            Console.ReadLine();
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "recover --pause") { UseShellExecute = true, Verb = "runas" });
                return 0;
            }
            catch (Exception e) { Err("Not started: " + e.Message); return Done(1); }
        }
        var engine = new Engine(new ComFirewall(), new AuthenticodeVerifier(), new NetGuardPaths(), HostsFile.DefaultPath);
        var rc = Recover(engine, new Args(new[] { "recover", "--reason", "emergency restore (service not running)" }));
        return Done(rc);
    }

    private static int Recover(Engine engine, Args a)
    {
        var fromTask = a.Has("--from-task");
        var quiet = fromTask || a.Has("--quiet");

        // While the service runs it owns everything (and protects its files from administrators):
        // ask it, with the recovery code. The dead-man task runs as SYSTEM and may act directly.
        if (!fromTask && ServiceControl.IsRunning())
        {
            var code = a.Value("--code");
            if (code == null && !Console.IsInputRedirected && !quiet)
            {
                Console.Write("The NetGuard service is running. Recovery code: ");
                code = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(code)) code = null;
            }
            if (code == null)
            {
                Err("The NetGuard service is running. Emergency restore needs your recovery code:");
                Console.WriteLine("  ngctl recover --code XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX");
                Console.WriteLine("(Or in the NetGuard window: Settings → Emergency restore.)");
                return 1;
            }
            var r = new GuardClient().SendAsync(new NetGuard.Core.Ipc.IpcRequest { Command = NetGuard.Core.Ipc.IpcCommands.Recover, Code = code })
                .GetAwaiter().GetResult();
            if (!r.Ok) { Err(r.Error ?? "Refused."); return 1; }
            if (!quiet) { Console.WriteLine(r.Text); Ok("Normal internet restored."); }
            return 0;
        }

        // Service not running (broken, stopped, or being uninstalled): restore directly.
        var state = engine.LoadState();
        var plan = engine.PlanRecovery(state);
        if (!quiet)
        {
            Head("Recovery – restore normal internet");
            PrintPlan(plan);
        }
        if (a.Has("--dry-run"))
        {
            Ok("DRY RUN – nothing was changed.");
            return 0;
        }

        var reason = a.Value("--reason") ?? (fromTask ? "dead-man switch" : "manual recovery via ngctl");
        ExecutionResult result;
        try
        {
            (_, result) = engine.Recover(reason, fromTask ? "SYSTEM (dead-man task)" : WinEnv.CurrentUser());
        }
        catch (UnauthorizedAccessException)
        {
            // NetGuard's data folder is read-only for administrators (self-protection). The firewall isn't:
            // undo the lock anyway, just without updating NetGuard's own records.
            result = engine.Execute(plan, state);
            if (!quiet) Warn("NetGuard's files are protected, so the history wasn't updated – the firewall was restored anyway.");
        }
        try { DeadManSwitch.Disarm(); } catch { /* best effort */ }
        if (!quiet)
        {
            PrintResult(result);
            if (result.Success) Ok("Normal internet restored.");
        }
        return result.Success ? 0 : 1;
    }

    // ------------------------------------------------------------------ update

    private static int Update(Engine engine, Args a)
    {
        var state = engine.LoadState();
        if (state.TestMode is { } tm && tm.DeadlineUtc > DateTimeOffset.UtcNow)
        {
            Err("A test-mode lock is running. Let it finish (or 'ngctl recover') before updating.");
            return 1;
        }

        var current = GitHubReleases.CurrentVersion;
        Head("Update");
        Line("Installed", current.ToString());
        Console.WriteLine("  Checking " + GitHubReleases.ReleasesPage + " …");
        var latest = Updater.CheckLatestAsync().GetAwaiter().GetResult();
        Line("Newest release", $"{latest.Version}" + (latest.Published is { } p ? $"  ({p.ToLocalTime():g})" : ""));

        if (latest.Version <= current)
        {
            Ok("You already have the newest version.");
            return 0;
        }
        if (!string.IsNullOrWhiteSpace(latest.Notes))
        {
            Console.WriteLine("  What's new:");
            foreach (var l in latest.Notes.Split('\n').Take(15)) Console.WriteLine("    " + l.TrimEnd());
        }
        if (a.Has("--check"))
        {
            Ok($"Version {latest.Version} is available. Run 'ngctl update' to install it.");
            return 0;
        }

        var path = Updater.DownloadInstallerAsync(latest, m => Console.WriteLine("  " + m)).GetAwaiter().GetResult();
        engine.Log.Append(LogTypes.Updated, new { from = current.ToString(), to = latest.Version.ToString(), release = latest.HtmlUrl, installer = path }, WinEnv.CurrentUser());
        Updater.RunInstaller(path, silent: false);
        Ok($"The installer for {latest.Version} has started. Follow it on screen.");
        return 0;
    }

    // ------------------------------------------------------------------ service

    private static int Service(Args a)
    {
        switch (a.Positional(1))
        {
            case null or "status":
                Line("NetGuard service", ServiceControl.Describe());
                return 0;
            case "on":
                ServiceControl.EnableAndStart();
                Ok("NetGuard service is running and starts automatically with Windows.");
                return 0;
            case "off":
                try { ServiceControl.StopAndDisable(); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Err("The service protects itself and can't be stopped. To remove NetGuard: NetGuard window → Settings → Advanced → Uninstall NetGuard.");
                    return 1;
                }
                Warn("NetGuard service stopped and disabled. The firewall rules stay as they are, but nothing repairs them now.");
                return 0;
            default:
                throw new ArgumentException("Use 'ngctl service status', 'on' or 'off'.");
        }
    }

    // ------------------------------------------------------------------ diag / log

    private static int Diag(Args a)
    {
        switch (a.Positional(1))
        {
            case "audit":
                var on = a.Positional(2) switch { "on" => true, "off" => false, _ => throw new ArgumentException("Use 'ngctl diag audit on' or 'off'.") };
                BlockedConnections.SetAuditing(on);
                Ok(on ? "Windows now logs blocked connections. Remember 'ngctl diag audit off' when done." : "Logging of blocked connections switched off.");
                return 0;
            case "blocked":
                var minutes = a.Int("--minutes") ?? 10;
                var apps = BlockedConnections.Query(TimeSpan.FromMinutes(minutes));
                Head($"Programs with blocked outbound connections (last {minutes} min)");
                if (apps.Count == 0)
                    Console.WriteLine("  None found. (Is auditing on? 'ngctl diag audit on', then reproduce the problem.)");
                foreach (var app in apps)
                    Console.WriteLine($"  {app.Count,6}x  {app.Application}\n          last {app.LastSeen:T} → {string.Join(", ", app.Destinations)}");
                return 0;
            default:
                throw new ArgumentException("Use 'ngctl diag audit on|off' or 'ngctl diag blocked'.");
        }
    }

    private static int ShowLog(Engine engine, Args a)
    {
        var n = a.Int("--tail") ?? 20;
        var entries = engine.Log.ReadAll().ToList();
        Head($"Last {Math.Min(n, entries.Count)} of {entries.Count} log entries ({engine.Log.FilePath})");
        foreach (var e in entries.TakeLast(n))
        {
            var ts = DateTimeOffset.TryParse((string?)e["ts"], out var t) ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "?";
            var data = e["data"] is JsonObject d ? Summarize(d) : "";
            Console.WriteLine($"  {ts}  {(string?)e["type"],-18} {(string?)e["actor"]}  {data}");
        }
        return 0;
    }

    private static string Summarize(JsonObject d)
    {
        var parts = new List<string>();
        foreach (var key in new[] { "profile", "reason", "path", "minutes", "endedEarly" })
            if (d[key] is { } v) parts.Add($"{key}={v.ToJsonString().Trim('"')}");
        if (d["changes"] is JsonArray c) parts.Add($"{c.Count} change(s)");
        if (d["errors"] is JsonArray er && er.Count > 0) parts.Add($"{er.Count} ERROR(S)");
        return string.Join("  ", parts);
    }

    // ------------------------------------------------------------------ output helpers

    private static void PrintPlan(Plan plan)
    {
        Console.WriteLine();
        if (plan.IsEmpty) { Console.WriteLine("  Changes: none – firewall already matches."); return; }
        Console.WriteLine($"  Changes ({plan.Actions.Count}):");
        foreach (var act in plan.Actions) Console.WriteLine("    - " + act.Describe());
    }

    private static void PrintResult(ExecutionResult r)
    {
        if (r.Success) Ok($"{r.Done.Count} change(s) applied.");
        else
        {
            Warn($"{r.Done.Count} change(s) applied, {r.Errors.Count} failed:");
            foreach (var e in r.Errors) Err($"    {e.Action}: {e.Error}");
        }
    }

    private static bool Confirm(string prompt)
    {
        Console.Write($"{prompt}: ");
        var ok = string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.Ordinal);
        if (!ok) Console.WriteLine("Cancelled – nothing was changed.");
        return ok;
    }

    private static void Head(string s)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(s);
        Console.ResetColor();
    }

    private static void Line(string label, string value) => Console.WriteLine($"  {label,-18} {value}");

    private static void Ok(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(s); Console.ResetColor(); }
    private static void Warn(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("  ! " + s); Console.ResetColor(); }
    private static void Err(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine(s); Console.ResetColor(); }
}

internal sealed class Args
{
    private readonly string[] _a;
    public Args(string[] a) => _a = a;
    public string Command => _a[0].ToLowerInvariant();
    public bool Has(string flag) => _a.Any(x => string.Equals(x, flag, StringComparison.OrdinalIgnoreCase));

    public string? Value(string opt)
    {
        var i = Array.FindIndex(_a, x => string.Equals(x, opt, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < _a.Length ? _a[i + 1] : null;
    }

    public int? Int(string opt) => Value(opt) is { } v ? (int.TryParse(v, out var n) ? n : throw new ArgumentException($"{opt} needs a number.")) : null;

    /// <summary>Nth non-option argument (0 = command). Skips option values.</summary>
    public string? Positional(int n)
    {
        var list = new List<string>();
        for (var i = 0; i < _a.Length; i++)
        {
            if (_a[i].StartsWith("--"))
            {
                if (_a[i] is "--minutes" or "--reason" or "--tail" or "--code") i++;
                continue;
            }
            list.Add(_a[i]);
        }
        return n < list.Count ? list[n] : null;
    }
}
