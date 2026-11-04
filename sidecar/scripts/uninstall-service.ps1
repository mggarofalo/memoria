<#
.SYNOPSIS
    Stop and remove the memoria-qmd service, its installed files and its secret.
    Logs stay. Requires an elevated shell; -WhatIf prints the steps and changes
    nothing.

.DESCRIPTION
    Pass the same data paths the install granted access to as -DataPath, so the
    service account's permissions come off them while its name still resolves.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = "memoria-qmd",
    [string]$InstallDir = "",
    [string]$StateDir = "",
    [string[]]$DataPath = @()
)

$ErrorActionPreference = "Stop"
$sidecar = Split-Path -Parent $PSScriptRoot
# Inside a release package, or beside this directory in a checkout.
$module = Join-Path $sidecar "deploy/windows/MemoriaService.psm1"
if (-not (Test-Path -LiteralPath $module)) { $module = Join-Path (Split-Path -Parent $sidecar) "deploy/windows/MemoriaService.psm1" }
Import-Module $module -Force

if ([string]::IsNullOrWhiteSpace($InstallDir)) { $InstallDir = Join-Path $env:ProgramFiles "Memoria\$ServiceName" }
if ([string]::IsNullOrWhiteSpace($StateDir)) { $StateDir = Join-Path $env:ProgramData $ServiceName }

$nssm = Join-Path $InstallDir "nssm.exe"
if (-not $WhatIfPreference -and -not (Test-Path -LiteralPath $nssm)) {
    $found = Get-Command nssm -ErrorAction SilentlyContinue
    if (-not $found) { throw "NSSM was found neither in $InstallDir nor on PATH." }
    $nssm = $found.Source
}

$plan = New-MemoriaUninstallPlan -ServiceName $ServiceName -Nssm $nssm -InstallDir $InstallDir -SecretDir (Join-Path $StateDir "secret") -DataPaths $DataPath
if ($WhatIfPreference) {
    Show-MemoriaPlan -Plan $plan
    return
}
Invoke-MemoriaPlan -Plan $plan
Write-Host "Removed $ServiceName. Logs remain in $StateDir\logs."
