<#
.SYNOPSIS
    Install memoria-worker as a Windows service with NSSM, for a GPU desktop.

.DESCRIPTION
    Copies the worker with its own node.exe and nssm.exe under Program Files,
    where only administrators can change it, and registers a service that runs
    as the virtual account NT SERVICE\<ServiceName>. Requires an elevated
    PowerShell, NSSM on PATH (winget install NSSM.NSSM), Node 22.18+, and a CUDA
    build of llama-server. Run it with -WhatIf first: that prints every step and
    changes nothing, and does not need elevation.

    The worker has no runtime npm dependencies and runs from source, so there is
    no install or build step.

    The token comes from -TokenFile or the MEMORIA_WORKER_TOKEN environment
    variable, so it never appears on a command line. It is written to a file that
    only Administrators and the service account can read, and the service reads
    it at startup through MEMORIA_WORKER_TOKEN_FILE. It is not stored in the
    service's registry environment, which local users can generally read.

    llama-server must already be somewhere only administrators can change,
    because the service runs it. The script refuses it otherwise.

    GPU access under this account has not been confirmed: run the smoke script
    after installing. docs/deployment.md lists what could not be checked without
    installing.

.EXAMPLE
    .\scripts\install-service.ps1 -BindHost 192.168.1.20 -LlamaServer "C:\Program Files\llama\llama-server.exe" -TokenFile .\token.txt -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = "memoria-worker",
    [string]$BindHost = "127.0.0.1",
    # The worker serves plain http. For binding beyond loopback on a private link such as WireGuard.
    [switch]$AllowPlaintextLan,
    [int]$Port = 8383,
    [string]$TokenFile = "",
    [Parameter(Mandatory = $true)][string]$LlamaServer,
    [string]$ModelDir = "",
    [string]$InstallDir = "",
    [string]$StateDir = "",
    [string]$LogDir = ""
)

$ErrorActionPreference = "Stop"
$worker = Split-Path -Parent $PSScriptRoot
# Inside a release package, or beside this directory in a checkout.
$module = Join-Path $worker "deploy/windows/MemoriaService.psm1"
if (-not (Test-Path -LiteralPath $module)) { $module = Join-Path (Split-Path -Parent $worker) "deploy/windows/MemoriaService.psm1" }
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
    if (-not (Test-Path -LiteralPath $LlamaServer -PathType Leaf)) {
        throw "llama-server was not found at $LlamaServer."
    }
    # DLLs beside the binary load with it, so its directory matters as much as the file.
    $llamaDir = Split-Path -Parent $LlamaServer
    $writers = @(Get-MemoriaNonAdminWriters -Path $LlamaServer) + @(Get-MemoriaNonAdminWriters -Path $llamaDir)
    if ($writers.Count -gt 0) {
        throw "llama-server at $LlamaServer can be changed by: $(($writers | Select-Object -Unique) -join ', '). Move it somewhere only administrators can write to, such as Program Files."
    }
}

$token = Get-MemoriaSecretValue -SourceFile $TokenFile -EnvValue $env:MEMORIA_WORKER_TOKEN -Label "token"
if (-not (Test-MemoriaLoopback -BindHost $BindHost) -and $null -eq $token) {
    throw "A token is required when binding $BindHost. Pass -TokenFile or set MEMORIA_WORKER_TOKEN."
}
if (-not (Test-MemoriaLoopback -BindHost $BindHost) -and -not $AllowPlaintextLan) {
    throw "The worker serves plain http, so its token and every text it embeds would cross the network in clear. Use a private link such as WireGuard and pass -AllowPlaintextLan to say so. See docs/deployment.md."
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) { $InstallDir = Join-Path $env:ProgramFiles "Memoria\$ServiceName" }
if ([string]::IsNullOrWhiteSpace($StateDir)) { $StateDir = Join-Path $env:ProgramData $ServiceName }
if ([string]::IsNullOrWhiteSpace($LogDir)) { $LogDir = Join-Path $StateDir "logs" }
if ([string]::IsNullOrWhiteSpace($ModelDir)) { $ModelDir = Join-Path $env:USERPROFILE ".cache\qmd\models" }
$secretPath = Join-Path $StateDir "secret\token.txt"

$environment = @(
    "MEMORIA_WORKER_HOST=$BindHost",
    "MEMORIA_WORKER_ALLOW_PLAINTEXT_LAN=$(if ($AllowPlaintextLan) { '1' } else { '0' })",
    "MEMORIA_WORKER_PORT=$Port",
    "MEMORIA_WORKER_LLAMA_SERVER=$LlamaServer",
    "MEMORIA_WORKER_MODEL_DIR=$ModelDir"
)
if ($null -ne $token) { $environment += "MEMORIA_WORKER_TOKEN_FILE=$secretPath" }

$nodeSource = if ($dryRun) { "node.exe" } else { (Get-Command node -ErrorAction Stop).Source }
$nssmSource = if ($dryRun) { "nssm.exe" } else { (Get-Command nssm -ErrorAction Stop).Source }
$exists = Test-MemoriaServiceExists -ServiceName $ServiceName

$plan = New-MemoriaInstallPlan -Spec @{
    ServiceName = $ServiceName
    DisplayName = "Memoria inference worker"
    Description = "Supervises llama-server for QMD's models and serves them to the Memoria sidecar."
    SourceDir = $worker
    Entry = "src\main.ts"
    CopyItems = @("src", "package.json")
    InstallDir = $InstallDir
    StateDir = $StateDir
    LogDir = $LogDir
    SecretPath = $secretPath
    HasSecret = ($null -ne $token)
    NodeSource = $nodeSource
    NssmSource = $nssmSource
    Environment = $environment
    ReadPaths = @($ModelDir, (Split-Path -Parent $LlamaServer))
    ModifyPaths = @()
    # llama-server children must not outlive the worker: they would hold the VRAM and the ports.
    KillProcessTree = $true
    Exists = $exists
    BuildSteps = @()
    # The install has no .git, so this is the only way it can report a version.
    PostCopySteps = @(New-MemoriaExeStep -Exe "node" -Arguments @((Join-Path $worker "scripts/write-version.ts"), (Join-Path $InstallDir "src")))
}

if ($dryRun) {
    Show-MemoriaPlan -Plan $plan
    return
}
Invoke-MemoriaPlan -Plan $plan -SecretValue $token
Write-Host "Installed and started $ServiceName on http://${BindHost}:$Port (logs: $LogDir)"
Write-Host "Check it with: npm run smoke, with MEMORIA_WORKER_URL and MEMORIA_WORKER_TOKEN set"
