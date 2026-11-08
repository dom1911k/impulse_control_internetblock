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
AppPublisherURL=https://github.com/dom1911k/impulse_control_internetblock
AppSupportURL=https://github.com/dom1911k/impulse_control_internetblock/blob/main/docs/RECOVERY.md
AppUpdatesURL=https://github.com/dom1911k/impulse_control_internetblock/releases
VersionInfoVersion={#AppVersion}
MinVersion=10.0.17763
InfoBeforeFile=..\docs\BEFORE-INSTALL.txt
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
Name: "{autoprograms}\NetGuard Emergency Restore"; Filename: "{app}\ngctl.exe"; Parameters: "recover --pause"; Comment: "Ends any NetGuard lock with your recovery code"
Name: "{autoprograms}\NetGuard - manual recovery steps"; Filename: "{app}\RECOVERY.md"
Name: "{autodesktop}\NetGuard"; Filename: "{app}\NetGuard.exe"; Tasks: desktopicon

[Registry]
; NetGuard's tray icon starts with Windows for every user.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NetGuard"; ValueData: """{app}\NetGuard.exe"" --tray"; Flags: uninsdeletevalue

[Messages]
FinishedLabel=NetGuard is installed and its protection service is running.%n%nOpen NetGuard (it also sits in the tray next to the clock). If this is your first time, the setup wizard walks you through everything, including your recovery code.

[Run]
; The protection service: LocalSystem, automatic start, restarts itself if it crashes.
Filename: "{sys}\sc.exe"; Parameters: "create NetGuard binPath= ""\""{app}\NetGuard.Service.exe\"""" start= auto DisplayName= NetGuard"; Flags: runhidden waituntilterminated; StatusMsg: "Installing the NetGuard service..."
Filename: "{sys}\sc.exe"; Parameters: "config NetGuard binPath= ""\""{app}\NetGuard.Service.exe\"""" start= auto"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description NetGuard ""Keeps NetGuard's internet lock in place and repairs any change."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure NetGuard reset= 86400 actions= restart/2000/restart/2000/restart/2000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start NetGuard"; Flags: runhidden waituntilterminated; StatusMsg: "Starting the NetGuard service..."
; Open NetGuard afterwards with the user's normal rights (not the installer's admin rights).
Filename: "{app}\NetGuard.exe"; Description: "Start NetGuard now"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\NetGuard.exe"; Flags: nowait runasoriginaluser; Check: ShouldStartAppSilently

; Uninstalling must always leave the PC with normal internet. NetGuard -> Settings -> Uninstall has already
; restored internet and stopped the service; these steps make sure of it:
; 1) NetGuard's own recovery, 2) the plain PowerShell fallback in case (1) failed, 3) remove the service.
[UninstallRun]
Filename: "{app}\ngctl.exe"; Parameters: "recover --quiet --reason ""uninstall"""; Flags: runhidden waituntilterminated; RunOnceId: "NetGuardRecover"
Filename: "powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ""Stop-Service NetGuard -Force -ErrorAction SilentlyContinue; Get-NetFirewallRule -Group 'NetGuard' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; Set-NetFirewallProfile -All -DefaultOutboundAction Allow; schtasks /Delete /TN '\NetGuard\DeadManSwitch' /F 2>$null"""; Flags: runhidden waituntilterminated; RunOnceId: "NetGuardRecoverFallback"
Filename: "{sys}\sc.exe"; Parameters: "delete NetGuard"; Flags: runhidden waituntilterminated; RunOnceId: "NetGuardDeleteService"

[UninstallDelete]
Type: files; Name: "{app}\*.old"

[Code]
function IsSystemAccount: Boolean;
begin
  Result := CompareText(GetUserNameString, 'SYSTEM') = 0;
end;

// After a silent update started by the NetGuard service (running as SYSTEM) there's nobody to show the app to.
function ShouldStartAppSilently: Boolean;
begin
  Result := WizardSilent and not IsSystemAccount;
end;

function ServiceRunning: Boolean;
var
  ResultCode: Integer;
begin
  Exec('powershell.exe',
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "if ((Get-Service NetGuard -ErrorAction SilentlyContinue).Status -eq ''Running'') { exit 1 } else { exit 0 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode = 1;
end;

// Updating: stop the service so its files can be replaced. The firewall rules stay in place meanwhile,
// so an active lock is not interrupted; the service starts again at the end of setup.
// NetGuard protects its service from administrators, so this only works when the update was started
// from inside NetGuard (the service then runs this installer itself).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('powershell.exe',
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Stop-Service NetGuard -Force -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Running as SYSTEM (update started by the service): make sure the service process is gone, and close the
  // NetGuard window in the user's session - Restart Manager can't close apps in another session.
  if IsSystemAccount then
    Exec('powershell.exe',
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-Process NetGuard.Service, NetGuard -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 1"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ServiceRunning then
    Result := 'A NetGuard lock is active and NetGuard protects itself, so this installer can''t replace it.' + #13#10#13#10 +
              'To update: open NetGuard -> Settings -> Check for updates -> Install update.' + #13#10 +
              'To remove it: open NetGuard -> Settings -> Advanced -> Uninstall NetGuard.'
  else
    Result := '';
end;

function InitializeUninstall: Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  // Without an active lock NetGuard doesn't protect itself, so the service simply stops here.
  Exec('powershell.exe',
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Stop-Service NetGuard -Force -ErrorAction SilentlyContinue"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ServiceRunning then
  begin
    MsgBox('A NetGuard lock is active and NetGuard protects itself. It can only be removed from inside the app:' + #13#10#13#10 +
           'open NetGuard -> Settings -> Advanced -> Uninstall NetGuard (needs your recovery code during a lock).', mbInformation, MB_OK);
    Result := False;
  end;
end;
