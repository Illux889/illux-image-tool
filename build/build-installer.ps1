<#
.SYNOPSIS
  Builds the Illux Image Tool installer: artifacts/installer/IlluxImageTool-Setup-<version>.exe and its .sha256 file.
.DESCRIPTION
  1. Fetches jpegtran (build/fetch-jpegtran.ps1).
  2. Downloads Microsoft's VC++ 2015-2022 x64 redistributable and checks its Microsoft signature (R11).
  3. Publishes the command-line tool, the watcher service and the settings window self-contained for win-x64 into
     one shared folder, artifacts/publish/. A file that two apps publish differently must be a .dll; the higher
     version wins (a framework facade vs. the Windows Desktop implementation). Anything else stops the build.
  4. Compiles installer/IlluxImageTool.iss with Inno Setup 6.4 or newer (ISCC.exe).
  5. Writes the checksum file as "<sha256 hex>  <installer file name>".
  Inno Setup is never installed by this script; pass -Iscc or put ISCC.exe on the PATH.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Path of ISCC.exe; found on the PATH or in the usual Inno Setup 6 folders when not given.
    [string]$Iscc,

    # CI only: an installer that always fails before copying files, to test the recovery of a failed update.
    [switch]$FailPrepareForTest
)

$ErrorActionPreference = 'Stop'
$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot 'artifacts'
$Apps = @(
    @{ Name = 'Cli';      Project = 'src\Illux.ImageTool.Cli\Illux.ImageTool.Cli.csproj' },
    @{ Name = 'Watcher';  Project = 'src\Illux.ImageTool.Watcher\Illux.ImageTool.Watcher.csproj' },
    @{ Name = 'Settings'; Project = 'src\Illux.ImageTool.Settings\Illux.ImageTool.Settings.csproj' }
)

function Find-Iscc {
    if ($Iscc) {
        if (-not (Test-Path -LiteralPath $Iscc)) { throw "ISCC.exe not found at '$Iscc'." }
        return $Iscc
    }
    $candidates = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | ForEach-Object Source),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $found = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $found) { throw 'Inno Setup 6 (ISCC.exe) not found. Install Inno Setup 6.4 or newer, or pass -Iscc <path>.' }
    return $found
}

function Get-VcRedist {
    $dir = Join-Path $Artifacts 'redist'
    $file = Join-Path $dir 'vc_redist.x64.exe'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    if (-not (Test-Path -LiteralPath $file)) {
        $url = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
        Write-Host "Downloading $url"
        $partial = "$file.part"
        $prev = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
        try { Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing } finally { $ProgressPreference = $prev }
        Move-Item -LiteralPath $partial -Destination $file -Force
    }

    # The file is checked every time, also when it was downloaded by an earlier build.
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '<none>' }
    if ($signature.Status -ne 'Valid' -or $subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        Remove-Item -LiteralPath $file -Force
        throw "vc_redist.x64.exe is not validly signed by Microsoft (status $($signature.Status), signer $subject); deleted it."
    }
    Write-Host "vc_redist.x64.exe $((Get-Item -LiteralPath $file).VersionInfo.FileVersion), signed by $subject"
}

function Get-VersionKey([string]$path) {
    $assembly = try { [Reflection.AssemblyName]::GetAssemblyName($path).Version } catch { $null }
    $fileVersion = (Get-Item -LiteralPath $path).VersionInfo
    $file = New-Object Version($fileVersion.FileMajorPart, $fileVersion.FileMinorPart, $fileVersion.FileBuildPart, $fileVersion.FilePrivatePart)
    [pscustomobject]@{ Assembly = $(if ($assembly) { $assembly } else { New-Object Version(0, 0, 0, 0) }); File = $file }
}

function Publish-Apps {
    $staging = Join-Path $Artifacts 'publish-apps'
    $publish = Join-Path $Artifacts 'publish'
    Remove-Item -Recurse -Force -LiteralPath $staging, $publish -ErrorAction SilentlyContinue

    foreach ($app in $Apps) {
        $out = Join-Path $staging $app.Name
        Write-Host "Publishing $($app.Name) $Version"
        & dotnet publish (Join-Path $RepoRoot $app.Project) -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=false "-p:Version=$Version" -o $out -nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish of $($app.Name) failed (exit code $LASTEXITCODE)." }
    }

    # Merge into one folder: relative path -> chosen source file.
    $chosen = [ordered]@{}
    foreach ($app in $Apps) {
        $root = Join-Path $staging $app.Name
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
            $rel = $file.FullName.Substring($root.Length + 1)
            if (-not $chosen.Contains($rel)) { $chosen[$rel] = $file.FullName; continue }

            $current = $chosen[$rel]
            if ((Get-FileHash -LiteralPath $current).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) { continue }
            if ([IO.Path]::GetExtension($rel) -ne '.dll') {
                throw "Publish conflict: '$rel' differs between the apps ($current vs $($file.FullName))."
            }
            $a = Get-VersionKey $current
            $b = Get-VersionKey $file.FullName
            $takeNew = ($b.Assembly -gt $a.Assembly) -or ($b.Assembly -eq $a.Assembly -and $b.File -gt $a.File)
            if ($takeNew) { $chosen[$rel] = $file.FullName }
            Write-Host ("  {0}: assembly {1}/file {2} vs {3}/{4} -> keeping {5}" -f $rel, $a.Assembly, $a.File, $b.Assembly, $b.File, $chosen[$rel].Substring($staging.Length + 1))
        }
    }

    foreach ($rel in $chosen.Keys) {
        $target = Join-Path $publish $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $chosen[$rel] -Destination $target
    }

    $required = @('IlluxImageTool.exe', 'IlluxImageToolWatcher.exe', 'IlluxImageToolSettings.exe', 'tools\jpegtran\jpegtran.exe', 'tools\jpegtran\jpeg62.dll')
    foreach ($rel in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $rel))) { throw "Published folder lacks $rel." }
    }
    Write-Host "Published $($chosen.Count) files into $publish"
}

& (Join-Path $PSScriptRoot 'fetch-jpegtran.ps1')
$compiler = Find-Iscc
Get-VcRedist
Publish-Apps

$installerDir = Join-Path $Artifacts 'installer'
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
$defines = @("/DMyVersion=$Version")
if ($FailPrepareForTest) { $defines += '/DFailPrepare' }
& $compiler /Qp @defines (Join-Path $RepoRoot 'installer\IlluxImageTool.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$name = "IlluxImageTool-Setup-$Version.exe"
$installer = Join-Path $installerDir $name
if (-not (Test-Path -LiteralPath $installer)) { throw "ISCC did not produce $installer." }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$installer.sha256", "$hash  $name`n", (New-Object Text.ASCIIEncoding))
Write-Host "Built $installer"
Write-Host "SHA-256 $hash"
