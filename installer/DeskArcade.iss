; Desk Arcade installer (Inno Setup 6). Build it with ..\build-installer.ps1.
;
; How upgrades work
;   * AppId (MyAppGuid) identifies the product. NEVER change it, or new versions install side by side.
;   * Running a newer setup over an existing install: closes the running game, installs into the same
;     folder, keeps task choices (desktop icon, autostart), and relaunches the game if it was running.
;   * Settings and high scores live in %APPDATA%\DeskArcade and are never touched by an upgrade.
;   * Installing an older version over a newer one is refused.

#define MyAppName      "Desk Arcade"
#define MyAppExeName   "DeskArcade.exe"
#define MyAppPublisher "Imperium Games"
#define MyAppGuid      "8F3C2A6E-5B7D-4E1A-9C2F-6D4B8A1E7C35"
; Must match the mutex name in src/Program.cs
#define AppMutex       "DeskArcade.SingleInstance.v1"
#define DotNetMajor    "10"
; build-installer.ps1 passes /DSourceDir when the exe was published elsewhere (the release workflow does)
#ifndef SourceDir
  #define SourceDir    "..\dist"
#endif

#ifndef MyAppVersion
  #define MyAppVersion GetVersionNumbersString(SourceDir + "\" + MyAppExeName)
#endif

; Target architecture: /DArch=x64 (default) or /DArch=arm64. One script builds every variant:
;   x64                  DeskArcade-Setup-<version>.exe             needs the .NET runtime
;   x64  /DSelfContained DeskArcade-Setup-<version>-standalone.exe  runtime bundled
;   arm64 /DSelfContained DeskArcade-Setup-<version>-arm64.exe      runtime bundled (required)
; All variants share the AppId, so x64 and ARM64 builds upgrade each other in place.
#ifndef Arch
  #define Arch "x64"
#endif

#if Arch == "arm64"
  #ifndef SelfContained
    #error The ARM64 installer bundles the .NET runtime: compile it with /DSelfContained
  #endif
  #define Suffix "-arm64"
#elif Arch == "x64"
  #ifdef SelfContained
    #define Suffix "-standalone"
  #else
    #define Suffix ""
  #endif
#else
  #error Arch must be x64 or arm64
#endif

[Setup]
AppId={{{#MyAppGuid}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup
; Per-user install (no admin prompt). Kept fixed so every version installs to the same scope/folder.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Hide the folder page when upgrading: the previous folder is reused.
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousTasks=yes
OutputDir=Output
OutputBaseFilename=DeskArcade-Setup-{#MyAppVersion}{#Suffix}
SetupIconFile=..\assets\DeskArcade.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#elif Ver >= EncodeVer(6, 3, 0)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
#endif
#ifdef Sign
; Code signing (build-installer.ps1 -SignCertThumbprint passes /DSign and /Sdeskarcade=<signtool command>).
; Inno Setup signs Setup itself and the uninstaller it embeds; signing Setup after the build could not
; reach unins000.exe, which Setup writes on the user's machine.
SignTool=deskarcade
SignedUninstaller=yes
#endif
CloseApplications=yes
RestartApplications=no
SetupMutex=DeskArcadeSetup,Global\DeskArcadeSetup
ShowLanguageDialog=no
; the "arcade" command on PATH takes effect in terminals opened after setup
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start {#MyAppName} when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked
Name: "addtopath"; Description: "Add the ""arcade"" command to PATH (""arcade dotnet test"" lets you play while it runs)"; GroupDescription: "Command line:"

[InstallDelete]
; Files that older versions shipped but newer ones don't. Add entries here when you drop a file.
Type: files; Name: "{app}\*.pdb"

[Files]
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\packaging\windows\arcade.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DeskArcade"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart
; If autostart was on in an older install and is now unticked, remove it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "DeskArcade"; Flags: deletevalue; Tasks: not autostart
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NotOnPath; Tasks: addtopath

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  InstalledVersion: String;
  WasRunning: Boolean;

function UninstallKey(): String;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + '{' + '{#MyAppGuid}' + '}_is1';
end;

function GetInstalledVersion(): String;
begin
  if not RegQueryStringValue(HKCU, UninstallKey(), 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKLM, UninstallKey(), 'DisplayVersion', Result) then
      Result := '';
end;

function NextVersionPart(var S: String): Integer;
var
  P: Integer;
begin
  P := Pos('.', S);
  if P = 0 then
  begin
    Result := StrToIntDef(S, 0);
    S := '';
  end
  else
  begin
    Result := StrToIntDef(Copy(S, 1, P - 1), 0);
    S := Copy(S, P + 1, Length(S));
  end;
end;

{ -1 if A < B, 0 if equal, 1 if A > B. "1.2" equals "1.2.0". }
function CompareVersions(A, B: String): Integer;
var
  NA, NB: Integer;
begin
  Result := 0;
  while (Result = 0) and ((A <> '') or (B <> '')) do
  begin
    NA := NextVersionPart(A);
    NB := NextVersionPart(B);
    if NA < NB then Result := -1
    else if NA > NB then Result := 1;
  end;
end;

#ifndef SelfContained
function IsDotNetDesktopInstalled(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\{#DotNetMajor}.*'), FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;
#endif

{ The user PATH with the install directory taken out; Found says whether it was there. }
function PathWithoutApp(var Found: Boolean): String;
var
  Path, Dir, Part: String;
  P: Integer;
begin
  Result := '';
  Found := False;
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then Exit;
  Dir := Uppercase(ExpandConstant('{app}'));
  while Path <> '' do
  begin
    P := Pos(';', Path);
    if P = 0 then P := Length(Path) + 1;
    Part := Copy(Path, 1, P - 1);
    Delete(Path, 1, P);
    if (Uppercase(Part) = Dir) or (Uppercase(Part) = Dir + '\') then
      Found := True
    else if Part <> '' then
    begin
      if Result <> '' then Result := Result + ';';
      Result := Result + Part;
    end;
  end;
end;

function NotOnPath(): Boolean;
var
  Found: Boolean;
begin
  PathWithoutApp(Found);
  Result := not Found;
end;

function IsAppRunning(): Boolean;
begin
  Result := CheckForMutexes('{#AppMutex}');
end;

{ Ask the running game to quit (it saves settings), then force it if it doesn't. }
procedure StopRunningApp();
var
  ResultCode, I: Integer;
  Exe: String;
begin
  if not IsAppRunning() then Exit;
  Exe := ExpandConstant('{app}\{#MyAppExeName}');
  if FileExists(Exe) then
    Exec(Exe, '--signal quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for I := 1 to 50 do
  begin
    if not IsAppRunning() then Exit;
    Sleep(100);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function InitializeSetup(): Boolean;
#ifndef SelfContained
var
  ErrorCode: Integer;
#endif
begin
  Result := True;
  InstalledVersion := GetInstalledVersion();

  if (InstalledVersion <> '') and (CompareVersions(InstalledVersion, '{#MyAppVersion}') > 0) then
  begin
    SuppressibleMsgBox('{#MyAppName} ' + InstalledVersion + ' is already installed, which is newer than this installer ({#MyAppVersion}).' + #13#10#13#10 +
      'Downgrading is not supported. Uninstall it first if you really want this version.', mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

#ifndef SelfContained
  if not IsDotNetDesktopInstalled() then
  begin
    if SuppressibleMsgBox('{#MyAppName} needs the .NET {#DotNetMajor} Runtime (x64), which is not installed.' + #13#10#13#10 +
      'Open the download page now? Run this installer again afterwards.', mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/{#DotNetMajor}.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
#endif
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  if InstalledVersion <> '' then
  begin
    if CompareVersions(InstalledVersion, '{#MyAppVersion}') = 0 then
      Result := 'Reinstall:' + NewLine + Space + '{#MyAppName} {#MyAppVersion}' + NewLine + NewLine
    else
      Result := 'Upgrade:' + NewLine + Space + '{#MyAppName} ' + InstalledVersion + '  ->  {#MyAppVersion}' + NewLine +
        Space + 'Your settings and high scores are kept.' + NewLine + NewLine;
  end;
  Result := Result + MemoDirInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  WasRunning := IsAppRunning();
  if WasRunning then
    StopRunningApp();
  if IsAppRunning() then
    Result := '{#MyAppName} is still running. Exit it from its tray icon, then click Back and Next to retry.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  { Interactive installs offer "Launch" on the last page; silent upgrades bring the game back themselves. }
  if (CurStep = ssPostInstall) and WasRunning and WizardSilent() then
    ExecAsOriginalUser(ExpandConstant('{app}\{#MyAppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  StopRunningApp();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, Path: String;
  Found: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    Path := PathWithoutApp(Found);
    if Found then RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Path);
  end;
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{userappdata}\DeskArcade');
  if DirExists(DataDir) and
     (SuppressibleMsgBox('Also delete your {#MyAppName} settings and high scores?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
    DelTree(DataDir, True, True, True);
end;
