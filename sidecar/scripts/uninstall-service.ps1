<#
.SYNOPSIS
    Stop and remove the memoria-qmd NSSM service. Requires an elevated shell.
#>
[CmdletBinding()]
param([string]$ServiceName = "memoria-qmd")

$ErrorActionPreference = "Stop"
if (-not (Get-Command nssm -ErrorAction SilentlyContinue)) {
    throw "NSSM is not on PATH."
}
nssm stop $ServiceName confirm 2>$null | Out-Null
nssm remove $ServiceName confirm
Write-Host "Removed $ServiceName"
