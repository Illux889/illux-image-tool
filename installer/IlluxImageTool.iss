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

  AttrDirectory = $10;
  AttrReparsePoint = $400;
  AttrInvalid = $FFFFFFFF;
  SeFileObject = 1;
  OwnerSecurityInfo = 1;
  DaclSecurityInfo = 4;
  SddlRevision1 = 1;
  ErrorInvalidOwner = 1307;

  SvcNotInstalled = -1;
  SvcStopped = 1;
  SvcRunning = 4;

  { Exit code of a completed install whose service did not start (Inno's own codes stay below 10). }
  ExitServiceNotStarted = 10;

  DirRoot = 0;          // SYSTEM/Administrators full, Users read (R29, R34)
  DirUsersModify = 1;   // as the root, plus Users: create files/folders here and modify what is inside (runs, logs, requests)
  DirAdminsOnly = 2;    // SYSTEM/Administrators only (update)

  { Every folder gets a protected DACL, set atomically when it is created (no moment with ProgramData's inherited
    rights). 0x1200a9 = read + execute, 0x1301bf = modify, 0x100006 = create files + create folders (+ synchronize). }
  SddlRoot = 'D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)';
  SddlUsersModify = 'D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICIIO;0x1301bf;;;BU)(A;;0x100006;;;BU)';
  SddlAdminsOnly = 'D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)';

  MaskFull = $1F01FF;
  MaskGenericAll = $10000000;
  { Any of: write data/add file, append/add subfolder, write EA, delete child, write attributes, delete, write DAC,
    write owner, generic all, generic write. }
  MaskWrite = $500D0156;
  MaskModify = $1301BF;
  MaskCreateHere = $100006;

var
  VCRedistNeedsRestart: Boolean;
  ServiceWasInstalled: Boolean;   // the service existed when this setup began
  ServiceStoppedByUs: Boolean;
  ServiceCreatedByUs: Boolean;
  TraysClosed: Boolean;
  UpdatingFlagExisted: Boolean;
  PrepareStarted: Boolean;
  InstallCompleted: Boolean;
  RecoveryDone: Boolean;
  ServiceStartFailed: Boolean;

function GetFileAttributesW(lpFileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
{ Native memory is only ever touched through these calls: Pascal Script records and strings are never handed to
  Windows to write into. }
function CreateDirectoryW(lpPathName: String; lpSecurityAttributes: Cardinal): Integer;
  external 'CreateDirectoryW@kernel32.dll stdcall';
function LocalAlloc(uFlags, uBytes: Cardinal): Cardinal;
  external 'LocalAlloc@kernel32.dll stdcall';
procedure CopyCardinalToMemory(Destination: Cardinal; var Source: Cardinal; Length: Cardinal);
  external 'RtlMoveMemory@kernel32.dll stdcall';
procedure CopyWordFromMemory(var Destination: Word; Source: Cardinal; Length: Cardinal);
  external 'RtlMoveMemory@kernel32.dll stdcall';
function GetNamedSecurityInfoW(pObjectName: String; ObjectType: Integer; SecurityInfo: Cardinal;
  ppsidOwner, ppsidGroup, ppDacl, ppSacl: Cardinal; var ppSecurityDescriptor: Cardinal): Cardinal;
  external 'GetNamedSecurityInfoW@advapi32.dll stdcall';
function ConvertSecurityDescriptorToStringSecurityDescriptorW(SecurityDescriptor: Cardinal; RequestedStringSDRevision: Cardinal;
  SecurityInformation: Cardinal; var StringSecurityDescriptor: Cardinal; StringSecurityDescriptorLen: Cardinal): Integer;
  external 'ConvertSecurityDescriptorToStringSecurityDescriptorW@advapi32.dll stdcall';
function ConvertStringSecurityDescriptorToSecurityDescriptorW(StringSecurityDescriptor: String; StringSDRevision: Cardinal;
  var SecurityDescriptor: Cardinal; SecurityDescriptorSize: Cardinal): Integer;
  external 'ConvertStringSecurityDescriptorToSecurityDescriptorW@advapi32.dll stdcall';
function lstrlenW(lpString: Cardinal): Integer;
  external 'lstrlenW@kernel32.dll stdcall';
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

{ The lines "sc <Params>" prints; False if sc could not be run. }
function ScOutput(const Params: String; var Code: Integer; var Lines: TArrayOfString): Boolean;
var
  Output: TExecOutput;
begin
  Result := False;
  try
    Result := ExecAndCaptureOutput(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code, Output);
    Lines := Output.StdOut;
  except
    Log('sc ' + Params + ' failed: ' + GetExceptionMessage);
  end;
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
  Lines: TArrayOfString;
  Code, I, P: Integer;
  Line: String;
begin
  Result := 0;
  if not ScOutput('query ' + ServiceName, Code, Lines) then
    Exit;
  if Code = 1060 then
  begin
    Result := SvcNotInstalled;
    Exit;
  end;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[I]);
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

{ Someone switched the service off on purpose ("sc qc": START_TYPE ... DISABLED). }
function ServiceDisabled: Boolean;
var
  Lines: TArrayOfString;
  Code, I: Integer;
begin
  Result := False;
  if not ScOutput('qc ' + ServiceName, Code, Lines) then
    Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
    if (Pos('START_TYPE', Uppercase(Lines[I])) > 0) and (Pos('DISABLED', Uppercase(Lines[I])) > 0) then
      Result := True;
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
  ServiceStoppedByUs := True;
  Sc('stop ' + ServiceName);
  if not WaitForServiceState(SvcStopped, 90) then
  begin
    Log('The service did not stop within 90 seconds; ending its process.');
    RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#ServiceExe}');
    WaitForServiceState(SvcStopped, 15);
  end;
end;

function StartService: Boolean;
begin
  Sc('start ' + ServiceName);
  Result := WaitForServiceState(SvcRunning, 60);
  if Result then
    Log('The service is running.')
  else
    Log('The service did not reach the running state within 60 seconds.');
end;

{ Registers (first install) or re-points (upgrade) the service. Runs before any file is copied: sc does not need the
  program file yet, and a failure here stops the install cleanly. Returns an error message or ''. }
function ConfigureService: String;
var
  BinPath: String;
  Code: Integer;
begin
  Result := '';
  BinPath := '"\"' + ExpandConstant('{app}\{#ServiceExe}') + '\""';
  if ServiceState = SvcNotInstalled then
  begin
    Code := Sc('create ' + ServiceName + ' binPath= ' + BinPath + ' start= auto DisplayName= "Illux Image Tool Watcher"');
    if Code <> 0 then
    begin
      Result := Format('The watcher service could not be registered (sc create, error %d).', [Code]);
      Exit;
    end;
    ServiceCreatedByUs := True;
  end
  else
  begin
    { Upgrade: keep the start type and the account someone may have chosen in the settings window. }
    Code := Sc('config ' + ServiceName + ' binPath= ' + BinPath);
    if Code <> 0 then
    begin
      Result := Format('The watcher service could not be updated (sc config, error %d).', [Code]);
      Exit;
    end;
  end;
  Code := Sc('description ' + ServiceName + ' "Converts images that arrive in the watched folders set up in Illux Image Tool Settings."');
  if Code = 0 then
    Code := Sc('failure ' + ServiceName + ' reset= 86400 actions= restart/60000/restart/60000/restart/60000');
  if Code = 0 then
    Code := Sc('failureflag ' + ServiceName + ' 1');
  if Code <> 0 then
    Result := Format('The watcher service could not be configured (sc, error %d).', [Code]);
end;

{ ---------- tray helper and settings window ---------- }

procedure CloseSettingsApps;
begin
  { Tray helpers of every user and an open settings window; they lock files in the program folder. }
  if RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#SettingsExe}') = 0 then
  begin
    TraysClosed := True;
    Sleep(1000);
  end;
end;

function XmlEscape(const Text: String): String;
begin
  Result := Text;
  StringChangeEx(Result, '&', '&amp;', True);
  StringChangeEx(Result, '<', '&lt;', True);
  StringChangeEx(Result, '>', '&gt;', True);
end;

function Schtasks(const Params: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\schtasks.exe'), Params);
end;

{ As SYSTEM (an update started by the service) there is no "original user": a scheduled task for the Users group
  runs the tray helper in the session of each logged-on user. The tray helper allows one copy per user.
  schtasks.exe does the work in its own process (the Task Scheduler COM calls inside Setup ended, as SYSTEM, in
  heap corruption when Setup exited). }
procedure StartTrayForLoggedOnUsers;
var
  Xml: TArrayOfString;
  FileName: String;
  Code: Integer;
begin
  SetArrayLength(Xml, 23);
  Xml[0] := '<?xml version="1.0" encoding="UTF-8"?>';
  Xml[1] := '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">';
  Xml[2] := '  <RegistrationInfo>';
  Xml[3] := '    <Description>Starts the Illux Image Tool tray helper for the logged-on users after an update.</Description>';
  Xml[4] := '  </RegistrationInfo>';
  Xml[5] := '  <Principals>';
  Xml[6] := '    <Principal id="Users">';
  Xml[7] := '      <GroupId>S-1-5-32-545</GroupId>';
  Xml[8] := '      <RunLevel>LeastPrivilege</RunLevel>';
  Xml[9] := '    </Principal>';
  Xml[10] := '  </Principals>';
  Xml[11] := '  <Settings>';
  Xml[12] := '    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>';
  Xml[13] := '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>';
  Xml[14] := '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>';
  Xml[15] := '    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>';
  Xml[16] := '    <Priority>5</Priority>';
  Xml[17] := '  </Settings>';
  Xml[18] := '  <Actions Context="Users">';
  Xml[19] := '    <Exec><Command>' + XmlEscape(ExpandConstant('{app}\{#SettingsExe}')) + '</Command><Arguments>--tray</Arguments></Exec>';
  Xml[20] := '  </Actions>';
  Xml[21] := '</Task>';
  Xml[22] := '';
  FileName := ExpandConstant('{tmp}\tray-task.xml');
  if not SaveStringsToUTF8File(FileName, Xml, False) then
  begin
    Log('Could not write ' + FileName);
    Exit;
  end;
  Code := Schtasks('/create /tn "' + TrayTaskName + '" /xml "' + FileName + '" /f');
  if Code = 0 then
    Code := Schtasks('/run /tn "' + TrayTaskName + '"');
  if Code = 0 then
    Log('Started the tray helper for the logged-on users.')
  else
    Log(Format('Could not start the tray helper for the logged-on users (schtasks, error %d).', [Code]));
end;

procedure DeleteTrayTask;
begin
  Schtasks('/delete /tn "' + TrayTaskName + '" /f');
end;

procedure StartTray;
var
  Code: Integer;
begin
  if not FileExists(ExpandConstant('{app}\{#SettingsExe}')) then
    Exit;
  if IsRunningAsSystem then
    StartTrayForLoggedOnUsers
  else if ExecAsOriginalUser(ExpandConstant('{app}\{#SettingsExe}'), '--tray', '', SW_SHOWNORMAL, ewNoWait, Code) then
    Log('Started the tray helper.')
  else
    Log(Format('Could not start the tray helper (%d).', [Code]));
end;

{ ---------- ProgramData (R29, R34): security descriptors ---------- }

function IsReparsePoint(const Path: String): Boolean;
var
  Attributes: Cardinal;
begin
  Attributes := GetFileAttributesW(Path);
  Result := (Attributes <> AttrInvalid) and ((Attributes and AttrReparsePoint) <> 0);
end;

{ Owner and DACL of a file or folder as SDDL, e.g. "O:BAD:PAI(A;OICI;FA;;;SY)...". }
function GetSddl(const Path: String; var Sddl: String): Boolean;
var
  Descriptor, Text: Cardinal;
  Len, I: Integer;
  Ch: Word;
begin
  Result := False;
  Sddl := '';
  Descriptor := 0;
  if GetNamedSecurityInfoW(Path, SeFileObject, OwnerSecurityInfo or DaclSecurityInfo, 0, 0, 0, 0, Descriptor) <> 0 then
    Exit;
  try
    Text := 0;
    if ConvertSecurityDescriptorToStringSecurityDescriptorW(Descriptor, SddlRevision1, OwnerSecurityInfo or DaclSecurityInfo, Text, 0) = 0 then
      Exit;
    try
      Len := lstrlenW(Text);
      for I := 0 to Len - 1 do
      begin
        CopyWordFromMemory(Ch, Text + I * 2, 2);
        Sddl := Sddl + Chr(Ch);
      end;
      Result := True;
    finally
      LocalFree(Text);
    end;
  finally
    LocalFree(Descriptor);
  end;
end;

{ SY, BA and BU in whichever form SDDL gives them. }
function NormalizeSid(const Sid: String): String;
begin
  Result := Uppercase(Sid);
  if Result = 'S-1-5-18' then Result := 'SY'
  else if Result = 'S-1-5-32-544' then Result := 'BA'
  else if Result = 'S-1-5-32-545' then Result := 'BU';
end;

function IsTrustedSid(const Sid: String): Boolean;
begin
  Result := (Sid = 'SY') or (Sid = 'BA');
end;

{ Access mask of one SDDL right code; $FFFFFFFF (counts as "may write") for codes this check does not know. }
function RightCodeMask(const Code: String): Cardinal;
begin
  if Code = 'GA' then Result := $10000000
  else if Code = 'GR' then Result := $80000000
  else if Code = 'GW' then Result := $40000000
  else if Code = 'GX' then Result := $20000000
  else if Code = 'FA' then Result := $1F01FF
  else if Code = 'FR' then Result := $120089
  else if Code = 'FW' then Result := $120116
  else if Code = 'FX' then Result := $1200A0
  else if Code = 'SD' then Result := $10000
  else if Code = 'RC' then Result := $20000
  else if Code = 'WD' then Result := $40000
  else if Code = 'WO' then Result := $80000
  else if Code = 'CC' then Result := $1
  else if Code = 'DC' then Result := $2
  else if Code = 'LC' then Result := $4
  else if Code = 'SW' then Result := $8
  else if Code = 'RP' then Result := $10
  else if Code = 'WP' then Result := $20
  else if Code = 'DT' then Result := $40
  else if Code = 'LO' then Result := $80
  else if Code = 'CR' then Result := $100
  else Result := $FFFFFFFF;
end;

{ The rights field of an ACE: "0x1301bf" or a run of two-letter codes ("FA", "FRFX"). }
function RightsMask(const Rights: String): Cardinal;
var
  I, Digit: Integer;
  Hex: String;
begin
  Result := 0;
  if (Length(Rights) > 2) and (Lowercase(Copy(Rights, 1, 2)) = '0x') then
  begin
    Hex := Lowercase(Copy(Rights, 3, Length(Rights)));
    if Length(Hex) > 8 then
    begin
      Result := $FFFFFFFF;
      Exit;
    end;
    for I := 1 to Length(Hex) do
    begin
      Digit := Pos(Hex[I], '0123456789abcdef') - 1;
      if Digit < 0 then
      begin
        Result := $FFFFFFFF;
        Exit;
      end;
      Result := Result * 16 + Digit;
    end;
  end
  else if (Length(Rights) = 0) or (Length(Rights) mod 2 <> 0) then
    Result := $FFFFFFFF
  else
    for I := 0 to Length(Rights) div 2 - 1 do
      Result := Result or RightCodeMask(Uppercase(Copy(Rights, I * 2 + 1, 2)));
end;

{ Splits "a;b;c" (empty fields kept). }
function SplitFields(const S: String): TArrayOfString;
var
  Rest: String;
  P, N: Integer;
begin
  Rest := S;
  N := 0;
  repeat
    P := Pos(';', Rest);
    SetArrayLength(Result, N + 1);
    if P > 0 then
    begin
      Result[N] := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end
    else
      Result[N] := Rest;
    N := N + 1;
  until P = 0;
end;

{ Why a folder does not have the owner and ACL the installer gives it, or '' when it does. Accepted: a real folder
  owned by SYSTEM or Administrators, protected DACL, SYSTEM and Administrators with full control, and for anyone
  else only the rights the layout allows (DirRoot: read; DirAdminsOnly: none; DirUsersModify: read, plus the two
  Users grants, which must both be there). }
function CheckDirSecurity(const Path: String; Kind: Integer): String;
var
  Sddl, Owner, Dacl, Ace, Sid, Flags: String;
  Fields: TArrayOfString;
  P, Q: Integer;
  Mask: Cardinal;
  HasSystem, HasAdmins, HasContents, HasCreate: Boolean;
begin
  Result := '';
  if IsReparsePoint(Path) then begin Result := 'it is a link'; Exit; end;
  if not DirExists(Path) then begin Result := 'it is not a folder'; Exit; end;
  if not GetSddl(Path, Sddl) then begin Result := 'its security could not be read'; Exit; end;
  Log('Security of "' + Path + '": ' + Sddl);

  P := Pos('D:', Sddl);
  if (Pos('O:', Sddl) <> 1) or (P = 0) then begin Result := 'its security could not be read'; Exit; end;
  Owner := NormalizeSid(Copy(Sddl, 3, P - 3));
  if not IsTrustedSid(Owner) then begin Result := 'it is not owned by SYSTEM or Administrators'; Exit; end;

  Dacl := Copy(Sddl, P + 2, Length(Sddl));
  Q := Pos('(', Dacl);
  if Q = 0 then Q := Length(Dacl) + 1;
  if Pos('P', Copy(Dacl, 1, Q - 1)) = 0 then begin Result := 'its access rights are inherited from ProgramData'; Exit; end;
  Dacl := Copy(Dacl, Q, Length(Dacl));

  HasSystem := False; HasAdmins := False; HasContents := False; HasCreate := False;
  while Pos('(', Dacl) = 1 do
  begin
    Q := Pos(')', Dacl);
    if Q = 0 then begin Result := 'its security could not be read'; Exit; end;
    Ace := Copy(Dacl, 2, Q - 2);
    Dacl := Copy(Dacl, Q + 1, Length(Dacl));
    Fields := SplitFields(Ace);
    if GetArrayLength(Fields) < 6 then begin Result := 'its security could not be read'; Exit; end;
    if Fields[0] = 'D' then
      Continue; { a deny entry never gives anyone access }
    if Fields[0] <> 'A' then begin Result := 'it has an unexpected kind of access entry'; Exit; end;
    Flags := Uppercase(Fields[1]);
    Mask := RightsMask(Fields[2]);
    Sid := NormalizeSid(Fields[5]);

    if IsTrustedSid(Sid) then
    begin
      if ((Mask and MaskFull) = MaskFull) or ((Mask and MaskGenericAll) <> 0) then
      begin
        if Sid = 'SY' then HasSystem := True else HasAdmins := True;
      end;
    end
    else if Kind = DirAdminsOnly then
    begin
      Result := 'it gives access to ' + Sid;
      Exit;
    end
    else if (Mask and MaskWrite) <> 0 then
    begin
      { Only the two Users grants of DirUsersModify may write; read-only entries are fine for anyone. }
      if (Kind = DirUsersModify) and (Sid = 'BU') and (Pos('IO', Flags) > 0) and (Mask = MaskModify) then
        HasContents := True
      else if (Kind = DirUsersModify) and (Sid = 'BU') and (Flags = '') and ((Mask or MaskCreateHere) = MaskCreateHere) then
        HasCreate := True
      else
      begin
        Result := 'it lets ' + Sid + ' change it';
        Exit;
      end;
    end;
  end;

  if not (HasSystem and HasAdmins) then
    Result := 'SYSTEM or Administrators lack full control'
  else if (Kind = DirUsersModify) and not (HasContents and HasCreate) then
    Result := 'Users cannot write in it';
end;

function SddlForKind(Kind: Integer): String;
begin
  case Kind of
    DirRoot: Result := SddlRoot;
    DirUsersModify: Result := SddlUsersModify;
  else
    Result := SddlAdminsOnly;
  end;
end;

{ Creates the folder with its final owner and DACL in one step. Returns '' or an error message. }
function CreateSecureDir(const Path: String; Kind: Integer): String;
var
  Attributes, Descriptor, Value: Cardinal;
  Sddl: String;
  Created: Boolean;
  Attempt, Error: Integer;
begin
  Result := '';
  Created := False;
  Error := 0;
  for Attempt := 1 to 2 do
  begin
    { First with owner Administrators; if this account may not assign that owner, with its default owner (an
      elevated administrator or SYSTEM), which the check below accepts as well. }
    if Attempt = 1 then
      Sddl := 'O:BA' + SddlForKind(Kind)
    else
      Sddl := SddlForKind(Kind);
    Descriptor := 0;
    if ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, SddlRevision1, Descriptor, 0) = 0 then
    begin
      Result := Format('Internal error: invalid security descriptor (%d).', [DLLGetLastError]);
      Exit;
    end;
    try
      { SECURITY_ATTRIBUTES (32-bit): nLength, lpSecurityDescriptor, bInheritHandle. }
      Attributes := LocalAlloc($40 { LPTR }, 12);
      if Attributes = 0 then
      begin
        Result := 'Out of memory.';
        Exit;
      end;
      try
        Value := 12;
        CopyCardinalToMemory(Attributes, Value, 4);
        Value := Descriptor;
        CopyCardinalToMemory(Attributes + 4, Value, 4);
        Created := CreateDirectoryW(Path, Attributes) <> 0;
        if not Created then
          Error := DLLGetLastError;
      finally
        LocalFree(Attributes);
      end;
    finally
      LocalFree(Descriptor);
    end;
    if Created or (Error <> ErrorInvalidOwner) then
      Break;
  end;

  if not Created then
  begin
    Result := Format('Could not create the folder "%s" (error %d).', [Path, Error]);
    Exit;
  end;
  Result := CheckDirSecurity(Path, Kind);
  if Result <> '' then
  begin
    RemoveDir(Path);
    Result := 'The new folder "' + Path + '" did not get the expected access rights: ' + Result + '.';
  end
  else
    Log('Created "' + Path + '" with its access rights.');
end;

{ An existing folder is kept only if it has exactly the owner and ACL the installer would give it; anything else
  (left by a program run before the install, half-configured, or planted by a user) is moved aside and replaced.
  Kept folders are never re-ACLed, so no ACL is ever pushed onto files a user may have placed in them. }
function EnsureDataDir(const Path: String; Kind: Integer; var Created: Boolean): String;
var
  Reason, Aside: String;
begin
  Result := '';
  Created := False;
  if GetFileAttributesW(Path) <> AttrInvalid then
  begin
    Reason := CheckDirSecurity(Path, Kind);
    if Reason = '' then
      Exit;
    Aside := Path + ' (untrusted ' + GetDateTimeString('yyyymmdd-hhnnss', '-', '-') + ')';
    Log('Moving "' + Path + '" aside to "' + Aside + '": ' + Reason + '.');
    if not RenameFile(Path, Aside) then
    begin
      Result := 'The folder "' + Path + '" cannot be used (' + Reason + ') and could not be moved aside. '
        + 'Close the programs that use it, or remove it, and run the installer again.';
      Exit;
    end;
  end;
  Result := CreateSecureDir(Path, Kind);
  Created := Result = '';
end;

{ A root created just now can only contain what this setup created in it. }
function VerifyFreshRoot(const Root: String): String;
var
  Find: TFindRec;
  Name, Sddl, Owner: String;
begin
  Result := '';
  if FindFirst(Root + '\*', Find) then
  try
    repeat
      Name := Find.Name;
      if (Name = '.') or (Name = '..') then
        Continue;
      if (CompareText(Name, 'runs') <> 0) and (CompareText(Name, 'logs') <> 0)
        and (CompareText(Name, 'requests') <> 0) and (CompareText(Name, 'update') <> 0) then
      begin
        Result := 'Unexpected item "' + Name + '" in the new folder "' + Root + '".';
        Exit;
      end;
      Owner := '';
      if GetSddl(Root + '\' + Name, Sddl) and (Pos('O:', Sddl) = 1) and (Pos('D:', Sddl) > 0) then
        Owner := NormalizeSid(Copy(Sddl, 3, Pos('D:', Sddl) - 3));
      if not IsTrustedSid(Owner) then
      begin
        Result := '"' + Root + '\' + Name + '" is not owned by SYSTEM or Administrators.';
        Exit;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

function PrepareDataFolders: String;
var
  Root: String;
  FreshRoot, Created: Boolean;
begin
  Root := DataRoot;
  Result := EnsureDataDir(Root, DirRoot, FreshRoot);
  if Result = '' then Result := EnsureDataDir(Root + '\runs', DirUsersModify, Created);
  if Result = '' then Result := EnsureDataDir(Root + '\logs', DirUsersModify, Created);
  if Result = '' then Result := EnsureDataDir(Root + '\requests', DirUsersModify, Created);
  if Result = '' then Result := EnsureDataDir(Root + '\update', DirAdminsOnly, Created);
  if (Result = '') and FreshRoot then
    Result := VerifyFreshRoot(Root);
end;

{ ---------- update state (spec 11a, R31) ---------- }

{ Phase back to Idle; ErrorText (plain text, no quotes) is shown by the settings window and tray helper. }
procedure ResetUpdateState(const ErrorText: String);
var
  Path, Json: String;
begin
  Path := DataRoot + '\update-state.json';
  if not DirExists(DataRoot) then
    Exit;
  if (ErrorText = '') and not FileExists(Path) then
    Exit;
  Json := '{' + #13#10 + '  "phase": "Idle"';
  if ErrorText <> '' then
    Json := Json + ',' + #13#10 + '  "error": "' + ErrorText + '"';
  Json := Json + #13#10 + '}' + #13#10;
  if not SaveStringToFile(Path, Json, False) then
    Log('Could not write ' + Path);
end;

{ A failed or cancelled setup: no updating.flag may be left behind (it would hold up every AE run), the settings
  window must not keep showing "Installing", and whatever this setup stopped is started again. }
procedure RecoverFromFailedInstall;
var
  StateFile: String;
  State: AnsiString;
begin
  if RecoveryDone then
    Exit;
  RecoveryDone := True;
  Log('The installation did not complete; restoring the previous state.');

  StateFile := DataRoot + '\update-state.json';
  if not UpdatingFlagExisted then
    UpdatingFlagExisted := FileExists(DataRoot + '\updating.flag');
  DeleteFile(DataRoot + '\updating.flag');
  State := '';
  if FileExists(StateFile) then
    LoadStringFromFile(StateFile, State);
  if UpdatingFlagExisted or (Pos('Installing', State) > 0) then
    ResetUpdateState('Update failed - see the installer log.');

  if ServiceCreatedByUs then
    Sc('delete ' + ServiceName) { a first install that did not complete: no service without a program }
  else if ServiceStoppedByUs then
    StartService;
  if TraysClosed then
    StartTray;
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
  PrepareStarted := True;
  UpdatingFlagExisted := FileExists(DataRoot + '\updating.flag');
  StopService;
  CloseSettingsApps;
  Result := PrepareDataFolders;
  if Result = '' then
    Result := InstallVCRedist;
#ifdef FailPrepare
  if Result = '' then
    Result := 'Test build: this installer always fails before copying files (FailPrepare).';
#endif
  if Result = '' then
    Result := ConfigureService;
  if Result <> '' then
  begin
    Log('Cannot install: ' + Result);
    RecoverFromFailedInstall;
  end;
end;

function NeedRestart: Boolean;
begin
  Result := VCRedistNeedsRestart;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { R31: the flag goes before the service starts, or the service could see a fresh flag and wait. }
    DeleteFile(DataRoot + '\updating.flag');
    ResetUpdateState('');
    if ServiceDisabled then
      Log('The service is disabled; not starting it.')
    else if not StartService then
    begin
      ServiceStartFailed := True;
      ResetUpdateState('The watcher service did not start after the installation - see the Windows event log.');
      SuppressibleMsgBox('Illux Image Tool was installed, but the watcher service did not start. '
        + 'See the Windows event log and the logs in ' + DataRoot + '\logs.', mbError, MB_OK, IDOK);
    end;
    StartTray;
    InstallCompleted := True;
  end;
end;

{ Called only when setup ran to completion; a service that did not start is reported to the caller (the updater). }
function GetCustomSetupExitCode: Integer;
begin
  if ServiceStartFailed then
    Result := ExitServiceNotStarted
  else
    Result := 0;
end;

{ Setup ended without completing after it had begun changing things (stopped the service, for instance). }
procedure DeinitializeSetup;
begin
  if PrepareStarted and not InstallCompleted then
    RecoverFromFailedInstall;
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
