# Milestone 2 – test sheet

What's new:
- **The NetGuard service** runs in the background as SYSTEM. It holds the lock permanently (also
  across reboots), checks the firewall every 30 seconds and right after any firewall change, and
  undoes tampering.
- **Self-protection.** Your account stays an administrator, so NetGuard protects its own service,
  registry key and folders from administrators. They can't stop, reconfigure, delete or edit them.
- **Recovery code.** Your emergency key, written on paper. It ends a lock early (until the unlock
  process in milestone 4) and allows uninstalling during a lock.

Expect about 45 minutes, plus a reboot. Keep this page on your phone.

---

## 0. Install

Download and run **NetGuardSetup.exe** from
https://github.com/dom1911k/impulse_control_internetblock/releases/latest
(use the installer this time, not "Install update" in the old version, because the service needs
to be installed).

✅ NetGuard opens. **Tools → Protection service** says *Running … self-protection on*.
Your configuration from milestone 1 is kept.

## 1. Recovery code

**Setup → 3 · Create recovery code.** Write the code on paper, type its last five characters,
click *I've written it down*.

✅ Setup says "You have a recovery code". Before that, every *Switch* button was greyed out with
"Create your recovery code first".

## 2. A real lock

**Profiles → Switch to WoW** → OK.

| Check | Expected |
|---|---|
| Banner | blue, *WoW mode* |
| WoW, Battle.net, Spotify | ✅ work |
| Chrome / Edge, Steam, Discord | ❌ no connection |
| **Maintenance** card | *Switch* greyed out: "Looser than the current profile…" |
| **Offline** card | *Switch* available (stricter is always allowed) |
| **Test lock** buttons | greyed out |
| Setup → *Replace recovery code* | greyed out ("only with normal internet") |
| Tools → **Emergency restore** → type a **wrong** code | ❌ "not right, 4 attempts left". History shows *Wrong recovery code* |

## 3. Tampering (all of these must fail or be undone)

Open **PowerShell as administrator** (Start → type `PowerShell` → *Run as administrator* → Yes).

**These must fail outright:**

| # | Try | Expected |
|---|---|---|
| a | `Stop-Service NetGuard` | ❌ "Cannot open NetGuard service" / access denied |
| b | `sc.exe config NetGuard start= disabled` | ❌ Access is denied |
| c | `sc.exe delete NetGuard` | ❌ Access is denied |
| d | `notepad C:\ProgramData\NetGuard\config.json` → change something → Save | ❌ can't save (access denied) |
| e | `Remove-Item "C:\Program Files\NetGuard\NetGuard.Service.exe"` | ❌ access denied |
| f | Settings → Apps → NetGuard → Uninstall | ❌ "NetGuard protects itself and can only be removed from inside the app" |
| g | `& "C:\Program Files\NetGuard\ngctl.exe" recover` | ❌ asks for the recovery code |

**These must be undone within a few seconds.** Run, wait about 5 s, then check:

| # | Tamper | Check | Expected |
|---|---|---|---|
| h | `Set-NetFirewallProfile -Profile Public -Enabled False` | `Get-NetFirewallProfile \| ft Name,Enabled,DefaultOutboundAction` | Public `True` again |
| i | `Set-NetFirewallProfile -All -DefaultOutboundAction Allow` | same | all `Block` again, Chrome still dead |
| j | `Remove-NetFirewallRule -DisplayName "NetGuard:Always:steam.exe"` | `Get-NetFirewallRule -DisplayName "NetGuard:Always:steam.exe"` | rule is back |
| k | `New-NetFirewallRule -DisplayName "Let Chrome out" -Direction Outbound -Action Allow -Program "C:\Program Files\Google\Chrome\Application\chrome.exe"` | `Get-NetFirewallRule -DisplayName "Let Chrome out" \| ft DisplayName,Enabled` | `Enabled False`, Chrome still dead |
| l | Notepad as admin → `C:\Windows\System32\drivers\etc\hosts` → delete the NetGuard block → save | open it again after **30–40 s** | the block is back |
| m | `Stop-Process -Name NetGuard.Service -Force` (kill it) | `Get-Service NetGuard` after ~5 s | *Running* again |

Clean up k: `Remove-NetFirewallRule -DisplayName "Let Chrome out"`.

✅ NetGuard → **History** shows **"Tampering undone"** for h–l, and *Protection started* after m.

## 4. Reboot

With WoW still active, restart the PC.

✅ Chrome still blocked, WoW and Spotify work, banner *WoW mode*, History shows *Protection started*.

## 5. Emergency restore

**Tools → Emergency restore…** → your real code. ✅ Banner *Normal internet*, Chrome works.
History shows *Normal internet restored – emergency restore with recovery code*.

## 6. Test locks still work

With normal internet: **Test lock** (3 minutes) on WoW → ends by itself.

## 7. Update path (only if a newer version than yours is on the Releases page)

**Tools → Check for updates → Install update.** NetGuard closes; open it again after a minute.
✅ Tools shows the new version, self-protection on.

## 8. Report back

1. ✅/❌ for each row in steps 1–6
2. a screenshot of the **History** tab
3. anything that felt confusing, slow or ugly

## If anything goes wrong

**RECOVERY.md** (Start menu → *NetGuard Recovery (manual steps)*). Short version: Tools →
Emergency restore with your code. **Don't uninstall to test it.** If you ever want to remove
NetGuard: Tools → Uninstall NetGuard.
