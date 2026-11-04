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
    Absolute directory to install into. Defaults to %LOCALAPPDATA%\memoria.

.PARAMETER NoPathUpdate
    Do not add the install directory to the user PATH.

.PARAMETER PathVariable
    The user environment variable to add the directory to. Only tests change this.

.EXAMPLE
    pwsh -File install.ps1 -Version v1.2.3
#>
param(
    [string]$Version,
    [string]$InstallDir,
    [switch]$NoPathUpdate,
    [string]$PathVariable = 'Path'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Repo = 'mggarofalo/memoria'
$Binary = 'memoria.exe'
$MaxUnpackedBytes = 134217728

function Write-Info([string]$Message) {
    [Console]::Error.WriteLine("[memoria] $Message")
}

function Test-AbsolutePath([string]$Path) {
    if ([IO.Path]::DirectorySeparatorChar -eq '\') {
        # IsPathRooted also accepts \dir and C:dir, which depend on the current drive.
        return $Path -cmatch '\A([A-Za-z]:[\\/]|\\\\)'
    }
    return $Path.StartsWith('/')
}

# Adds a directory to a user variable without damaging it. The usual
# [Environment]::GetEnvironmentVariable returns the expanded value, and writing that
# back destroys every %VAR% entry and turns REG_EXPAND_SZ into REG_SZ.
function Add-ToUserPath([string]$Directory, [string]$Name) {
    $Key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
    try {
        $Current = $Key.GetValue($Name, '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if (($Current -split ';') -contains $Directory) {
            return
        }
        $Updated = if ($Current) { "$($Current.TrimEnd(';'));$Directory" } else { $Directory }
        $Key.SetValue($Name, $Updated, [Microsoft.Win32.RegistryValueKind]::ExpandString)
    } finally {
        $Key.Dispose()
    }
    # Writing the registry directly tells nobody. Setting a variable through this API
    # broadcasts the change, so new terminals see the new value. PowerShell turns $null
    # into an empty string here, which would leave the variable behind, so it is then
    # deleted from the registry.
    $RefreshName = 'MEMORIA_INSTALL_REFRESH'
    [Environment]::SetEnvironmentVariable($RefreshName, '1', 'User')
    $Key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
    try {
        $Key.DeleteValue($RefreshName, $false)
    } finally {
        $Key.Dispose()
    }
    Write-Info "Added $Directory to your user PATH. Restart your terminal to pick it up."
}

# Unpacks only the one expected entry, and refuses anything else about it that
# would let the archive write elsewhere or fill a disk.
function Expand-Binary([string]$ArchivePath, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $Zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $Entries = @($Zip.Entries | Where-Object { $_.FullName -ceq $Binary })
        if ($Entries.Count -eq 0) {
            throw "The archive has no $Binary."
        }
        if ($Entries.Count -gt 1) {
            throw "The archive names $Binary more than once."
        }
        $Entry = $Entries[0]
        if ($Entry.Length -gt $MaxUnpackedBytes) {
            throw 'The archive unpacks to more than the size limit.'
        }
        $In = $Entry.Open()
        $Out = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try {
            $Buffer = New-Object byte[] 81920
            $Total = 0L
            while (($Read = $In.Read($Buffer, 0, $Buffer.Length)) -gt 0) {
                $Total += $Read
                if ($Total -gt $MaxUnpackedBytes) {
                    throw 'The archive unpacks to more than the size limit.'
                }
                $Out.Write($Buffer, 0, $Read)
            }
        } finally {
            $Out.Dispose()
            $In.Dispose()
        }
    } finally {
        $Zip.Dispose()
    }
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
if (-not (Test-AbsolutePath $InstallDir)) {
    throw 'InstallDir must be an absolute path.'
}

if (-not $Version) {
    Write-Info 'Finding the latest release...'
    $Version = (Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest").tag_name
}
if ($Version -cnotmatch '\Av(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(-[0-9A-Za-z.-]{1,64})?\z') {
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

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $Target = Join-Path $InstallDir $Binary
    $Previous = Join-Path $InstallDir ".$Binary.old"
    # A random name, created only if nothing is there, so nothing planted is followed.
    $Staged = Join-Path $InstallDir ".$Binary.$([IO.Path]::GetRandomFileName())"
    try {
        Expand-Binary $ArchivePath $Staged

        # A running exe cannot be overwritten or deleted, but it can be renamed. The
        # leftover is removed by the next install or self-update.
        if (Test-Path -LiteralPath $Previous) {
            Remove-Item -LiteralPath $Previous -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $Target) {
            Move-Item -LiteralPath $Target -Destination $Previous -Force
        }
        try {
            Move-Item -LiteralPath $Staged -Destination $Target -Force
        } catch {
            if (Test-Path -LiteralPath $Previous) {
                Move-Item -LiteralPath $Previous -Destination $Target -Force
            }
            throw
        }
        if (Test-Path -LiteralPath $Previous) {
            Remove-Item -LiteralPath $Previous -Force -ErrorAction SilentlyContinue
        }
    } finally {
        if (Test-Path -LiteralPath $Staged) {
            Remove-Item -LiteralPath $Staged -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Info "Installed $Version to $Target"

    if (-not $NoPathUpdate) {
        Add-ToUserPath $InstallDir $PathVariable
    }
    Write-Info 'Next: memoria auth login --api-url https://your-server'
} finally {
    Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
}
