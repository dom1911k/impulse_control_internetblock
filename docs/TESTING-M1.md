# Milestone 1 – test sheet

What you're testing: the core engine. NetGuard turns a profile into Windows Firewall rules, shows
exactly what it would change (**Preview**), and in **test mode** restores normal internet by itself.
There's no background service, cooldown or tray icon yet. Those are milestones 2–4.

Expect about 30–40 minutes. Keep this page open on your phone, because during the test your PC only
has internet for WoW, Battle.net and Spotify.

---

## 0. Install

1. Open https://github.com/dom1911k/impulse_control_internetblock/releases/latest
   (no sign-in needed) and download **NetGuardSetup.exe**.
2. Run it. Windows will probably warn you because the file isn't code-signed:
   **More info → Run anyway**. Then click through the installer.
3. Start **NetGuard** from the Start menu. Windows asks for administrator permission; say **Yes**.
   NetGuard needs it to change the firewall.

**Updates later:** NetGuard → **Tools** → **Check for updates** → **Install update**.
This only works with normal internet, not during a lock.

## 1. Setup tab

1. Click **Find apps**. ✅ The Details box at the bottom lists your WoW folder, Battle.net, the
   Blizzard Agent folder, `Spotify.exe` under `C:\Users\<you>\AppData\Roaming\Spotify`, and Steam.
   If "Spotify (Store)" lists anything, tell me.
2. Click **Create configuration**.
3. Click **Show allowed files**. ✅ Under *Profile WoW* you see `+` lines for `Wow.exe` (and
   `WowClassic.exe` if installed), several Battle.net files, `Agent.exe` and `Spotify.exe`, each
   `[signed by …]`. ❗ Any `x … REJECTED` line for a Blizzard or Spotify file: send me that line.

## 2. Preview (changes nothing)

**Profiles** tab → on the **WoW** card click **Preview**. The Details box should list:
- default OUTBOUND → BLOCK for Domain, Private, Public
- `Add rule NetGuard:WoW:…` for each allowed program, plus `dns-udp`, `dns-tcp`, `time-sync`,
  `connectivity-check`
- `Add rule NetGuard:Always:steam.exe` etc. as **BLOCK**
- `Disable non-NetGuard allow rule …` for a few dozen built-in Windows rules (expected: these
  would let other programs through otherwise)
- a hosts-file line for the Steam websites

and end with **"This was only a preview – nothing was changed."**

## 3. Optional: record blocked connections

**Tools** → **Start recording**. This makes troubleshooting in step 5 much easier.

## 4. Test A – 3-minute automatic end

Close Battle.net, WoW, Spotify, Steam, Discord and browsers first.
**Profiles** → Test length **3 minutes** → **WoW** card → **Start test lock** → OK.

The top banner turns blue: *"WoW mode"*, with a countdown. Within those 3 minutes:

| Check | Expected |
|---|---|
| Open Chrome/Edge, load any site | ❌ no connection |
| Start Spotify, play a song you haven't downloaded | ✅ plays |
| Start Battle.net | ✅ logs in / shows online |

Then don't touch anything. ✅ When the countdown reaches 0, the banner goes back to
*"Normal internet"* and the browser works again.

## 5. Test B – the full WoW check (10 minutes)

Test length **10 minutes** → **WoW** → **Start test lock**.

| Check | Expected |
|---|---|
| Battle.net → Play WoW, log in, enter the world, move around | ✅ works |
| Spotify keeps playing | ✅ |
| Chrome / Edge / Firefox | ❌ no connection |
| Steam (start it) | ❌ can't connect. Offline Mode / Rocksmith still launches |
| Discord | ❌ can't connect |
| `https://store.steampowered.com` in a browser | ❌ |

While it runs: **Tools** → **Show status**. ✅ "Active profile: WoW", and at the bottom
"In sync: the firewall matches the active profile."

If something that should work doesn't (e.g. Battle.net can't log in): **Tools** →
**Show blocked (last 10 min)**. Send me that list; it shows exactly which program was blocked.

End it early with **Restore internet now** in the banner, or let it run out.

## 6. Test C – the safety net works even if NetGuard is closed

Test length **3 minutes** → **Offline** card → **Start test lock**. Then close the NetGuard window
and choose **No – close anyway**. The browser should be dead.

Wait 3½ minutes. ✅ Expected: internet comes back by itself, about 30 s after the deadline.
Open NetGuard again: ✅ banner says *"Normal internet"*, and **History** shows the test lock ending.

## 7. Report back

**Tools** → **Stop recording**. Then send me:
1. what **Find apps** and **Show allowed files** showed (you can blank out your user name)
2. ✅/❌ for each check in tests A, B and C
3. a screenshot of the **History** tab
4. anything that looked wrong, confusing or ugly in the window

## If anything goes wrong

- NetGuard → **Tools** → **Restore normal internet…**
- or restart the PC: during a test lock, the safety task restores internet at boot
- or Start menu → **NetGuard Recovery (manual steps)**: 3 PowerShell commands
- Uninstalling NetGuard (Settings → Apps) always restores normal internet too.

## For experts: the console tool

`ngctl.exe` is installed next to `NetGuard.exe` in `C:\Program Files\NetGuard` and does the same
things from an admin terminal: `ngctl status`, `ngctl apply WoW --dry-run`,
`ngctl apply WoW --test-mode --minutes 10`, `ngctl recover`, `ngctl diag blocked`, `ngctl update`.
Run `ngctl help` for the full list.
