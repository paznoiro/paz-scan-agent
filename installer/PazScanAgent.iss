; Paz Scan Agent installer (Inno Setup 6, free: https://jrsoftware.org/isinfo.php).
;
; Build the app first (scripts/publish-windows.sh or scripts/build-installer.ps1), then:
;   iscc /DAppVersion=1.0.0 installer\PazScanAgent.iss
; The setup lands in artifacts\.
;
; Per-user and without admin rights: it installs under %LOCALAPPDATA%\Programs and starts at
; sign-in through the current user's Run key, which is all a scanner on someone's desk needs.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Paz Scan Agent"
#define AppExe "PazScanAgent.exe"
#define PublishDir "..\artifacts\win-x64"

[Setup]
AppId={{D22B0970-C90B-401F-9D70-1FB376376FB2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=PazNoiro
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts
OutputBaseFilename=PazScanAgentSetup-{#AppVersion}
SetupIconFile=..\src\PazScan.Agent.Windows\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; An upgrade replaces the whole app; nothing from the previous version may linger beside it.
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PazScanAgent"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopAgent"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Code]
// A running agent holds its files open. It keeps nothing worth saving (scans in progress belong to
// a browser tab that will retry), so stop it rather than ask the person to find it in the tray.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
