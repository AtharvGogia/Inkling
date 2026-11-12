; Inkling per-user installer (no admin rights needed).
; Build with build-installer.ps1, which publishes the app and passes AppVersion.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "Inkling"
#define AppExe "Inkling.exe"
#define PublishDir "..\Inkling\bin\Release\net10.0-windows\win-x64\publish"

[Setup]
AppId={{3F7721A8-D7BA-4948-B7E8-F05A72EDB618}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Atharv Gogia
AppComments=Type a request anywhere, press Enter, get the answer in place.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupIconFile=..\Inkling\Assets\inkling.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\dist
OutputBaseFilename=Inkling-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; A running Inkling is closed in PrepareToInstall below (and restarted afterwards), so no
; AppMutex: in silent mode that check makes setup abort instead of upgrading.
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "startup"; Description: "Start Inkling when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Inline AI answers anywhere"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the tray menu's "Start with Windows" toggle uses, so the two stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Inkling"; \
  ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue
; Clear the value on uninstall even if it was set from the tray menu instead of the installer.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Inkling"; \
  ValueType: none; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch Inkling now"; Flags: nowait postinstall skipifsilent

; Settings and log in %APPDATA%\Inkling are kept on uninstall, so a reinstall keeps your setup.

[Code]
var
  WasRunning: Boolean;

function StopInkling(): Boolean;
var
  Code: Integer;
begin
  // taskkill exits 0 only if it found and stopped a process. Inkling's CLI children are in a
  // kill-on-close job object, so they go with it.
  Result := Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if Result then Sleep(700); // let Windows release the exe file
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  WasRunning := StopInkling();
  Result := '';
end;

// Runs before the uninstaller's "app is running" check, which silently cancels in /VERYSILENT mode.
function InitializeUninstall(): Boolean;
begin
  StopInkling();
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  // Interactive installs offer "Launch Inkling now"; silent upgrades bring it back if it was running.
  if (CurStep = ssPostInstall) and WasRunning and WizardSilent() then
    ExecAsOriginalUser(ExpandConstant('{app}\{#AppExe}'), '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;
