# NetGuard

A self-control internet lock for a Windows gaming PC. It blocks all internet except the programs
the active profile allows. Daily use is simple, and undoing it in a weak moment is deliberately hard.

> **Status: milestone 1 of 5 – core engine + admin console (`ngctl`).**
> No service, tray app, cooldown or installer yet. Test steps: [docs/TESTING-M1.md](docs/TESTING-M1.md).
> Emergency: [docs/RECOVERY.md](docs/RECOVERY.md).

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
src/NetGuard.Cli/        ngctl.exe – admin console
tests/NetGuard.Core.Tests/
docs/                    TESTING-M1.md, RECOVERY.md
.github/workflows/       Windows build: tests + ngctl.exe artifact
```

Coming next: `NetGuard.Service` (milestone 2), `NetGuard.Tray` (3), cooldown/commitments (4),
`installer/` (5).

## Building

The GitHub Actions workflow builds everything on Windows and publishes `ngctl.exe` as an artifact.
Locally (any OS with the .NET 8 SDK):

```bash
dotnet test tests/NetGuard.Core.Tests
```

```bash
dotnet publish src/NetGuard.Cli -c Release -o publish/ngctl
```
