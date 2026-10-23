<#
.SYNOPSIS
  End-to-end test of the installer on a throw-away Windows machine (GitHub Actions runner). Needs administrator rights.
.DESCRIPTION
  NEVER run this on a real PC: it installs and uninstalls Illux Image Tool, creates a local test user, writes into
  C:\ProgramData\Illux Image Tool and registers a scheduled task.

  1. Plants an "untrusted" (user-owned) data folder, then installs -Installer silently.
  2. Checks the service, files, version, shortcuts, Run key, Event Log source, ProgramData owner/ACLs (also by
     writing as a standard user) and that the untrusted folder was moved aside.
  3. Runs both AE scripts end to end on a generated JPEG (image output and PDF output).
  4. Sets up a watched folder, plants updating.flag + an "Installing" update state, then upgrades silently to
     -UpgradeInstaller as SYSTEM (as the updater in the service does): new version, service restarted,
     config.json unchanged, flag removed, state Idle, the watched folder still works.
  5. Uninstalls silently: service, Run key, Event Log source and program files gone, ProgramData kept.
  Exits 1 if any check failed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$UpgradeInstaller,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$UpgradeVersion,
    [Parameter(Mandatory)][string]$WorkDir
)

$ErrorActionPreference = 'Stop'
$App      = Join-Path $env:ProgramFiles 'Illux Image Tool'
$Data     = Join-Path $env:ProgramData 'Illux Image Tool'
$Service  = 'IlluxImageToolWatcher'
$Cli      = Join-Path $App 'IlluxImageTool.exe'
$StartMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Illux Image Tool'
$RunKey   = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$EventKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\Illux Image Tool'
$script:Failures = New-Object System.Collections.Generic.List[string]

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The smoke test needs administrator rights.'
}
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$UpgradeInstaller = (Resolve-Path -LiteralPath $UpgradeInstaller).Path

function Check($Condition, [string]$Message) {
    if ([bool]$Condition) { Write-Host "PASS  $Message" }
    else { Write-Host "::error::FAIL  $Message"; $script:Failures.Add($Message) }
}

function Section([string]$Title) { Write-Host "`n=== $Title ===" }

function Get-ServiceState {
    $s = Get-Service -Name $Service -ErrorAction SilentlyContinue
    if ($s) { $s.Status.ToString() } else { 'Missing' }
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return [bool](& $Condition)
}

function Get-ServicePid { (Get-CimInstance Win32_Service -Filter "Name='$Service'").ProcessId }

function Show-Log([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        Write-Host "::group::$Path"
        Get-Content -LiteralPath $Path | Write-Host
        Write-Host '::endgroup::'
    }
}

function Start-AndWait([string]$FilePath, $ArgumentList) {
    # Not Start-Process -Wait: Windows PowerShell then also waits for child processes, such as the tray helper the
    # installer starts, which never exit.
    $p = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -PassThru -NoNewWindow
    $null = $p.Handle # keeps the handle so ExitCode is available after the exit
    if (-not $p.WaitForExit(900000)) { throw "$FilePath did not finish within 15 minutes." }
    return $p.ExitCode
}

function Invoke-Installer([string]$Path, [string]$LogName) {
    $log = Join-Path $WorkDir $LogName
    $code = Start-AndWait $Path @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"")
    Show-Log $log
    return $code
}

function Sid([string]$Value) { New-Object Security.Principal.SecurityIdentifier($Value) }
$SidSystem = 'S-1-5-18'; $SidAdmins = 'S-1-5-32-544'; $SidUsers = 'S-1-5-32-545'
$WriteRights = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'

function Get-Rules([string]$Path, [bool]$IncludeInherited) {
    (Get-Acl -LiteralPath $Path).GetAccessRules($true, $IncludeInherited, [Security.Principal.SecurityIdentifier])
}

function Test-DataAcls([string]$When) {
    $acl = Get-Acl -LiteralPath $Data
    Check ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq $SidAdmins) "$($When): data folder owned by Administrators"
    Check $acl.AreAccessRulesProtected "$($When): data folder ACL is protected (no inheritance from ProgramData)"
    $rules = Get-Rules $Data $true
    $sids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    Check (($sids -join ',') -eq (@($SidSystem, $SidAdmins, $SidUsers) | Sort-Object) -join ',') "$($When): data folder grants only SYSTEM, Administrators, Users (found $($sids -join ', '))"
    foreach ($sid in $SidSystem, $SidAdmins) {
        Check ([bool]($rules | Where-Object { $_.IdentityReference.Value -eq $sid -and $_.AccessControlType -eq 'Allow' -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq [Security.AccessControl.FileSystemRights]::FullControl })) "$($When): $sid has full control of the data folder"
    }
    $users = @($rules | Where-Object { $_.IdentityReference.Value -eq $SidUsers })
    Check ($users.Count -gt 0 -and -not ($users | Where-Object { ($_.FileSystemRights -band $WriteRights) -ne 0 })) "$($When): Users can only read the data folder"

    foreach ($sub in 'runs', 'logs', 'requests') {
        $explicit = @(Get-Rules (Join-Path $Data $sub) $false | Where-Object {
            $_.IdentityReference.Value -eq $SidUsers -and $_.AccessControlType -eq 'Allow' -and
            ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify })
        Check ($explicit.Count -gt 0) "$($When): explicit Users modify grant on $sub\"
    }

    $update = Join-Path $Data 'update'
    Check ((Get-Acl -LiteralPath $update).AreAccessRulesProtected)"$($When): update\ ACL is protected"
    $updateSids = @(Get-Rules $update $true | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
    Check (($updateSids -join ',') -eq (@($SidSystem, $SidAdmins) | Sort-Object) -join ',') "$($When): update\ grants only SYSTEM and Administrators (found $($updateSids -join ', '))"
}

# --- a standard user, to check the ACLs by actually writing ---
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

public static class SmokeImpersonation
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);

    // "ok" when the user could create (and delete) the file, otherwise the exception type.
    public static string TryWrite(string user, string password, string path, bool overwriteExisting)
    {
        SafeAccessTokenHandle token;
        if (!LogonUser(user, ".", password, 3 /* network */, 0, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            return WindowsIdentity.RunImpersonated(token, () =>
            {
                try
                {
                    if (overwriteExisting) { using (var s = new FileStream(path, FileMode.Open, FileAccess.Write)) { } return "ok"; }
                    File.WriteAllText(path, "x");
                    File.Delete(path);
                    return "ok";
                }
                catch (Exception e) { return e.GetType().Name; }
            });
        }
    }
}
'@
$TestUser = 'illuxsmoke'
$TestPassword = 'Aa1!' + [Guid]::NewGuid().ToString('N')
New-LocalUser -Name $TestUser -Password (ConvertTo-SecureString $TestPassword -AsPlainText -Force) -AccountNeverExpires -Description 'Illux Image Tool smoke test' | Out-Null

function Test-UserWrites([string]$When) {
    $cases = @(
        @{ Path = (Join-Path $Data 'user-test.txt');            Expect = $false; Existing = $false; Name = 'create a file in the data folder' },
        @{ Path = (Join-Path $Data 'update\user-test.txt');     Expect = $false; Existing = $false; Name = 'create a file in update\' },
        @{ Path = (Join-Path $Data 'config.json');              Expect = $false; Existing = $true;  Name = 'write config.json' },
        @{ Path = (Join-Path $Data 'requests\user-test.txt');   Expect = $true;  Existing = $false; Name = 'create a file in requests\' },
        @{ Path = (Join-Path $Data 'runs\user-test.txt');       Expect = $true;  Existing = $false; Name = 'create a file in runs\' },
        @{ Path = (Join-Path $Data 'logs\user-test.txt');       Expect = $true;  Existing = $false; Name = 'create a file in logs\' }
    )
    foreach ($c in $cases) {
        if ($c.Existing -and -not (Test-Path -LiteralPath $c.Path)) { Check $false "$($When): $($c.Path) exists for the write test"; continue }
        $result = [SmokeImpersonation]::TryWrite($TestUser, $TestPassword, $c.Path, $c.Existing)
        $verb = if ($c.Expect) { 'can' } else { 'cannot' }
        Check (($result -eq 'ok') -eq $c.Expect) "$($When): a standard user $verb $($c.Name) (result: $result)"
    }
}

# --- a small test image: 120 x 80 px JPEG at 96 ppi ---
Add-Type -AssemblyName System.Drawing
function New-TestJpeg([string]$Path) {
    $bmp = New-Object System.Drawing.Bitmap(120, 80)
    try {
        $bmp.SetResolution(96, 96)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::SteelBlue)
        $g.FillEllipse([System.Drawing.Brushes]::Orange, 20, 10, 80, 60)
        $g.Dispose()
        $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    }
    finally { $bmp.Dispose() }
}

function Get-ImageInfo([string]$Path) {
    $img = [System.Drawing.Image]::FromFile($Path)
    try { [pscustomobject]@{ Width = $img.Width; Height = $img.Height; Ppi = [math]::Round($img.HorizontalResolution) } }
    finally { $img.Dispose() }
}

function Test-PdfHeader([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $bytes = [IO.File]::ReadAllBytes($Path)
    return [Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min(8, $bytes.Length)) -eq '%PDF-1.6'
}

$Smoke = Join-Path $WorkDir 'files'
Remove-Item -Recurse -Force -LiteralPath $Smoke -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Smoke | Out-Null
$TestJpeg = Join-Path $Smoke 'test.jpg'
New-TestJpeg $TestJpeg

# =====================================================================================================
Section 'Untrusted data folder before the first install'
if (Test-Path -LiteralPath $Data) { throw "$Data already exists on this runner." }
New-Item -ItemType Directory -Path $Data | Out-Null
Set-Content -LiteralPath (Join-Path $Data 'config.json') -Value '{ "planted": true }'
& icacls.exe $Data /setowner $identity.Name | Out-Null
$plantedOwner = (Get-Acl -LiteralPath $Data).GetOwner([Security.Principal.SecurityIdentifier]).Value
Check ($plantedOwner -eq $identity.User.Value) "planted data folder is owned by $($identity.Name) (found $plantedOwner)"

# =====================================================================================================
Section "Install $Version"
$code = Invoke-Installer $Installer 'install.log'
Check ($code -eq 0) "installer exit code 0 (got $code)"
if ($code -ne 0) { throw 'The first install failed; nothing else can be checked.' }

Check (Wait-Until { (Get-ServiceState) -eq 'Running' } 60) "service is running (state: $(Get-ServiceState))"
$svc = Get-CimInstance Win32_Service -Filter "Name='$Service'"
Check ($svc.StartMode -eq 'Auto') "service starts automatically (start mode: $($svc.StartMode))"
Check ($svc.DisplayName -eq 'Illux Image Tool Watcher') "service display name (found '$($svc.DisplayName)')"
Check ($svc.PathName -eq "`"$App\IlluxImageToolWatcher.exe`"") "service binary path (found $($svc.PathName))"
Check (-not [string]::IsNullOrWhiteSpace($svc.Description)) 'service has a description'
$failure = (& sc.exe qfailure $Service) -join "`n"
Check ($failure -match 'RESTART') "service restarts on failure`n$failure"

$cliVersion = (& $Cli --version | Out-String).Trim()
Check ($cliVersion -eq $Version) "CLI --version is $Version (got '$cliVersion')"

foreach ($rel in 'IlluxImageTool.exe', 'IlluxImageToolWatcher.exe', 'IlluxImageToolSettings.exe', 'tools\jpegtran\jpegtran.exe',
                 'tools\jpegtran\jpeg62.dll', 'scripts\ae\IlluxImageTool.bat', 'scripts\ae\IlluxImageTool.ps1', 'docs\ae-setup.md',
                 'LICENSE.txt', 'System.Diagnostics.EventLog.Messages.dll', 'unins000.exe') {
    Check (Test-Path -LiteralPath (Join-Path $App $rel)) "installed $rel"
}
Check (Test-Path -LiteralPath (Join-Path $StartMenu 'Illux Image Tool Settings.lnk')) 'Start menu: Illux Image Tool Settings'
Check (Test-Path -LiteralPath (Join-Path $StartMenu 'AE setup guide.lnk')) 'Start menu: AE setup guide'
$run = (Get-ItemProperty -Path $RunKey -Name IlluxImageToolTray -ErrorAction SilentlyContinue).IlluxImageToolTray
Check ($run -eq "`"$App\IlluxImageToolSettings.exe`" --tray") "Run key IlluxImageToolTray (found '$run')"
Check ([Diagnostics.EventLog]::SourceExists('Illux Image Tool') -and [Diagnostics.EventLog]::LogNameFromSourceName('Illux Image Tool', '.') -eq 'Application') 'Event Log source "Illux Image Tool" in the Application log'

Section 'Data folder'
$aside = @(Get-ChildItem -LiteralPath $env:ProgramData -Directory -Filter 'Illux Image Tool (untrusted *')
Check ($aside.Count -eq 1) "the planted data folder was moved aside (found: $($aside.Name -join ', '))"
Check (-not (Test-Path -LiteralPath (Join-Path $Data 'config.json'))) 'the planted config.json is not in the new data folder'
foreach ($sub in 'runs', 'logs', 'requests', 'update') { Check (Test-Path -LiteralPath (Join-Path $Data $sub)) "data folder has $sub\" }
# The settings window (elevated) is the normal writer of config.json; here the test writes it as administrator.
$watch = Join-Path $WorkDir 'watch'
foreach ($d in 'In', 'Out', 'Error') { New-Item -ItemType Directory -Force -Path (Join-Path $watch $d) | Out-Null }
$config = @{ folders = @(@{ name = 'smoke'; in = "$watch\In"; out = "$watch\Out"; error = "$watch\Error"; parameters = @() }); pollSeconds = 1 } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $Data 'config.json'), $config)
$configHash = (Get-FileHash -LiteralPath (Join-Path $Data 'config.json')).Hash
Test-DataAcls 'after install'
Test-UserWrites 'after install'

Section 'Tray helper'
$tray = @(Get-CimInstance Win32_Process -Filter "Name='IlluxImageToolSettings.exe'")
if ($tray.Count -gt 0) { Write-Host "Tray helper started by the installer: $($tray.CommandLine -join ' | ')" }
else {
    Write-Host '::warning::The installer did not leave a tray helper running (no interactive session?); starting one to test that it loads.'
    Start-Process -FilePath (Join-Path $App 'IlluxImageToolSettings.exe') -ArgumentList '--tray' | Out-Null
    Start-Sleep -Seconds 5
    $tray = @(Get-CimInstance Win32_Process -Filter "Name='IlluxImageToolSettings.exe'")
}
Check ($tray.Count -gt 0) 'IlluxImageToolSettings.exe --tray starts and keeps running'

# =====================================================================================================
Section 'AE scripts end to end'
$out1 = Join-Path $Smoke 'out-ps1'; New-Item -ItemType Directory -Force -Path $out1 | Out-Null
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $App 'scripts\ae\IlluxImageTool.ps1') $TestJpeg $out1 | Write-Host
$code = $LASTEXITCODE
Check ($code -eq 0) "PowerShell AE script exit code 0 (got $code)"
$result = Join-Path $out1 'test.jpg'
if (Test-Path -LiteralPath $result) {
    $info = Get-ImageInfo $result
    Check ($info.Ppi -eq 200 -and $info.Width -eq 250 -and $info.Height -eq 167)"AE output upsampled to 200 ppi, 250 x 167 px (got $($info.Ppi) ppi, $($info.Width) x $($info.Height))"
} else { Check $false "AE script wrote $result" }

$out2 = Join-Path $Smoke 'out-bat'; New-Item -ItemType Directory -Force -Path $out2 | Out-Null
# cmd /c strips the outer quotes, leaving:  "<bat>" "<input>" "<output folder>" output=pdf
$bat = Join-Path $App 'scripts\ae\IlluxImageTool.bat'
$code = Start-AndWait cmd.exe "/d /c `"`"$bat`" `"$TestJpeg`" `"$out2`" output=pdf`""
Check ($code -eq 0) "Batch AE script exit code 0 (got $code)"
Check (Test-PdfHeader (Join-Path $out2 'test.pdf')) 'Batch AE script wrote test.pdf with a %PDF-1.6 header'

# =====================================================================================================
Section "Upgrade to $UpgradeVersion as SYSTEM (like the updater)"
Set-Content -LiteralPath (Join-Path $Data 'updating.flag') -Value $UpgradeVersion
[IO.File]::WriteAllText((Join-Path $Data 'update-state.json'), "{ `"availableVersion`": `"$UpgradeVersion`", `"phase`": `"Installing`" }")
$pidBefore = Get-ServicePid
$upgradeLog = Join-Path $WorkDir 'upgrade.log'
$taskName = 'IlluxSmokeUpgrade'
$action = New-ScheduledTaskAction -Execute $UpgradeInstaller -Argument "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$upgradeLog`""
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Settings $settings -User 'NT AUTHORITY\SYSTEM' -RunLevel Highest -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 3
$finished = Wait-Until { (Get-ScheduledTask -TaskName $taskName).State -ne 'Running' } 900
$taskResult = (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
Show-Log $upgradeLog
Check ($finished -and $taskResult -eq 0) "upgrade installer as SYSTEM finished with exit code 0 (finished: $finished, result: $taskResult)"

Check (Wait-Until { (Get-ServiceState) -eq 'Running' } 60) "service is running after the upgrade (state: $(Get-ServiceState))"
$pidAfter = Get-ServicePid
Check ($pidAfter -and $pidAfter -ne $pidBefore) "service was restarted (process $pidBefore -> $pidAfter)"
$cliVersion = (& $Cli --version | Out-String).Trim()
Check ($cliVersion -eq $UpgradeVersion) "CLI --version is $UpgradeVersion after the upgrade (got '$cliVersion')"
Check ((Get-FileHash -LiteralPath (Join-Path $Data 'config.json')).Hash -eq $configHash) 'config.json unchanged by the upgrade'
Check (-not (Test-Path -LiteralPath (Join-Path $Data 'updating.flag'))) 'updating.flag removed'
$state = Get-Content -Raw -LiteralPath (Join-Path $Data 'update-state.json') | ConvertFrom-Json
Check ($state.phase -eq 'Idle') "update-state.json phase is Idle (found '$($state.phase)')"
Test-DataAcls 'after upgrade'
Test-UserWrites 'after upgrade'
if (Test-Path -LiteralPath $upgradeLog) {
    $trayLine = Select-String -LiteralPath $upgradeLog -Pattern 'tray helper' | Select-Object -Last 1
    Write-Host "Tray restart after the SYSTEM upgrade: $($trayLine.Line)"
}

Section 'Watched folder after the upgrade'
Copy-Item -LiteralPath $TestJpeg -Destination (Join-Path $watch 'In\watched.jpg')
$watchedOut = Join-Path $watch 'Out\watched.jpg'
Check (Wait-Until { Test-Path -LiteralPath $watchedOut } 90) 'the service converted a file dropped into the watched In folder'
if (Test-Path -LiteralPath $watchedOut) {
    Start-Sleep -Seconds 1
    $info = Get-ImageInfo $watchedOut
    Check ($info.Ppi -eq 200) "watched-folder output is 200 ppi (got $($info.Ppi))"
}
$originals = Join-Path $watch 'Originals\watched.jpg'
Check (Wait-Until { Test-Path -LiteralPath $originals } 30) 'the original was moved to Originals'
Show-Log (Get-ChildItem -LiteralPath (Join-Path $Data 'logs') -Filter 'watcher*.log' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName

# =====================================================================================================
Section 'Uninstall'
$uninstallLog = Join-Path $WorkDir 'uninstall.log'
$code = Start-AndWait (Join-Path $App 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$uninstallLog`"")
Check ($code -eq 0) "uninstaller exit code 0 (got $code)"
# The uninstaller finishes in a copy of itself; wait for it to remove its own folder.
Check (Wait-Until { -not (Test-Path -LiteralPath (Join-Path $App 'unins000.exe')) } 120) 'uninstaller finished'
Show-Log $uninstallLog
Check (Wait-Until { (Get-ServiceState) -eq 'Missing' } 60) "service removed (state: $(Get-ServiceState))"
Check (-not (Test-Path -LiteralPath (Join-Path $App 'IlluxImageTool.exe'))) 'program files removed'
Check ($null -eq (Get-ItemProperty -Path $RunKey -Name IlluxImageToolTray -ErrorAction SilentlyContinue)) 'Run key removed'
Check (-not (Test-Path -LiteralPath $EventKey)) 'Event Log source removed'
Check (-not (Test-Path -LiteralPath $StartMenu)) 'Start menu folder removed'
Check (-not (Get-CimInstance Win32_Process -Filter "Name='IlluxImageToolSettings.exe'")) 'no tray helper left running'
Check ((Get-FileHash -LiteralPath (Join-Path $Data 'config.json')).Hash -eq $configHash) 'ProgramData config.json kept'

Remove-LocalUser -Name $TestUser -ErrorAction SilentlyContinue

Write-Host ''
if ($script:Failures.Count -gt 0) {
    Write-Host "$($script:Failures.Count) check(s) failed:"
    $script:Failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host 'All installer checks passed.'
