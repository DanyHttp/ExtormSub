; ExtormSub installer (Inno Setup 6). Built by build\build-installer.ps1 — do not run ISCC by hand
; unless PublishDir points at a self-contained `dotnet publish` output.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{9C3F5E62-3A0B-4E7B-9E2A-5D6C2B7A1F10}
AppName=ExtormSub
AppVersion={#AppVersion}
AppVerName=ExtormSub {#AppVersion}
AppPublisher=ExtormSub
VersionInfoVersion={#AppVersion}
; Per-user install by default: no UAC prompt. Admins can choose "install for all users".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\ExtormSub
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=ExtormSub-Setup-{#AppVersion}
SetupIconFile=..\src\ExtormSub.App\Assets\ExtormSub.ico
UninstallDisplayIcon={app}\ExtormSub.exe
UninstallDisplayName=ExtormSub
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; The running app is closed through its own `--exit` switch (see [Code]), which flushes history cleanly.
CloseApplications=no
#ifdef Sign
; The "extormsub" sign tool is defined by build\build-installer.ps1 (ISCC /S). Signs Setup and the uninstaller.
SignTool=extormsub
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ExtormSub"; Filename: "{app}\ExtormSub.exe"
Name: "{autodesktop}\ExtormSub"; Filename: "{app}\ExtormSub.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ExtormSub.exe"; Description: "{cm:LaunchProgram,ExtormSub}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\ExtormSub.exe"; Parameters: "--minimized"; Flags: nowait runasoriginaluser; Check: RelaunchAfterUpdate

[UninstallRun]
Filename: "{app}\ExtormSub.exe"; Parameters: "--exit"; Flags: runhidden waituntilterminated; RunOnceId: "ExitExtormSub"

[Code]
const
  AppMutexName = 'Local\ExtormSub.SingleInstance';

procedure WaitForAppExit();
var
  i: Integer;
begin
  for i := 1 to 40 do
  begin
    if not CheckForMutexes(AppMutexName) then Exit;
    Sleep(250);
  end;
end;

// In-app updates run Setup with /SILENT /RELAUNCH=1: start ExtormSub again when done.
function RelaunchAfterUpdate(): Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

// Upgrades: ask the running copy to exit cleanly before files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\ExtormSub.exe');
  if CheckForMutexes(AppMutexName) and FileExists(Exe) then
  begin
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    WaitForAppExit();
    if CheckForMutexes(AppMutexName) then
      Result := 'ExtormSub is still running. Close it from the tray icon (Exit) and try again.';
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    WaitForAppExit();
  if CurUninstallStep = usPostUninstall then
  begin
    // "Start with Windows" entry written by the app itself.
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ExtormSub');
    // Silent uninstalls keep user data (default answer: No).
    if SuppressibleMsgBox('Also delete your ExtormSub settings, API keys, subtitle history, downloaded models and speech engines?',
         mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\ExtormSub'), True, True, True);
      DelTree(ExpandConstant('{userappdata}\ExtormSub'), True, True, True);
    end;
  end;
end;
