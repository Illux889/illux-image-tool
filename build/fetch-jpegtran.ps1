<#
.SYNOPSIS
  Fetches jpegtran.exe (libjpeg-turbo 3.2.0, vc-x64 release) into third_party/libjpeg-turbo/.
.DESCRIPTION
  Downloads the official installer, verifies a pinned SHA-256 and extracts only the files the
  tool needs (bin\jpegtran.exe, bin\jpeg62.dll, doc\LICENSE.md, doc\README.ijg) using 7-Zip.
  Idempotent: does nothing if all files are already present. Requires 7-Zip (winget install 7zip.7zip).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$Version  = '3.2.0'
$Url      = "https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/$Version/libjpeg-turbo-$Version-vc-x64.exe"
# Computed locally from the downloaded 2,361,008-byte asset (GitHub publishes no checksum for it).
$Sha256   = '662761D8BA8DAE04AEC74023EBAECEB856C2B56B9B59CFD180759D26300DDA42'
$Wanted   = @('bin\jpegtran.exe', 'bin\jpeg62.dll', 'doc\LICENSE.md', 'doc\README.ijg')

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Target   = Join-Path $RepoRoot 'third_party\libjpeg-turbo'

$Missing = $Wanted | Where-Object { -not (Test-Path (Join-Path $Target (Split-Path -Leaf $_))) }
if (-not $Missing) {
    Write-Host "jpegtran already present in $Target"
    return
}

$SevenZip = @(
    (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
    (Get-Command 7z -ErrorAction SilentlyContinue | ForEach-Object Source)
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $SevenZip) {
    throw '7-Zip not found. Install it with: winget install 7zip.7zip'
}

$Work = Join-Path ([IO.Path]::GetTempPath()) ('illux-jpegtran-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $Work | Out-Null
try {
    $Installer = Join-Path $Work "libjpeg-turbo-$Version-vc-x64.exe"
    Write-Host "Downloading $Url"
    $prev = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $Url -OutFile $Installer -UseBasicParsing } finally { $ProgressPreference = $prev }

    $Actual = (Get-FileHash -Path $Installer -Algorithm SHA256).Hash
    if ($Actual -ne $Sha256) {
        throw "SHA-256 mismatch for $Url`n  expected $Sha256`n  actual   $Actual"
    }

    $Extract = Join-Path $Work 'x'
    & $SevenZip x $Installer "-o$Extract" -y @Wanted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7z extraction failed (exit code $LASTEXITCODE)" }

    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    foreach ($rel in $Wanted) {
        $src = Join-Path $Extract $rel
        if (-not (Test-Path $src)) { throw "Expected file missing from installer: $rel" }
        Copy-Item -Path $src -Destination (Join-Path $Target (Split-Path -Leaf $rel)) -Force
    }
    Write-Host "jpegtran $Version installed into $Target"
}
finally {
    Remove-Item -Recurse -Force -Path $Work -ErrorAction SilentlyContinue
}
