; Paz Scan Agent installer (Inno Setup 6, free: https://jrsoftware.org/isinfo.php).
;
; Build the app first (scripts/publish-windows.sh or scripts/build-installer.ps1), then:
;   iscc /DAppVersion=1.0.0 installer\PazScanAgent.iss
; The setup lands in artifacts\.
;
; Per-user and without admin rights: it installs under %LOCALAPPDATA%\Programs and starts at
; sign-in through the current user's Run key, which is all a scanner on someone's desk needs.

#ifndef AppVersion
  ; No default: one would quietly disagree with the program's own version after a bump.
  #error Pass the version: iscc /DAppVersion=1.2.3 installer\PazScanAgent.iss
#endif
#define AppName "Paz Scan Agent"
#define AppExe "PazScanAgent.exe"
#define SettingsFile "appsettings.json"
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

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PazScanAgent"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Flags: nowait skipifnotsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Code]
// A running agent holds its files open, and so does the NAPS2 worker it starts (/T takes that too).
// It keeps nothing worth saving (scans in progress belong to a browser tab that will retry), so stop
// it rather than ask the person to find it in the tray.
procedure StopAgent;
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     and (ResultCode = 0) then
    // Killed; Windows lets go of its files a moment later.
    Sleep(1000);
end;

// An upgrade replaces the whole app; nothing from the previous version may linger beside it. Kept:
// the person's own settings file, and the uninstaller, which Setup updates rather than replaces.
// Only a folder that holds the agent is cleared, never one some other /DIR= pointed at.
procedure ClearPreviousVersion;
var
  Dir: String;
  Found: TFindRec;
begin
  Dir := ExpandConstant('{app}');
  if not FileExists(Dir + '\{#AppExe}') then Exit;
  if FindFirst(Dir + '\*', Found) then
  try
    repeat
      if (Found.Name <> '.') and (Found.Name <> '..') and (CompareText(Found.Name, '{#SettingsFile}') <> 0)
         and (Pos('unins', Lowercase(Found.Name)) <> 1) then
      begin
        if Found.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          DelTree(Dir + '\' + Found.Name, True, True, True)
        else
          DeleteFile(Dir + '\' + Found.Name);
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAgent;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    ClearPreviousVersion;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopAgent;
    // Setup did not install the settings file, so the uninstaller would leave it, and the folder.
    DeleteFile(ExpandConstant('{app}\{#SettingsFile}'));
  end;
end;
