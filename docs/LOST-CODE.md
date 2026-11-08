# Lost recovery code

This is deliberately slow and fiddly. NetGuard protects its service, registry key and folders from
administrators. Getting rid of it without the code means taking ownership of those parts by hand.

If you only want to get out of a lock: wait. Stricter profiles were the only ones you could switch
to, and the code was the only way out until the unlock process (milestone 4) exists.

## Steps (administrator account, about 15 minutes)

1. **Stop the service from starting.**
   1. Start → `regedit` → *Run as administrator*.
   2. Go to `HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\NetGuard`.
   3. Right-click **NetGuard** → *Permissions…* → *Advanced* → next to *Owner* click *Change* →
      type `Administrators` → OK → tick *Replace owner on subcontainers and objects* → OK.
   4. Back in *Permissions*, select *Administrators* → tick *Full Control* → OK.
   5. Double-click the value **Start** and set it to `4`.
2. **Restart the PC.** The service doesn't start any more.
3. **Restore internet**, administrator PowerShell:
   ```powershell
   Remove-NetFirewallRule -Group NetGuard
   ```
   ```powershell
   Set-NetFirewallProfile -All -DefaultOutboundAction Allow
   ```
4. **Take back NetGuard's folders**, so it can be uninstalled:
   ```powershell
   takeown /f "C:\Program Files\NetGuard" /r /d y
   ```
   ```powershell
   icacls "C:\Program Files\NetGuard" /grant Administrators:F /t
   ```
   ```powershell
   takeown /f "C:\ProgramData\NetGuard" /r /d y
   ```
   ```powershell
   icacls "C:\ProgramData\NetGuard" /grant Administrators:F /t
   ```
5. **Uninstall** NetGuard in Settings → Apps. Reinstall it afterwards if you want, and create a new
   recovery code.
