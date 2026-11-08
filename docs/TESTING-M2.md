# Milestone 2 – test sheet

What's new: the **NetGuard service** runs in the background as SYSTEM. It holds the lock
permanently (also across reboots), checks the firewall every 30 seconds and right after any
firewall change, and undoes tampering. The NetGuard window now runs with normal rights and only
*asks* the service. The service decides what's allowed.

Expect about 45 minutes, plus a reboot. Keep this page on your phone.

---

## 0. Install

Download and run **NetGuardSetup.exe** from
https://github.com/dom1911k/impulse_control_internetblock/releases/latest
(this time use the installer, not "Install update" in the old version, because the service needs
to be installed).

✅ NetGuard opens. **Tools → Protection service** says *Running*. The banner says *Normal internet*.
Your configuration from milestone 1 is kept.

## 1. Account separation

Follow **ACCOUNTS.md** (next to NetGuard in the Start menu folder / install folder, or on GitHub).
It's needed for step 4. Without it, your daily account can simply stop the service.

From here on, "**as domin**" means your normal (now standard) account, and "**as admin**" means a
window started with *Run as administrator* where you type the NetGuardAdmin password.

## 2. A real lock (as domin)

NetGuard → **Profiles** → **Switch to WoW** → OK.

| Check | Expected |
|---|---|
| Banner | blue, *WoW mode*, "NetGuard keeps it in place…" |
| WoW, Battle.net, Spotify | ✅ work |
| Chrome / Edge, Steam, Discord | ❌ no connection |
| **Maintenance** card | *Switch* greyed out, with "looser than WoW … needs an administrator" |
| **Offline** card | *Switch* available (stricter is always allowed) |
| **Test lock** buttons | greyed out (a test lock would be a way out of a real lock) |
| Tools → **Restore normal internet…** | ❌ "This needs an administrator" |

## 3. Tampering as domin (all of these must simply fail)

| Try | Expected |
|---|---|
| Windows Security → Firewall & network protection → Public network → switch the firewall **off** | Windows asks for the admin password → Cancel |
| Start menu → *Services* → **NetGuard** → Stop | ❌ "Access is denied" (or the button is greyed out) |
| Open `C:\ProgramData\NetGuard\config.json` in Notepad and save a change | ❌ can't open or can't save |
| PowerShell (normal): `Remove-NetFirewallRule -DisplayName "NetGuard:WoW:Spotify.exe"` | ❌ "Access is denied" |
| PowerShell (normal): `Stop-Service NetGuard` | ❌ "Cannot open NetGuard service" |

## 4. Tampering as admin (it must be undone within a few seconds)

Open **PowerShell as administrator** (Start → type `PowerShell` → *Run as administrator* →
NetGuardAdmin password). Run each command, wait about 5 seconds, then run the check.

| # | Tamper | Check after ~5 s | Expected |
|---|---|---|---|
| a | `Set-NetFirewallProfile -Profile Public -Enabled False` | `Get-NetFirewallProfile \| ft Name,Enabled,DefaultOutboundAction` | Public is `True` again |
| b | `Set-NetFirewallProfile -All -DefaultOutboundAction Allow` | same as a | all `Block` again, Chrome still dead |
| c | `Remove-NetFirewallRule -DisplayName "NetGuard:Always:steam.exe"` | `Get-NetFirewallRule -DisplayName "NetGuard:Always:steam.exe"` | the rule is back |
| d | `New-NetFirewallRule -DisplayName "Let Chrome out" -Direction Outbound -Action Allow -Program "C:\Program Files\Google\Chrome\Application\chrome.exe"` | `Get-NetFirewallRule -DisplayName "Let Chrome out" \| ft DisplayName,Enabled` | `Enabled False`, Chrome still dead |
| e | Notepad as admin → open `C:\Windows\System32\drivers\etc\hosts` → delete the NetGuard block → save | open hosts again after **30–40 s** | the block is back |

Clean up d afterwards: `Remove-NetFirewallRule -DisplayName "Let Chrome out"`.

✅ NetGuard → **History** shows a **"Tampering undone"** line for each (a–e).

Two things an admin **is** allowed to do by design:

| # | Do | Expected |
|---|---|---|
| f | `Stop-Process -Name NetGuard.Service -Force` (kill it) | `Get-Service NetGuard` shows *Running* again within ~10 s (Windows restarts it) |
| g | `Stop-Service NetGuard` | Service stops. **The lock stays** (Chrome still dead); only repairs pause. NetGuard's banner turns red. Then `Start-Service NetGuard` → back to normal, History shows *Protection stopped / started*. |

## 5. Reboot

With WoW still active, restart the PC and sign in as domin.

✅ Chrome is still blocked, WoW and Spotify work, banner says *WoW mode*, History shows
*Protection started* after the reboot.

## 6. Back to normal internet (as admin)

NetGuard → **Tools → Restart as administrator** (NetGuardAdmin password) →
**Tools → Restore normal internet…** → OK. ✅ Banner *Normal internet*, Chrome works.
Close that admin window and open NetGuard normally again.

## 7. Test locks still work (as domin)

With normal internet: **Test lock** (3 minutes) on WoW → ends by itself, banner back to
*Normal internet*.

## 8. Report back

1. ✅/❌ for each row in steps 2–7
2. a screenshot of the **History** tab
3. anything that felt confusing, slow or ugly

## If anything goes wrong

See **RECOVERY.md** (Start menu → *NetGuard Recovery (manual steps)*). Short version, in an admin
PowerShell: `Stop-Service NetGuard`, `Remove-NetFirewallRule -Group NetGuard`,
`Set-NetFirewallProfile -All -DefaultOutboundAction Allow`. Uninstalling NetGuard also always
restores normal internet.
