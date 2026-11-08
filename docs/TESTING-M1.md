# Milestone 1 – test sheet

What you're testing: the core engine. It turns a profile into Windows Firewall rules, prints exactly
what it would do (`--dry-run`), and in **test mode** restores normal internet by itself.
There is no service, tray app or cooldown yet. That's milestones 2–4.

Expect about 30–40 minutes. Keep this page open on your phone, because during the test your PC
only has internet for WoW, Battle.net and Spotify.

---

## 0. Get ngctl.exe

1. Open https://github.com/dom1911k/impulse_control_internetblock/releases (no sign-in needed) and,
   under the newest release, download **NetGuard-ngctl-win-x64.zip**.
2. Create the folder `C:\NetGuard-test` and unzip the download into it.
   Use exactly this folder (not Downloads): in test mode a SYSTEM task runs ngctl from here.
3. Open **Terminal as administrator** (Start → type `Terminal` → right-click → *Run as administrator*) and run:

   ```powershell
   cd C:\NetGuard-test
   ```

If Windows says the file is blocked: right-click `ngctl.exe` → Properties → tick **Unblock** → OK.

**Later updates:** with normal internet (no lock active), run `.\ngctl update`. It checks the
Releases page, shows what's new, verifies the download's checksum, and replaces itself.
`.\ngctl update --check` only looks. `.\ngctl version` shows what you have.

## 1. Detect your apps

```powershell
.\ngctl detect
```

✅ Check: it finds your WoW folder, Battle.net, the Blizzard Agent folder, `Spotify.exe` under
`C:\Users\<you>\AppData\Roaming\Spotify`, and Steam. If Spotify (Store) shows anything, tell me.

## 2. Create the config

```powershell
.\ngctl init
```

✅ Check: "Config written to C:\ProgramData\NetGuard\config.json".

## 3. See which exact files will be allowed

```powershell
.\ngctl programs WoW
```

✅ Check: you see `+` lines for `Wow.exe` (and `WowClassic.exe` if installed), several Battle.net
files, `Agent.exe` and `Spotify.exe`, each `[signed by …]`.
❗ Any red `x … REJECTED` line for a Blizzard or Spotify file: send me that line.

## 4. Dry run (changes nothing)

```powershell
.\ngctl apply WoW --dry-run
```

✅ Check: it ends with **DRY RUN – nothing was changed.** The change list should show:
- default OUTBOUND → BLOCK for Domain, Private, Public
- `Add rule NetGuard:WoW:…` for each allowed program, plus `dns-udp`, `dns-tcp`, `time-sync`, `connectivity-check`
- `Add rule NetGuard:Always:steam.exe` etc. as **BLOCK**
- `Disable non-NetGuard allow rule …` for a few dozen built-in Windows rules (expected: these would leak otherwise)
- the hosts-file line for the Steam websites

Then:

```powershell
.\ngctl status
```

✅ Check: "Active profile: none (normal internet)", "NetGuard rules 0", default outbound ALLOW.

## 5. Optional: record blocked connections

This makes step 7 much easier to troubleshoot:

```powershell
.\ngctl diag audit on
```

## 6. Test A – 3-minute automatic revert

Close Battle.net, WoW, Spotify, Steam, Discord and browsers first. Then:

```powershell
.\ngctl apply WoW --test-mode
```

Type `YES`. Within those 3 minutes:

| Check | Expected |
|---|---|
| Open Chrome/Edge, load any site | ❌ no connection |
| Start Spotify, play a song you haven't downloaded | ✅ plays |
| Start Battle.net | ✅ logs in / shows online |

Then **don't touch anything**. The countdown reaches 0.

✅ Expected: "Normal internet restored." The browser works again.

## 7. Test B – the full WoW check (10 minutes)

```powershell
.\ngctl apply WoW --test-mode --minutes 10
```

| Check | Expected |
|---|---|
| Battle.net → Play WoW, log in, enter the world, move around | ✅ works |
| Spotify keeps playing | ✅ |
| Chrome / Edge / Firefox | ❌ no connection |
| Steam (start it) | ❌ goes offline / can't connect. Offline Mode / Rocksmith still launches |
| Discord | ❌ can't connect |
| `https://store.steampowered.com` in a browser | ❌ (blocked twice: browser + hosts file) |

While it runs, open a **second** admin terminal and run:

```powershell
cd C:\NetGuard-test
.\ngctl status
```

✅ Expected: Active profile WoW, test-mode deadline shown, "In sync".

If something that should work doesn't (e.g. Battle.net can't log in), run this in the second window:

```powershell
.\ngctl diag blocked --minutes 5
```

Send me the output. It lists which programs got blocked.

Press **R** in the first window to finish early, or let it run out.

## 8. Test C – the safety net works without ngctl

```powershell
.\ngctl apply Offline --test-mode
```

Type `YES`, then **close the terminal window** (click X). The browser should be dead.
Wait 3½ minutes. ✅ Expected: internet comes back by itself, about 30 s after the deadline.

Then, in a new admin terminal:

```powershell
cd C:\NetGuard-test
.\ngctl status
```

✅ Expected: "Active profile: none", "Dead-man task: not present", "NetGuard rules 0".

## 9. Clean up and report

```powershell
.\ngctl diag audit off
```

```powershell
.\ngctl log
```

Send me:
1. the output of `ngctl detect` and `ngctl programs WoW` (you can blank out your user name)
2. ✅/❌ for each check in tests A, B and C
3. the `ngctl log` output
4. anything that felt confusing

## If anything goes wrong

Run `.\ngctl recover` in an admin terminal. If that fails, see `RECOVERY.md` (3 PowerShell commands).
Rebooting during test mode also restores internet, because the safety task runs at boot.

⚠️ Don't use `--permanent` yet. It has no automatic revert, and there's no service to guard it yet.
