<#
.SYNOPSIS
  Runs "qpdf --check" on every PDF in a folder (CI only: qpdf is an independent PDF reader, installed on the runner).
.DESCRIPTION
  Fails (exit 1) when the folder holds no PDF, or qpdf reports an error (exit code 2). Warnings (exit code 3) are
  shown in the job log as GitHub warnings and do not fail the run.
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
$errors = 0; $warnings = 0
foreach ($file in $files) {
    Write-Host "--- qpdf --check $($file.Name)"
    $output = & $qpdf --check $file.FullName 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Host $output
    if ($code -eq 2) { Write-Host "::error::qpdf --check reported errors in $($file.Name)"; $errors++ }
    elseif ($code -eq 3) { Write-Host "::warning::qpdf --check reported warnings for $($file.Name)"; $warnings++ }
    elseif ($code -ne 0) { Write-Host "::error::qpdf exited with $code for $($file.Name)"; $errors++ }
}
Write-Host "qpdf checked $($files.Count) PDF(s): $errors with errors, $warnings with warnings only."
if ($errors -gt 0) { exit 1 }
exit 0
