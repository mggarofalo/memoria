<#
.SYNOPSIS
    Stop and remove the memoria-worker Windows service. Run from an elevated PowerShell.
#>
[CmdletBinding()]
param([string]$ServiceName = "memoria-worker")

$ErrorActionPreference = "Stop"
if (-not (Get-Command nssm -ErrorAction SilentlyContinue)) {
    throw "NSSM is not on PATH."
}
nssm stop $ServiceName confirm | Out-Null
nssm remove $ServiceName confirm
if ($LASTEXITCODE -ne 0) { throw "nssm remove failed" }
Write-Host "Removed $ServiceName. Logs remain in $env:ProgramData\memoria-worker\logs."
