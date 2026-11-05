<#
.SYNOPSIS
  Runs "qpdf --check" on every PDF in a folder (CI only: qpdf is an independent PDF reader, installed on the runner).
.DESCRIPTION
  Fails (exit 1) when the folder holds no PDF, or qpdf exits with anything but 0: errors (2) and warnings (3, a repair such as a rebuilt
  cross-reference table) both fail the run.
#>
param([Parameter(Mandatory)][string]$Folder)
$ErrorActionPreference = 'Stop'

$qpdf = (Get-Command qpdf -ErrorAction SilentlyContinue).Source
if (-not $qpdf) {
    $qpdf = Get-ChildItem -Path "$env:ProgramFiles\qpdf*\bin\qpdf.exe", "$env:ProgramData\chocolatey\lib\qpdf\tools\*\bin\qpdf.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $qpdf) { throw 'qpdf was not found; install it first (choco install qpdf).' }
& $qpdf --version | Select-Object -First 1 | Write-Host

$files = @(Get-ChildItem -LiteralPath $Folder -Filter '*.pdf' -File -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) { Write-Host "::error::No PDF files in $Folder to check"; exit 1 }
$errors = 0
foreach ($file in $files) {
    Write-Host "--- qpdf --check $($file.Name)"
    $ErrorActionPreference = 'Continue' # qpdf writes warnings to stderr; they are not exceptions
    $output = & $qpdf --check $file.FullName 2>&1 | ForEach-Object { "$_" } | Out-String
    $code = $LASTEXITCODE
    Write-Host $output
    # Any exit code other than 0 fails: 3 means qpdf repaired or recovered something (e.g. a rebuilt cross-reference table).
    if ($code -ne 0) { Write-Host "::error::qpdf --check exited with $code (2 = errors, 3 = warnings) for $($file.Name)"; $errors++ }
}
Write-Host "qpdf checked $($files.Count) PDF(s): $errors failed."
if ($errors -gt 0) { exit 1 }
exit 0
