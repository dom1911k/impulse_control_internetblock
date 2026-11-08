# NetGuard – Recovery

## 1. The normal way: emergency restore with your recovery code

NetGuard → **Tools → Emergency restore…** → type your recovery code.

Normal internet is back immediately. NetGuard stays installed; it just has no lock to keep.

## 2. If the NetGuard window doesn't work

Administrator terminal (Start → type `Terminal` → right-click → *Run as administrator*):

```powershell
& "C:\Program Files\NetGuard\ngctl.exe" recover --code XXXXX-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX
```

## 3. If the NetGuard service itself is broken (not running)

Then nothing protects the firewall settings, and no code is needed. In an administrator PowerShell:

```powershell
Remove-NetFirewallRule -Group NetGuard
```

```powershell
Set-NetFirewallProfile -All -DefaultOutboundAction Allow
```

Internet works again after these two commands. (`ngctl recover` without a code does the same plus a
tidy-up, when the service isn't running.)

Check:

```powershell
Get-NetFirewallProfile | Format-Table Name, Enabled, DefaultOutboundAction
```

`DefaultOutboundAction` should be `Allow` (or `NotConfigured`).

## 4. Lost your recovery code?

That's deliberately slow. The steps are on GitHub, not on this PC:
`docs/LOST-CODE.md` in https://github.com/dom1911k/impulse_control_internetblock
