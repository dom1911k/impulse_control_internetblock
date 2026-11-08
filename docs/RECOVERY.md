# NetGuard – Recovery

Use this if you need normal internet back and the normal way doesn't work.
Everything here needs an **administrator** terminal
(Start → type `Terminal` → right-click → **Run as administrator**).

## 1. The normal way

```powershell
cd C:\NetGuard-test        # wherever ngctl.exe is
.\ngctl recover
```

This removes all NetGuard firewall rules, sets default outbound back to **Allow**, re-enables the
Windows rules NetGuard switched off, removes NetGuard's block from the hosts file, and logs the recovery.

(From milestone 2 on, also stop the service first: `Stop-Service NetGuard`.)

## 2. Manual fallback (if ngctl itself is broken)

Paste these into an administrator **PowerShell** window, one after another:

```powershell
Stop-Service NetGuard -ErrorAction SilentlyContinue
```

```powershell
Remove-NetFirewallRule -Group NetGuard
```

```powershell
Set-NetFirewallProfile -All -DefaultOutboundAction Allow
```

Internet works again after these three commands.

Optional tidy-up:

```powershell
# remove the test-mode safety task, if it exists
schtasks /Delete /TN "\NetGuard\DeadManSwitch" /F
```

```powershell
# switch the Windows rules NetGuard disabled back on (list is kept in state.json)
(Get-Content C:\ProgramData\NetGuard\state.json | ConvertFrom-Json).disabledForeignRules |
  ForEach-Object { Enable-NetFirewallRule -DisplayName $_.name -ErrorAction SilentlyContinue }
```

Hosts file: open `C:\Windows\System32\drivers\etc\hosts` in Notepad (run Notepad as administrator)
and delete everything between the two `NetGuard managed block` marker lines, including the markers.

## 3. Last resort

```powershell
netsh advfirewall reset
```

This resets **all** of Windows Firewall to factory defaults (also removes rules other programs
created). Only use it if nothing above works.

## Checking the result

```powershell
Get-NetFirewallProfile | Format-Table Name, Enabled, DefaultOutboundAction
```

`DefaultOutboundAction` should be `Allow` (or `NotConfigured`) for Domain, Private and Public.
