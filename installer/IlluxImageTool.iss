; Illux Image Tool installer (Inno Setup 6.4 or newer).
; Build with build\build-installer.ps1 -Version x.y.z, which publishes the apps into artifacts\publish\, downloads the
; VC++ redistributable into artifacts\redist\ and then runs: iscc /DMyVersion=x.y.z installer\IlluxImageTool.iss
;
; Silent install / upgrade: IlluxImageTool-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; The updater in the watcher service runs exactly that, as SYSTEM.

#ifndef MyVersion
  #error Pass the version: iscc /DMyVersion=x.y.z installer\IlluxImageTool.iss
#endif
#if Ver < EncodeVer(6, 4, 0)
  #error Inno Setup 6.4 or newer is needed (ExecAndCaptureOutput).
#endif

#define AppName       "Illux Image Tool"
#define RepoRoot      AddBackslash(SourcePath) + "..\"
#define ServiceName   "IlluxImageToolWatcher"
#define ServiceExe    "IlluxImageToolWatcher.exe"
#define SettingsExe   "IlluxImageToolSettings.exe"
#define EskoScripts   "C:\Esko\bg_data_fastserverscrrunnt_v100\Scripts"

[Setup]
; Never change the AppId: upgrades find the existing installation through it.
AppId={{EAFE50B4-DC3B-43EA-9DF7-CB7EF9A0BB1C}
AppName={#AppName}
AppVersion={#MyVersion}
AppVerName={#AppName} {#MyVersion}
AppPublisher=Illux
AppPublisherURL=https://github.com/Illux889/illux-image-tool
AppSupportURL=https://github.com/Illux889/illux-image-tool
AppUpdatesURL=https://github.com/Illux889/illux-image-tool/releases
VersionInfoVersion={#MyVersion}
; The AE scripts look for the tool in %ProgramFiles%\Illux Image Tool, so the folder is fixed.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#RepoRoot}artifacts\installer
OutputBaseFilename=IlluxImageTool-Setup-{#MyVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
; The service, the tray helpers and the settings window are stopped in PrepareToInstall; anything else that still
; holds a file (a command-line run, for example) is closed by the Restart Manager, if need be by force.
CloseApplications=force
RestartApplications=no
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#SettingsExe}

[Tasks]
Name: "eskoscripts"; Description: "Copy the Automation Engine scripts into the Esko Script Runner folders"; Check: EskoFoldersExist

[Files]
Source: "{#RepoRoot}artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}scripts\ae\*"; DestDir: "{app}\scripts\ae"; Flags: ignoreversion
Source: "{#RepoRoot}scripts\ae\IlluxImageTool.bat"; DestDir: "{#EskoScripts}\BatchFile"; Tasks: eskoscripts; Check: EskoFolderExists('BatchFile'); Flags: ignoreversion
Source: "{#RepoRoot}scripts\ae\IlluxImageTool.ps1"; DestDir: "{#EskoScripts}\PowerShell"; Tasks: eskoscripts; Check: EskoFolderExists('PowerShell'); Flags: ignoreversion
Source: "{#RepoRoot}docs\ae-setup.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "{#RepoRoot}LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#RepoRoot}artifacts\redist\vc_redist.x64.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#AppName}\Illux Image Tool Settings"; Filename: "{app}\{#SettingsExe}"
Name: "{autoprograms}\{#AppName}\AE setup guide"; Filename: "{win}\notepad.exe"; Parameters: """{app}\docs\ae-setup.md"""

[Registry]
; Tray helper at log-in for every user (spec 11a).
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "IlluxImageToolTray"; ValueData: """{app}\{#SettingsExe}"" --tray"; Flags: uninsdeletevalue
; Event Log source of the watcher service (R32), with the message file .NET ships next to the apps.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\{#AppName}"; ValueType: expandsz; ValueName: "EventMessageFile"; ValueData: "{app}\System.Diagnostics.EventLog.Messages.dll"; Flags: uninsdeletekey

[Code]
const
  ServiceName = '{#ServiceName}';
  TrayTaskName = 'Illux Image Tool tray helper';
  VCRedistKey = 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';

  SidSystem = '*S-1-5-18';
  SidAdmins = '*S-1-5-32-544';
  SidUsers = '*S-1-5-32-545';

  AttrReparsePoint = $400;
  AttrInvalid = $FFFFFFFF;
  SeFileObject = 1;
  OwnerSecurityInfo = 1;
  WinLocalSystemSid = 22;
  WinBuiltinAdministratorsSid = 26;

  SvcNotInstalled = -1;
  SvcStopped = 1;
  SvcRunning = 4;

  DirRoot = 0;          // SYSTEM/Administrators full, Users read; protected (R29, R34)
  DirUsersModify = 1;   // inherits the root, plus an explicit Users modify grant (runs, logs, requests)
  DirAdminsOnly = 2;    // SYSTEM/Administrators only; protected (update)

var
  VCRedistNeedsRestart: Boolean;
  ServiceWasInstalled: Boolean;

function GetFileAttributesW(lpFileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function GetNamedSecurityInfoW(pObjectName: String; ObjectType: Integer; SecurityInfo: Cardinal;
  var ppsidOwner: Cardinal; ppsidGroup, ppDacl, ppSacl: Cardinal; var ppSecurityDescriptor: Cardinal): Cardinal;
  external 'GetNamedSecurityInfoW@advapi32.dll stdcall';
function IsWellKnownSid(pSid: Cardinal; WellKnownSidType: Integer): Integer;
  external 'IsWellKnownSid@advapi32.dll stdcall';
function LocalFree(hMem: Cardinal): Cardinal;
  external 'LocalFree@kernel32.dll stdcall';

{ ---------- helpers ---------- }

function DataRoot: String;
begin
  Result := ExpandConstant('{commonappdata}\{#AppName}');
end;

function RunHidden(const FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
  Log(Format('%s %s -> %d', [ExtractFileName(FileName), Params, Result]));
end;

function Sc(const Params: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

function Icacls(const Params: String): Boolean;
begin
  Result := RunHidden(ExpandConstant('{sys}\icacls.exe'), Params) = 0;
end;

function IsRunningAsSystem: Boolean;
begin
  Result := (CompareText(GetUserNameString, 'SYSTEM') = 0)
    or (CompareText(GetEnv('USERNAME'), GetComputerNameString + '$') = 0);
end;

function EskoFolderExists(const Sub: String): Boolean;
begin
  Result := DirExists('{#EskoScripts}\' + Sub);
end;

function EskoFoldersExist: Boolean;
begin
  Result := EskoFolderExists('BatchFile') or EskoFolderExists('PowerShell');
end;

function VCRedistNeeded: Boolean;
var
  Installed: Cardinal;
begin
  Result := not ((RegQueryDWordValue(HKLM64, VCRedistKey, 'Installed', Installed) and (Installed = 1))
    or (RegQueryDWordValue(HKLM32, VCRedistKey, 'Installed', Installed) and (Installed = 1)));
end;

{ ---------- service ---------- }

{ The STATE line of "sc query" ("STATE : 4  RUNNING"); SvcNotInstalled when there is no such service. }
function ServiceState: Integer;
var
  Output: TExecOutput;
  Code, I, P: Integer;
  Line: String;
begin
  Result := 0;
  try
    if not ExecAndCaptureOutput(ExpandConstant('{sys}\sc.exe'), 'query ' + ServiceName, '', SW_HIDE,
      ewWaitUntilTerminated, Code, Output) then
      Exit;
  except
    Log('sc query failed: ' + GetExceptionMessage);
    Exit;
  end;
  if Code = 1060 then
  begin
    Result := SvcNotInstalled;
    Exit;
  end;
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
  begin
    Line := Trim(Output.StdOut[I]);
    if Pos('STATE', Uppercase(Line)) = 1 then
    begin
      P := Pos(':', Line);
      if P > 0 then
      begin
        Line := Trim(Copy(Line, P + 1, Length(Line)));
        P := Pos(' ', Line);
        if P > 0 then
          Line := Copy(Line, 1, P - 1);
        Result := StrToIntDef(Line, 0);
      end;
      Exit;
    end;
  end;
end;

function WaitForServiceState(Wanted, Seconds: Integer): Boolean;
var
  I, State: Integer;
begin
  for I := 0 to Seconds * 2 do
  begin
    State := ServiceState;
    if (State = Wanted) or ((Wanted = SvcStopped) and (State = SvcNotInstalled)) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(500);
  end;
  Result := False;
end;

procedure StopService;
var
  State: Integer;
begin
  State := ServiceState;
  ServiceWasInstalled := State <> SvcNotInstalled;
  if (State = SvcNotInstalled) or (State = SvcStopped) then
    Exit;
  Sc('stop ' + ServiceName);
  if not WaitForServiceState(SvcStopped, 90) then
  begin
    Log('The service did not stop within 90 seconds; ending its process.');
    RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#ServiceExe}');
    WaitForServiceState(SvcStopped, 15);
  end;
end;

procedure StartService;
begin
  Sc('start ' + ServiceName);
  if WaitForServiceState(SvcRunning, 60) then
    Log('The service is running.')
  else
    Log('The service did not reach the running state within 60 seconds.');
end;

procedure ConfigureService;
var
  BinPath: String;
begin
  BinPath := '"\"' + ExpandConstant('{app}\{#ServiceExe}') + '\""';
  if ServiceState = SvcNotInstalled then
    Sc('create ' + ServiceName + ' binPath= ' + BinPath + ' start= auto DisplayName= "Illux Image Tool Watcher"')
  else
    { Upgrade: keep the start type and the account someone may have chosen in the settings window. }
    Sc('config ' + ServiceName + ' binPath= ' + BinPath);
  Sc('description ' + ServiceName + ' "Converts images that arrive in the watched folders set up in Illux Image Tool Settings."');
  Sc('failure ' + ServiceName + ' reset= 86400 actions= restart/60000/restart/60000/restart/60000');
  Sc('failureflag ' + ServiceName + ' 1');
end;

{ ---------- tray helper and settings window ---------- }

procedure CloseSettingsApps;
begin
  { Tray helpers of every user and an open settings window; they lock files in the program folder. }
  if RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#SettingsExe}') = 0 then
    Sleep(1000);
end;

{ As SYSTEM (an update started by the service) there is no "original user": a scheduled task for the Users group
  runs the tray helper in the session of each logged-on user. The tray helper allows one copy per user. }
procedure StartTrayForLoggedOnUsers;
var
  Service, Folder, Definition, Action, Task: Variant;
  Step: String;
begin
  try
    Step := 'connect to the Task Scheduler';
    Service := CreateOleObject('Schedule.Service');
    Service.Connect();
    Folder := Service.GetFolder('\');
    Step := 'define the task';
    Definition := Service.NewTask(0);
    Definition.RegistrationInfo.Description := 'Starts the Illux Image Tool tray helper for the logged-on users after an update.';
    Definition.Principal.GroupId := 'S-1-5-32-545';
    Definition.Principal.LogonType := 4;    { TASK_LOGON_GROUP }
    Definition.Principal.RunLevel := 0;     { TASK_RUNLEVEL_LUA }
    Definition.Settings.ExecutionTimeLimit := 'PT0S';
    Definition.Settings.DisallowStartIfOnBatteries := False;
    Definition.Settings.StopIfGoingOnBatteries := False;
    Definition.Settings.MultipleInstances := 0;    { TASK_INSTANCES_PARALLEL }
    Definition.Settings.Priority := 5;
    Action := Definition.Actions.Create(0);    { TASK_ACTION_EXEC }
    Action.Path := ExpandConstant('{app}\{#SettingsExe}');
    Action.Arguments := '--tray';
    Step := 'register the task';
    { No user or password: the principal of the definition (the Users group) is used. }
    Task := Folder.RegisterTaskDefinition(TrayTaskName, Definition, 6, Unassigned, Unassigned, 4, Unassigned);
    Step := 'run the task';
    Task.Run(Unassigned);
    Log('Started the tray helper for the logged-on users.');
  except
    Log('Could not start the tray helper for the logged-on users (' + Step + '): ' + GetExceptionMessage);
  end;
end;

procedure DeleteTrayTask;
var
  Service: Variant;
begin
  try
    Service := CreateOleObject('Schedule.Service');
    Service.Connect();
    Service.GetFolder('\').DeleteTask(TrayTaskName, 0);
  except
    { Not registered: nothing to remove. }
  end;
end;

procedure StartTray;
var
  Code: Integer;
begin
  if IsRunningAsSystem then
    StartTrayForLoggedOnUsers
  else if ExecAsOriginalUser(ExpandConstant('{app}\{#SettingsExe}'), '--tray', '', SW_SHOWNORMAL, ewNoWait, Code) then
    Log('Started the tray helper.')
  else
    Log(Format('Could not start the tray helper (%d).', [Code]));
end;

{ ---------- ProgramData (R29, R34) ---------- }

function IsReparsePoint(const Path: String): Boolean;
var
  Attributes: Cardinal;
begin
  Attributes := GetFileAttributesW(Path);
  Result := (Attributes <> AttrInvalid) and ((Attributes and AttrReparsePoint) <> 0);
end;

function HasTrustedOwner(const Path: String): Boolean;
var
  Owner, Descriptor: Cardinal;
begin
  Result := False;
  Owner := 0;
  Descriptor := 0;
  if GetNamedSecurityInfoW(Path, SeFileObject, OwnerSecurityInfo, Owner, 0, 0, 0, Descriptor) <> 0 then
    Exit;
  try
    Result := (IsWellKnownSid(Owner, WinLocalSystemSid) <> 0) or (IsWellKnownSid(Owner, WinBuiltinAdministratorsSid) <> 0);
  finally
    LocalFree(Descriptor);
  end;
end;

function ApplyAcl(const Path: String; Kind: Integer): Boolean;
var
  Q: String;
begin
  Q := '"' + Path + '"';
  case Kind of
    DirRoot:
      Result := Icacls(Q + ' /setowner ' + SidAdmins)
        and Icacls(Q + ' /inheritance:r /grant:r ' + SidSystem + ':(OI)(CI)F ' + SidAdmins + ':(OI)(CI)F ' + SidUsers + ':(OI)(CI)RX');
    DirUsersModify:
      Result := Icacls(Q + ' /grant ' + SidUsers + ':(OI)(CI)M');
    DirAdminsOnly:
      Result := Icacls(Q + ' /inheritance:r /grant:r ' + SidSystem + ':(OI)(CI)F ' + SidAdmins + ':(OI)(CI)F');
  else
    Result := False;
  end;
end;

{ A fresh root may have been written into by someone else in the moment before its ACL was set: remove such files.
  (Deleting a hard link or symbolic link only removes the link.) }
procedure RemoveFilesFromFreshRoot(const Root: String);
var
  Find: TFindRec;
begin
  if FindFirst(Root + '\*', Find) then
  try
    repeat
      if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
      begin
        Log('Removing unexpected file from the new data folder: ' + Find.Name);
        DeleteFile(Root + '\' + Find.Name);
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

{ An existing folder is kept as it is (with its ACL) only if it is a real folder owned by SYSTEM or Administrators;
  anything else (left by a program run before the install, or planted by a user) is moved aside. A missing folder is
  created and gets its ACL while it is still empty. Existing folders are never re-ACLed, so no ACL is ever pushed onto
  files a user may have placed in them. }
function EnsureDataDir(const Path: String; Kind: Integer): String;
var
  Aside: String;
begin
  Result := '';
  if GetFileAttributesW(Path) <> AttrInvalid then
  begin
    if DirExists(Path) and not IsReparsePoint(Path) and HasTrustedOwner(Path) then
      Exit;
    Aside := Path + ' (untrusted ' + GetDateTimeString('yyyymmdd-hhnnss', '-', '-') + ')';
    Log('Moving "' + Path + '" aside to "' + Aside + '": it is a link or not owned by SYSTEM/Administrators.');
    if not RenameFile(Path, Aside) then
    begin
      Result := 'The folder "' + Path + '" is not owned by SYSTEM or Administrators and could not be moved aside. '
        + 'Close the programs that use it, or remove it, and run the installer again.';
      Exit;
    end;
  end;

  if not CreateDir(Path) then
  begin
    Result := 'Could not create the folder "' + Path + '".';
    Exit;
  end;
  if not ApplyAcl(Path, Kind) then
    Result := 'Could not set the access rights of "' + Path + '".'
  else if Kind = DirRoot then
    RemoveFilesFromFreshRoot(Path);
end;

function PrepareDataFolders: String;
var
  Root: String;
begin
  Root := DataRoot;
  Result := EnsureDataDir(Root, DirRoot);
  if Result = '' then Result := EnsureDataDir(Root + '\runs', DirUsersModify);
  if Result = '' then Result := EnsureDataDir(Root + '\logs', DirUsersModify);
  if Result = '' then Result := EnsureDataDir(Root + '\requests', DirUsersModify);
  if Result = '' then Result := EnsureDataDir(Root + '\update', DirAdminsOnly);
end;

{ The service is stopped: the update that started this installer (if any) is over. }
procedure ResetUpdateState;
var
  Path: String;
begin
  Path := DataRoot + '\update-state.json';
  if FileExists(Path) then
    if not SaveStringToFile(Path, '{' + #13#10 + '  "phase": "Idle"' + #13#10 + '}' + #13#10, False) then
      Log('Could not reset ' + Path);
end;

{ ---------- VC++ runtime (R11) ---------- }

function InstallVCRedist: String;
var
  Code: Integer;
begin
  Result := '';
  if not VCRedistNeeded then
  begin
    Log('The VC++ 2015-2022 x64 runtime is installed.');
    Exit;
  end;
  ExtractTemporaryFile('vc_redist.x64.exe');
  if not Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Format('vc_redist.x64.exe -> %d', [Code]));
  case Code of
    0, 1638: Log('The VC++ runtime is installed (or a newer one already was).');
    1641, 3010: VCRedistNeedsRestart := True;
  else
    Result := Format('The Microsoft Visual C++ runtime could not be installed (error %d).', [Code]);
  end;
end;

{ ---------- event functions ---------- }

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopService;
  CloseSettingsApps;
  Result := PrepareDataFolders;
  if Result = '' then
    Result := InstallVCRedist;
  if (Result <> '') and ServiceWasInstalled then
    StartService; { nothing was changed; leave the old version running }
end;

function NeedRestart: Boolean;
begin
  Result := VCRedistNeedsRestart;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ConfigureService;
    { R31: the flag goes before the service starts, or the service could see a fresh flag and wait. }
    DeleteFile(DataRoot + '\updating.flag');
    ResetUpdateState;
    StartService;
    StartTray;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopService;
    if ServiceWasInstalled then
      Sc('delete ' + ServiceName);
    CloseSettingsApps;
    DeleteTrayTask;
    { ProgramData (configuration, logs) is kept. }
  end;
end;
