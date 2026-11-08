; NetGuard installer (Inno Setup 6). Built by CI:
;   ISCC.exe /DAppVersion=0.1.N installer\NetGuard.iss
; Expects the published files in ..\publish\NetGuard (NetGuard.exe, ngctl.exe, docs).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{8A3F2C1E-5B7D-4E21-9C55-3D2A1B0C9E7F}
AppName=NetGuard
AppVersion={#AppVersion}
AppVerName=NetGuard {#AppVersion}
AppPublisher=NetGuard
DefaultDirName={autopf}\NetGuard
DefaultGroupName=NetGuard
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\out
OutputBaseFilename=NetGuardSetup
SetupIconFile=..\src\NetGuard.App\Assets\netguard.ico
UninstallDisplayIcon={app}\NetGuard.exe
UninstallDisplayName=NetGuard
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\NetGuard\*"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\NetGuard"; Filename: "{app}\NetGuard.exe"
Name: "{autoprograms}\NetGuard Recovery (manual steps)"; Filename: "{app}\RECOVERY.md"
Name: "{autodesktop}\NetGuard"; Filename: "{app}\NetGuard.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NetGuard.exe"; Description: "Start NetGuard now"; Flags: nowait postinstall skipifsilent shellexec

; Uninstalling must always leave the PC with normal internet:
; 1) NetGuard's own recovery, 2) the plain PowerShell fallback in case (1) failed.
[UninstallRun]
Filename: "{app}\ngctl.exe"; Parameters: "recover --quiet --reason ""uninstall"""; Flags: runhidden waituntilterminated; RunOnceId: "NetGuardRecover"
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ""Get-NetFirewallRule -Group 'NetGuard' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; Set-NetFirewallProfile -All -DefaultOutboundAction Allow; schtasks /Delete /TN '\NetGuard\DeadManSwitch' /F 2>$null"""; Flags: runhidden waituntilterminated; RunOnceId: "NetGuardRecoverFallback"

[UninstallDelete]
Type: files; Name: "{app}\*.old"
