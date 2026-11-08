# NetGuard

A self-control internet lock for a Windows gaming PC. It blocks all internet except the programs
the active profile allows. Daily use is simple, and undoing it in a weak moment is deliberately hard.

> **Status: milestone 1 of 5 – core engine, window app (test mode only), console tool, installer.**
> No background service, cooldown or tray icon yet. Test steps: [docs/TESTING-M1.md](docs/TESTING-M1.md).
> Emergency: [docs/RECOVERY.md](docs/RECOVERY.md).
>
> **Download:** [NetGuardSetup.exe](https://github.com/dom1911k/impulse_control_internetblock/releases/latest/download/NetGuardSetup.exe)
> (newest release, no sign-in needed).

## Profiles (default config)

| Profile | Internet for | Notes |
|---|---|---|
| Offline | nothing (DNS, time sync and connectivity check only) | |
| WoW | WoW (retail + Classic), Battle.net, Blizzard Agent, Spotify | |
| Maintenance | everything except Steam | Windows Update etc. (cooldown + auto-revert from milestone 4) |

Steam is blocked in every profile: by program (firewall block rules, which beat allow rules) and by
website (hosts-file entries for the Steam store and community). Steam itself can still *run*, so
Rocksmith works in Steam Offline Mode.

## How it works

* **Windows Defender Firewall does the blocking.** Default outbound = **Block** on all three firewall
  profiles, plus one allow rule per permitted program, named `NetGuard:<profile>:<exe>` in group
  `NetGuard`.
* **Leaks closed.** Windows ships with dozens of enabled outbound *allow* rules (Store apps, Edge
  WebView, …). They would bypass "block by default", so NetGuard **disables** them (never deletes)
  and records each one in `state.json`. Recovery switches exactly those back on. The *Core
  Networking* group (DHCP, IPv6 neighbour discovery) is never touched.
* **Versioned folders.** Battle.net and Blizzard Agent install into folders like
  `Agent.9000\Agent.exe` that change with every update. Profiles use path patterns
  (`*`, `**`), and NetGuard expands them to exact paths each time it applies a profile.
* **Signature pinning.** Every allowed WoW, Battle.net, Agent and Spotify file must carry a valid
  digital signature from *Blizzard Entertainment* / *Spotify AB*. A browser renamed to
  `Spotify.exe` is rejected. Unlike a SHA-256 pin, this keeps working when Spotify updates itself.
  Checks run offline, so they also work while blocked.
* **Test mode.** Every development lock has a dead-man switch. ngctl counts down and restores
  internet. Independently, a one-shot SYSTEM scheduled task runs the PowerShell recovery commands
  30 s after the deadline (and at boot), even if ngctl crashed or was closed.
* **Everything is logged** as JSON lines in `C:\ProgramData\NetGuard\log.jsonl`. That folder is
  writable only by SYSTEM and Administrators; standard users can read the log.

### Technical choices

| Choice | Why |
|---|---|
| C# / .NET 8, self-contained single-file exes | No runtime to install. One stack for service + UI. |
| Firewall COM API (`HNetCfg.FwPolicy2`) via late binding | The service re-checks the firewall every 30 s. COM reads it in milliseconds in-process; PowerShell would cost ~1 s and a runspace each time. No interop DLL needed. |
| PowerShell only in recovery | `RECOVERY.md` and the dead-man task use plain `NetSecurity` cmdlets, so recovery doesn't depend on NetGuard's own code. |
| Authenticode signer instead of SHA-256 by default | Survives app updates; SHA-256 remains available per program (`"pin": "Sha256"`). |
| Platform-independent core | All decisions (planning, diffing, later cooldown/commitments) live in `NetGuard.Core` and are unit-tested against an in-memory firewall. |

## Repository layout

```
src/NetGuard.Core/       config, profile → rule planner, drift diff, hosts file, state, JSONL log (no Windows APIs)
src/NetGuard.Windows/    COM firewall, Authenticode check, app detection, ACLs, dead-man task, blocked-connection diag
src/NetGuard.App/        NetGuard.exe – the window app (WPF): profiles, setup, history, tools, updates
src/NetGuard.Cli/        ngctl.exe – admin console (same functions, for scripts and troubleshooting)
installer/               Inno Setup script → NetGuardSetup.exe (uninstall always restores normal internet)
tests/NetGuard.Core.Tests/
docs/                    TESTING-M1.md, RECOVERY.md
.github/workflows/       Windows build: tests, both exes, installer, GitHub Release
```

Coming next: `NetGuard.Service` (milestone 2), tray icon + first-run wizard (3),
cooldown/commitments (4), finished installer with service + recovery shortcut (5).
From milestone 3 the window app talks to the service instead of changing the firewall itself, so it
will run as your standard user.

## Updating

Every push builds a numbered release (`v0.1.<build>`) on the
[Releases page](https://github.com/dom1911k/impulse_control_internetblock/releases).
In NetGuard: **Tools → Check for updates → Install update** (or `ngctl update` in an admin terminal).

It downloads only from this repository's releases, verifies the SHA-256 checksum published with the
release, swaps the files (the old ones are renamed and cleaned up on the next start), and logs the
update. During a lock, NetGuard itself has no internet, so updating only works with normal internet or
in Maintenance. That's intentional. From milestone 3 the tray icon will show "update available".

## Building

The GitHub Actions workflow builds everything on Windows and publishes `ngctl.exe` as an artifact.
Locally (any OS with the .NET 8 SDK):

```bash
dotnet test tests/NetGuard.Core.Tests
```

```bash
dotnet publish src/NetGuard.Cli -c Release -o publish/ngctl
```
