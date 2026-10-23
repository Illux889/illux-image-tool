# Illux Image Tool - Esko Automation Engine wrapper (PowerShell script).
#
# AE gives this script:  1st = input files (joined by ";"),  2nd = output folder,  the rest = Script Parameters.
# It waits while an update is being installed, runs IlluxImageTool.exe and hands its exit code
# back to AE (0 = OK, 1 = Warning, 2 = Error).
#
# Test-only settings (never needed in production):
#   ILLUX_IMAGETOOL_EXE                 path of the exe to run instead of the installed one
#   ILLUX_IMAGETOOL_ROOT                folder that holds updating.flag instead of C:\ProgramData\Illux Image Tool
#   ILLUX_IMAGETOOL_WAIT_SECONDS        seconds between two checks of updating.flag (default 5)
#   ILLUX_IMAGETOOL_WAIT_MAX_SECONDS    seconds to wait for an update at most (default 600 = 10 minutes)
Param(
    [string]$Inputs,
    [string]$OutputFolder,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$exe = $env:ILLUX_IMAGETOOL_EXE
if (-not $exe) {
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $exe = Join-Path $programFiles 'Illux Image Tool\IlluxImageTool.exe'
}

$root = if ($env:ILLUX_IMAGETOOL_ROOT) { $env:ILLUX_IMAGETOOL_ROOT } else { Join-Path $env:ProgramData 'Illux Image Tool' }
$flag = Join-Path $root 'updating.flag'
$wait = if ($env:ILLUX_IMAGETOOL_WAIT_SECONDS) { [int]$env:ILLUX_IMAGETOOL_WAIT_SECONDS } else { 5 }
$waitMax = if ($env:ILLUX_IMAGETOOL_WAIT_MAX_SECONDS) { [int]$env:ILLUX_IMAGETOOL_WAIT_MAX_SECONDS } else { 600 }

# Wait while an update is being installed.
if (Test-Path -LiteralPath $flag) {
    Write-Output 'Waiting for the update of Illux Image Tool to finish...'
    $waited = 0
    while (Test-Path -LiteralPath $flag) {
        if ($waited -ge $waitMax) {
            Write-Output 'ERROR: update did not finish'
            exit 2
        }
        Start-Sleep -Seconds $wait
        $waited += $wait
    }
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Output "ERROR: IlluxImageTool.exe not found at `"$exe`""
    exit 2
}

# A trailing backslash would escape the closing quote of the argument; '\.' names the same folder.
if ($OutputFolder -and $OutputFolder.EndsWith('\')) { $OutputFolder += '.' }

$arguments = @('ae', $Inputs, $OutputFolder)
if ($Rest) { $arguments += $Rest }

$global:LASTEXITCODE = $null
try {
    & $exe @arguments
}
catch {
    # The exe exists but could not be started (damaged file, wrong platform, blocked).
    Write-Output "ERROR: $_"
    exit 2
}

# AE knows 0 = OK, 1 = Warning, 2 = Error; anything else (a crash, a missing DLL, no exit code at all) is an Error.
if ($LASTEXITCODE -eq 0 -or $LASTEXITCODE -eq 1) { exit $LASTEXITCODE }
exit 2
