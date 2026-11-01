<#
.SYNOPSIS
    Install memoria-qmd as a Windows service with NSSM so it survives reboots
    and keeps the models warm.

.DESCRIPTION
    Builds the sidecar, then registers an NSSM service that runs
    `node dist/main.js` from the sidecar directory with the given environment.
    Requires an elevated PowerShell and NSSM on PATH (winget install NSSM.NSSM).

    The shared secret is read from -Secret or the MEMORIA_QMD_SECRET environment
    variable and stored in the service's environment block (readable by local
    administrators, which is the trust boundary this host already has).

.EXAMPLE
    .\scripts\install-service.ps1 -Host 192.168.1.20 -Secret (Get-Content .\secret.txt -Raw)
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "memoria-qmd",
    [Alias("Host")][string]$BindHost = "127.0.0.1",
    [int]$Port = 8282,
    [string]$Secret = $env:MEMORIA_QMD_SECRET,
    [string]$DbPath = $env:MEMORIA_QMD_DB_PATH,
    [string]$LogDir = "",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$sidecar = Split-Path -Parent $PSScriptRoot

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell."
}
if (-not (Get-Command nssm -ErrorAction SilentlyContinue)) {
    throw "NSSM is not on PATH. Install it with: winget install NSSM.NSSM"
}
$node = (Get-Command node -ErrorAction Stop).Source
$loopback = $BindHost -in @("127.0.0.1", "localhost", "::1") -or $BindHost -like "127.*"
if (-not $loopback -and [string]::IsNullOrWhiteSpace($Secret)) {
    throw "A shared secret is required when binding $BindHost. Pass -Secret or set MEMORIA_QMD_SECRET."
}
if (-not [string]::IsNullOrWhiteSpace($Secret) -and $Secret.Trim().Length -lt 16) {
    throw "The secret must be at least 16 characters."
}

if (-not $NoBuild) {
    Push-Location $sidecar
    try {
        npm ci --no-fund --no-audit
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
    } finally {
        Pop-Location
    }
}

if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $env:ProgramData "memoria-qmd\logs"
}
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$existing = nssm status $ServiceName 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Service $ServiceName exists; stopping and updating it."
    nssm stop $ServiceName confirm | Out-Null
} else {
    nssm install $ServiceName $node (Join-Path $sidecar "dist\main.js")
    if ($LASTEXITCODE -ne 0) { throw "nssm install failed" }
}

nssm set $ServiceName Application $node
nssm set $ServiceName AppParameters ('"' + (Join-Path $sidecar "dist\main.js") + '"')
nssm set $ServiceName AppDirectory $sidecar
nssm set $ServiceName DisplayName "Memoria QMD sidecar"
nssm set $ServiceName Description "REST sidecar over the QMD SDK for Memoria. Keeps the search models warm."
nssm set $ServiceName Start SERVICE_AUTO_START
nssm set $ServiceName AppStdout (Join-Path $LogDir "memoria-qmd.out.log")
nssm set $ServiceName AppStderr (Join-Path $LogDir "memoria-qmd.err.log")
nssm set $ServiceName AppRotateFiles 1
nssm set $ServiceName AppRotateBytes 10485760
nssm set $ServiceName AppStopMethodSkip 0
nssm set $ServiceName AppStopMethodConsole 15000
nssm set $ServiceName AppExit Default Restart
nssm set $ServiceName AppRestartDelay 5000

$envBlock = @(
    "MEMORIA_QMD_HOST=$BindHost",
    "MEMORIA_QMD_PORT=$Port",
    "MEMORIA_QMD_KEEP_WARM=1",
    "MEMORIA_QMD_WARMUP=1",
    "MEMORIA_QMD_LOG_LEVEL=info"
)
if (-not [string]::IsNullOrWhiteSpace($Secret)) { $envBlock += "MEMORIA_QMD_SECRET=$($Secret.Trim())" }
if (-not [string]::IsNullOrWhiteSpace($DbPath)) { $envBlock += "MEMORIA_QMD_DB_PATH=$DbPath" }
# The service runs as LocalSystem by default, whose profile is not the user's.
# Point QMD at the user's cache so it finds the index and models.
$userHome = $env:USERPROFILE
$envBlock += "XDG_CACHE_HOME=$(Join-Path $userHome '.cache')"
$envBlock += "XDG_CONFIG_HOME=$(Join-Path $userHome '.config')"
$envBlock += "USERPROFILE=$userHome"
$envBlock += "HOME=$userHome"
nssm set $ServiceName AppEnvironmentExtra $envBlock

nssm start $ServiceName
if ($LASTEXITCODE -ne 0) { throw "nssm start failed; check $LogDir" }
Write-Host "Installed and started $ServiceName on http://${BindHost}:$Port (logs: $LogDir)"
Write-Host "Probe it with: Invoke-RestMethod http://${BindHost}:$Port/health"
