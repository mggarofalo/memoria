<#
.SYNOPSIS
    Install memoria-qmd as a Windows service with NSSM so it survives reboots
    and keeps the models warm.

.DESCRIPTION
    Builds the sidecar, copies it with its own node.exe and nssm.exe under
    Program Files, where only administrators can change it, and registers a
    service that runs as the virtual account NT SERVICE\<ServiceName>. Requires
    an elevated PowerShell and NSSM on PATH (winget install NSSM.NSSM). Run it
    with -WhatIf first: that prints every step and changes nothing, and does not
    need elevation.

    The shared secret comes from -SecretFile or the MEMORIA_QMD_SECRET
    environment variable, so it never appears on a command line. It is written
    to a file that only Administrators and the service account can read, and the
    service reads it at startup through MEMORIA_QMD_SECRET_FILE. It is not stored
    in the service's registry environment, which local users can generally read.

    The account needs access to the data, which this script grants on the paths
    it is given: read on the QMD config, change on the QMD cache (index and
    models) and on each -CollectionRoot. Those grants are the part to check after
    a first install; docs/deployment.md lists what could not be confirmed
    without installing.

.EXAMPLE
    .\scripts\install-service.ps1 -BindHost 192.168.1.20 -SecretFile .\secret.txt -CollectionRoot D:\corpus\sessions -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = "memoria-qmd",
    [Alias("Host")][string]$BindHost = "127.0.0.1",
    [int]$Port = 8282,
    [string]$SecretFile = "",
    [string]$DbPath = $env:MEMORIA_QMD_DB_PATH,
    [string]$InstallDir = "",
    [string]$StateDir = "",
    [string]$LogDir = "",
    [string]$CacheHome = "",
    [string]$ConfigHome = "",
    [string[]]$CollectionRoot = @(),
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$sidecar = Split-Path -Parent $PSScriptRoot
# Inside a release package, or beside this directory in a checkout.
$module = Join-Path $sidecar "deploy/windows/MemoriaService.psm1"
if (-not (Test-Path -LiteralPath $module)) { $module = Join-Path (Split-Path -Parent $sidecar) "deploy/windows/MemoriaService.psm1" }
Import-Module $module -Force

$dryRun = [bool]$WhatIfPreference
if (-not $dryRun) {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell."
    }
    if (-not (Get-Command nssm -ErrorAction SilentlyContinue)) {
        throw "NSSM is not on PATH. Install it with: winget install NSSM.NSSM"
    }
    if (-not (Test-MemoriaNssmVersion -VersionText ((& nssm version 2>&1) -join "`n"))) {
        Write-Warning "NSSM 2.24 fails to start services on current Windows. Use 2.24-101 or later from nssm.cc."
    }
}

$secret = Get-MemoriaSecretValue -SourceFile $SecretFile -EnvValue $env:MEMORIA_QMD_SECRET -Label "secret"
if (-not (Test-MemoriaLoopback -BindHost $BindHost) -and $null -eq $secret) {
    throw "A shared secret is required when binding $BindHost. Pass -SecretFile or set MEMORIA_QMD_SECRET."
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) { $InstallDir = Join-Path $env:ProgramFiles "Memoria\$ServiceName" }
if ([string]::IsNullOrWhiteSpace($StateDir)) { $StateDir = Join-Path $env:ProgramData $ServiceName }
if ([string]::IsNullOrWhiteSpace($LogDir)) { $LogDir = Join-Path $StateDir "logs" }
if ([string]::IsNullOrWhiteSpace($CacheHome)) { $CacheHome = Join-Path $env:USERPROFILE ".cache" }
if ([string]::IsNullOrWhiteSpace($ConfigHome)) { $ConfigHome = Join-Path $env:USERPROFILE ".config" }
$secretPath = Join-Path $StateDir "secret\secret.txt"
if ($CollectionRoot.Count -eq 0) {
    Write-Warning "No -CollectionRoot given. POST /documents and DELETE /documents will fail until the service account can change the collection directories."
}

$environment = @(
    "MEMORIA_QMD_HOST=$BindHost",
    "MEMORIA_QMD_PORT=$Port",
    "MEMORIA_QMD_KEEP_WARM=1",
    "MEMORIA_QMD_WARMUP=1",
    "MEMORIA_QMD_LOG_LEVEL=info",
    # QMD reads these for its index, models and collection config. The service
    # account's own profile holds none of them.
    "XDG_CACHE_HOME=$CacheHome",
    "XDG_CONFIG_HOME=$ConfigHome"
)
if ($null -ne $secret) { $environment += "MEMORIA_QMD_SECRET_FILE=$secretPath" }
if (-not [string]::IsNullOrWhiteSpace($DbPath)) { $environment += "MEMORIA_QMD_DB_PATH=$DbPath" }

$modifyPaths = @((Join-Path $CacheHome "qmd")) + $CollectionRoot
if (-not [string]::IsNullOrWhiteSpace($DbPath)) { $modifyPaths += (Split-Path -Parent $DbPath) }

$buildSteps = @()
if (-not $NoBuild) {
    $buildSteps += New-MemoriaExeStep -Exe "npm" -Arguments @("ci", "--no-fund", "--no-audit") -WorkingDirectory $sidecar
    $buildSteps += New-MemoriaExeStep -Exe "npm" -Arguments @("run", "build") -WorkingDirectory $sidecar
}
$nodeSource = if ($dryRun) { "node.exe" } else { (Get-Command node -ErrorAction Stop).Source }
$nssmSource = if ($dryRun) { "nssm.exe" } else { (Get-Command nssm -ErrorAction Stop).Source }
$exists = Test-MemoriaServiceExists -ServiceName $ServiceName

$plan = New-MemoriaInstallPlan -Spec @{
    ServiceName = $ServiceName
    DisplayName = "Memoria QMD sidecar"
    Description = "REST sidecar over the QMD SDK for Memoria. Keeps the search models warm."
    SourceDir = $sidecar
    Entry = "dist\main.js"
    CopyItems = @("dist", "package.json", "package-lock.json")
    InstallDir = $InstallDir
    StateDir = $StateDir
    LogDir = $LogDir
    SecretPath = $secretPath
    HasSecret = ($null -ne $secret)
    NodeSource = $nodeSource
    NssmSource = $nssmSource
    Environment = $environment
    ReadPaths = @((Join-Path $ConfigHome "qmd"))
    ModifyPaths = $modifyPaths
    # NSSM's default, stated so it does not depend on one.
    KillProcessTree = $true
    Exists = $exists
    BuildSteps = $buildSteps
    # Runtime dependencies only, built against the node.exe copied alongside them.
    PostCopySteps = @(New-MemoriaExeStep -Exe "npm" -Arguments @("ci", "--omit=dev", "--no-fund", "--no-audit") -WorkingDirectory $InstallDir)
}

if ($dryRun) {
    Show-MemoriaPlan -Plan $plan
    return
}
Invoke-MemoriaPlan -Plan $plan -SecretValue $secret
Write-Host "Installed and started $ServiceName on http://${BindHost}:$Port (logs: $LogDir)"
Write-Host "Probe it with: Invoke-RestMethod http://${BindHost}:$Port/health"
