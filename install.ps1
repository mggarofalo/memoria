<#
.SYNOPSIS
    Installs the memoria CLI on Windows.

.DESCRIPTION
    Download this file and read it before running it. Do not pipe it into
    Invoke-Expression. See docs/cli.md for the steps, including how to check the
    attestation.

    The archive's SHA-256 is checked against the release's checksums.txt before
    anything is unpacked.

.PARAMETER Version
    Release to install, vX.Y.Z. Defaults to the latest.

.PARAMETER InstallDir
    Directory to install into. Defaults to %LOCALAPPDATA%\memoria.

.PARAMETER NoPathUpdate
    Do not add the install directory to the user PATH.

.EXAMPLE
    pwsh -File install.ps1 -Version v1.2.3
#>
param(
    [string]$Version,
    [string]$InstallDir,
    [switch]$NoPathUpdate
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Repo = 'mggarofalo/memoria'
$Binary = 'memoria.exe'

function Write-Info([string]$Message) {
    [Console]::Error.WriteLine("[memoria] $Message")
}

# Windows PowerShell 5.1 can default to older TLS versions.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

switch ($env:PROCESSOR_ARCHITECTURE) {
    'AMD64' { $Arch = 'amd64' }
    'ARM64' { $Arch = 'arm64' }
    default { throw "Unsupported architecture: $env:PROCESSOR_ARCHITECTURE" }
}

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA 'memoria'
}

if (-not $Version) {
    Write-Info 'Finding the latest release...'
    $Version = (Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest").tag_name
}
if ($Version -cnotmatch '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw 'Not a release version (expected vX.Y.Z).'
}

$Archive = "memoria_$($Version.Substring(1))_windows_$Arch.zip"
$Base = "https://github.com/$Repo/releases/download/$Version"

$WorkDir = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $WorkDir | Out-Null
try {
    $ArchivePath = Join-Path $WorkDir $Archive
    $ChecksumsPath = Join-Path $WorkDir 'checksums.txt'
    Write-Info "Downloading $Archive..."
    Invoke-WebRequest -Uri "$Base/$Archive" -OutFile $ArchivePath -UseBasicParsing
    Invoke-WebRequest -Uri "$Base/checksums.txt" -OutFile $ChecksumsPath -UseBasicParsing

    # Compare the name exactly: a regex or substring could find another asset's line.
    $Expected = $null
    foreach ($Line in Get-Content $ChecksumsPath) {
        $Fields = $Line -split '\s+', 2
        if ($Fields.Count -eq 2 -and $Fields[1].TrimStart('*') -ceq $Archive) {
            $Expected = $Fields[0]
            break
        }
    }
    if (-not $Expected) {
        throw "$Archive is not listed in checksums.txt"
    }
    $Actual = (Get-FileHash -Path $ArchivePath -Algorithm SHA256).Hash
    if ($Expected -ine $Actual) {
        throw "Checksum mismatch for $Archive; nothing was installed."
    }
    Write-Info 'Checksum verified.'

    $ExtractDir = Join-Path $WorkDir 'extract'
    Expand-Archive -Path $ArchivePath -DestinationPath $ExtractDir
    $Extracted = Join-Path $ExtractDir $Binary
    if (-not (Test-Path -LiteralPath $Extracted -PathType Leaf)) {
        throw "The archive has no $Binary."
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $Target = Join-Path $InstallDir $Binary
    $Previous = Join-Path $InstallDir ".$Binary.old"

    # A running exe cannot be overwritten or deleted, but it can be renamed. The
    # leftover is removed by the next install or self-update.
    if (Test-Path -LiteralPath $Previous) {
        Remove-Item -LiteralPath $Previous -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $Target) {
        Move-Item -LiteralPath $Target -Destination $Previous -Force
    }
    try {
        Move-Item -LiteralPath $Extracted -Destination $Target -Force
    } catch {
        if (Test-Path -LiteralPath $Previous) {
            Move-Item -LiteralPath $Previous -Destination $Target -Force
        }
        throw
    }
    if (Test-Path -LiteralPath $Previous) {
        Remove-Item -LiteralPath $Previous -Force -ErrorAction SilentlyContinue
    }
    Write-Info "Installed $Version to $Target"

    if (-not $NoPathUpdate) {
        $UserPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
        if (-not (($UserPath -split ';') -contains $InstallDir)) {
            $NewPath = if ($UserPath) { "$UserPath;$InstallDir" } else { $InstallDir }
            [Environment]::SetEnvironmentVariable('PATH', $NewPath, 'User')
            Write-Info "Added $InstallDir to your user PATH. Restart your terminal to pick it up."
        }
    }
    Write-Info 'Next: memoria auth login --api-url https://your-server'
} finally {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}
