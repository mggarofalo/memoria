<#
.SYNOPSIS
    Install memoria-worker as a Windows service with NSSM, for a GPU desktop.

.DESCRIPTION
    Registers an NSSM service that runs `node src/main.ts` from the worker
    directory. Requires an elevated PowerShell, NSSM on PATH
    (winget install NSSM.NSSM), Node 22.18+, and a CUDA build of llama-server.

    The worker has no runtime npm dependencies and runs from source, so there is
    no install or build step.

    The token is read from -Token or MEMORIA_WORKER_TOKEN and stored in the
    service's environment block, which local administrators can read.

.EXAMPLE
    .\scripts\install-service.ps1 -BindHost 192.168.1.20 -LlamaServer C:\llama\llama-server.exe -Token $env:MEMORIA_WORKER_TOKEN
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "memoria-worker",
    [string]$BindHost = "127.0.0.1",
    [int]$Port = 8383,
    [string]$Token = $env:MEMORIA_WORKER_TOKEN,
    [Parameter(Mandatory = $true)][string]$LlamaServer,
    [string]$ModelDir = "",
    [string]$LogDir = ""
)

$ErrorActionPreference = "Stop"
$worker = Split-Path -Parent $PSScriptRoot

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell."
}
if (-not (Get-Command nssm -ErrorAction SilentlyContinue)) {
    throw "NSSM is not on PATH. Install it with: winget install NSSM.NSSM"
}
if (-not (Test-Path -LiteralPath $LlamaServer -PathType Leaf)) {
    throw "llama-server was not found at $LlamaServer."
}
$node = (Get-Command node -ErrorAction Stop).Source
$loopback = $BindHost -in @("127.0.0.1", "localhost", "::1") -or $BindHost -like "127.*"
if (-not $loopback -and [string]::IsNullOrWhiteSpace($Token)) {
    throw "A token is required when binding $BindHost. Pass -Token or set MEMORIA_WORKER_TOKEN."
}
if (-not [string]::IsNullOrWhiteSpace($Token) -and $Token.Trim().Length -lt 16) {
    throw "The token must be at least 16 characters."
}

if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $LogDir = Join-Path $env:ProgramData "memoria-worker\logs"
}
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$entry = Join-Path $worker "src\main.ts"
$null = nssm status $ServiceName 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Service $ServiceName exists; stopping and updating it."
    nssm stop $ServiceName confirm | Out-Null
} else {
    nssm install $ServiceName $node $entry
    if ($LASTEXITCODE -ne 0) { throw "nssm install failed" }
}

nssm set $ServiceName Application $node
nssm set $ServiceName AppParameters ('"' + $entry + '"')
nssm set $ServiceName AppDirectory $worker
nssm set $ServiceName DisplayName "Memoria inference worker"
nssm set $ServiceName Description "Supervises llama-server for QMD's models and serves them to the Memoria sidecar."
nssm set $ServiceName Start SERVICE_AUTO_START
nssm set $ServiceName AppStdout (Join-Path $LogDir "memoria-worker.out.log")
nssm set $ServiceName AppStderr (Join-Path $LogDir "memoria-worker.err.log")
nssm set $ServiceName AppRotateFiles 1
nssm set $ServiceName AppRotateBytes 10485760
nssm set $ServiceName AppStopMethodSkip 0
nssm set $ServiceName AppStopMethodConsole 15000
# llama-server children must not outlive the worker: they would hold the VRAM and the ports.
nssm set $ServiceName AppKillProcessTree 1
nssm set $ServiceName AppExit Default Restart
nssm set $ServiceName AppRestartDelay 5000

# The service runs as LocalSystem by default, whose profile is not the user's.
# Point the worker at the user's QMD cache so it finds the models.
if ([string]::IsNullOrWhiteSpace($ModelDir)) {
    $ModelDir = Join-Path $env:USERPROFILE ".cache\qmd\models"
}
$envBlock = @(
    "MEMORIA_WORKER_HOST=$BindHost",
    "MEMORIA_WORKER_PORT=$Port",
    "MEMORIA_WORKER_LLAMA_SERVER=$LlamaServer",
    "MEMORIA_WORKER_MODEL_DIR=$ModelDir"
)
if (-not [string]::IsNullOrWhiteSpace($Token)) { $envBlock += "MEMORIA_WORKER_TOKEN=$($Token.Trim())" }
nssm set $ServiceName AppEnvironmentExtra $envBlock

nssm start $ServiceName
if ($LASTEXITCODE -ne 0) { throw "nssm start failed; check $LogDir" }
Write-Host "Installed and started $ServiceName on http://${BindHost}:$Port (logs: $LogDir)"
Write-Host "Check it with: npm run smoke, with MEMORIA_WORKER_URL and MEMORIA_WORKER_TOKEN set"
