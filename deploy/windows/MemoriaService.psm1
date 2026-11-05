<#
.SYNOPSIS
    Shared steps for the memoria-qmd and memoria-worker NSSM install scripts.

.DESCRIPTION
    An install is a plan: an ordered list of plain data steps that holds no
    secret value. New-MemoriaInstallPlan builds it and has no side effects, so
    -WhatIf can print exactly what an install would do, and tests can read it.
    Invoke-MemoriaPlan runs it.
#>

Set-StrictMode -Version 2.0

$script:AdministratorsSid = "S-1-5-32-544"
$script:SystemSid = "S-1-5-18"
$script:TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
$script:CreatorOwnerSid = "S-1-3-0"

function Get-MemoriaServiceAccount {
    param([Parameter(Mandatory = $true)][string]$ServiceName)
    return "NT SERVICE\$ServiceName"
}

<# False where there are no Windows services at all, so a dry run works on any host. #>
function Test-MemoriaServiceExists {
    param([Parameter(Mandatory = $true)][string]$ServiceName)
    if (-not (Get-Command Get-Service -ErrorAction SilentlyContinue)) { return $false }
    return $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
}

function Test-MemoriaLoopback {
    param([Parameter(Mandatory = $true)][string]$BindHost)
    return ($BindHost -in @("127.0.0.1", "localhost", "::1")) -or ($BindHost -like "127.*")
}

<#
    The secret to install: from a file the caller already holds, or from the
    environment. It is deliberately not a script parameter, because a parameter
    would put it on a command line. Returns $null when none is given.
#>
function Get-MemoriaSecretValue {
    param(
        [AllowEmptyString()][string]$SourceFile,
        [AllowEmptyString()][string]$EnvValue,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $value = $EnvValue
    if (-not [string]::IsNullOrWhiteSpace($SourceFile)) {
        if (-not (Test-Path -LiteralPath $SourceFile -PathType Leaf)) { throw "The $Label file was not found at $SourceFile." }
        $value = [IO.File]::ReadAllText($SourceFile)
    }
    if ([string]::IsNullOrWhiteSpace($value)) { return $null }
    $trimmed = $value.Trim()
    if ($trimmed.Contains("`n") -or $trimmed.Contains("`r")) { throw "The $Label must be one line." }
    if ($trimmed.Length -lt 16) { throw "The $Label must be at least 16 characters." }
    return $trimmed
}

<#
    NSSM 2.24, the last stable release, fails to start services on Windows 10
    Creators Update or newer. nssm.cc recommends 2.24-101 or later. NSSM can
    print UTF-16 when redirected, hence the NUL removal.
#>
function Test-MemoriaNssmVersion {
    param([AllowEmptyString()][string]$VersionText)
    $text = $VersionText -replace "`0", ""
    return -not ($text -match "(?m)\b2\.24\s+(32|64)-bit")
}

function New-MemoriaStep {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Description,
        [hashtable]$Data = @{}
    )
    return [pscustomobject]@{ Kind = $Kind; Description = $Description; Data = $Data }
}

function New-MemoriaExeStep {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$WorkingDirectory = "",
        [switch]$AllowFailure
    )
    $shown = ($Arguments | ForEach-Object { if ($_ -match "\s") { "`"$_`"" } else { $_ } }) -join " "
    return New-MemoriaStep -Kind "exe" -Description "$Exe $shown" -Data @{
        Exe = $Exe
        Arguments = $Arguments
        WorkingDirectory = $WorkingDirectory
        AllowFailure = [bool]$AllowFailure
    }
}

function New-MemoriaNssmSettingSteps {
    param([Parameter(Mandatory = $true)][hashtable]$Spec, [Parameter(Mandatory = $true)][string]$Nssm)
    $name = $Spec.ServiceName
    $entry = Join-Path $Spec.InstallDir $Spec.Entry
    $node = Join-Path $Spec.InstallDir "node.exe"
    $settings = @(
        @("Application", $node),
        @("AppParameters", ((@(if ($Spec.ContainsKey("NodeArguments")) { $Spec.NodeArguments }) + "`"$entry`"") -join " ")),
        @("AppDirectory", $Spec.InstallDir),
        @("DisplayName", $Spec.DisplayName),
        @("Description", $Spec.Description),
        @("Start", "SERVICE_AUTO_START"),
        @("AppStdout", (Join-Path $Spec.LogDir "$name.out.log")),
        @("AppStderr", (Join-Path $Spec.LogDir "$name.err.log")),
        # Append on start (NSSM's default, set so it does not depend on one), and
        # rotate at 10 MiB both at start and while running. NSSM never deletes
        # rotated files, so they need pruning.
        @("AppStdoutCreationDisposition", "4"),
        @("AppStderrCreationDisposition", "4"),
        @("AppRotateFiles", "1"),
        @("AppRotateOnline", "1"),
        @("AppRotateBytes", "10485760"),
        @("AppStopMethodSkip", "0"),
        @("AppStopMethodConsole", "15000"),
        @("AppKillProcessTree", $(if ($Spec.KillProcessTree) { "1" } else { "0" })),
        @("AppRestartDelay", "5000")
    )
    $steps = @($settings | ForEach-Object { New-MemoriaExeStep -Exe $Nssm -Arguments @("set", $name, $_[0], $_[1]) })
    $steps += New-MemoriaExeStep -Exe $Nssm -Arguments @("set", $name, "AppExit", "Default", "Restart")
    # This replaces the whole block, which also clears a secret an earlier install stored here.
    $steps += New-MemoriaExeStep -Exe $Nssm -Arguments (@("set", $name, "AppEnvironmentExtra") + @($Spec.Environment))
    return $steps
}

function New-MemoriaCopySteps {
    param([Parameter(Mandatory = $true)][hashtable]$Spec)
    $steps = @(New-MemoriaStep -Kind "ensure-dir" -Description "Create $($Spec.InstallDir)" -Data @{ Path = $Spec.InstallDir })
    $steps += New-MemoriaStep -Kind "copy" -Description "Copy node to $($Spec.InstallDir)" -Data @{ Source = $Spec.NodeSource; Destination = (Join-Path $Spec.InstallDir "node.exe") }
    # winget puts nssm.exe in a per-user directory, and the service runs whichever copy registered it.
    $steps += New-MemoriaStep -Kind "copy" -Description "Copy nssm to $($Spec.InstallDir)" -Data @{ Source = $Spec.NssmSource; Destination = (Join-Path $Spec.InstallDir "nssm.exe") }
    foreach ($item in $Spec.CopyItems) {
        $destination = Join-Path $Spec.InstallDir $item
        $steps += New-MemoriaStep -Kind "replace" -Description "Replace $destination" -Data @{ Source = (Join-Path $Spec.SourceDir $item); Destination = $destination }
    }
    return $steps
}

function New-MemoriaSecretSteps {
    param([Parameter(Mandatory = $true)][hashtable]$Spec, [Parameter(Mandatory = $true)][string]$Account)
    $secretDir = Split-Path -Parent $Spec.SecretPath
    $grants = @(
        @{ Principal = "*$script:AdministratorsSid"; Rights = "FullControl" },
        @{ Principal = $Account; Rights = "Read" }
    )
    if (-not $Spec.HasSecret) {
        return @(New-MemoriaStep -Kind "remove" -Description "Remove any earlier secret at $secretDir" -Data @{ Path = $secretDir })
    }
    return @(
        (New-MemoriaStep -Kind "secure-dir" -Description "Restrict $secretDir to Administrators and $Account" -Data @{ Path = $secretDir; Grants = $grants }),
        (New-MemoriaStep -Kind "write-secret" -Description "Write the secret to $($Spec.SecretPath)" -Data @{ Path = $Spec.SecretPath }),
        (New-MemoriaStep -Kind "secure-file" -Description "Restrict $($Spec.SecretPath) to Administrators and $Account" -Data @{ Path = $Spec.SecretPath; Grants = $grants }),
        (New-MemoriaStep -Kind "verify-acl" -Description "Verify nobody else can read $($Spec.SecretPath)" -Data @{ Path = $Spec.SecretPath; Allowed = @($script:AdministratorsSid, $Account) })
    )
}

function New-MemoriaGrantSteps {
    param([Parameter(Mandatory = $true)][hashtable]$Spec, [Parameter(Mandatory = $true)][string]$Account)
    $steps = @()
    foreach ($path in @($Spec.ReadPaths)) {
        $steps += New-MemoriaExeStep -Exe "icacls.exe" -Arguments @($path, "/grant", "${Account}:(OI)(CI)RX")
    }
    foreach ($path in @($Spec.ModifyPaths)) {
        $steps += New-MemoriaExeStep -Exe "icacls.exe" -Arguments @($path, "/grant", "${Account}:(OI)(CI)M")
    }
    return $steps
}

<#
    The whole install, in order. Nothing here touches the machine.

    $Spec keys: ServiceName, DisplayName, Description, SourceDir, Entry,
    CopyItems, InstallDir, StateDir, LogDir, SecretPath, HasSecret, NodeSource,
    NssmSource, Environment, ReadPaths, ModifyPaths, KillProcessTree, Exists,
    BuildSteps, PostCopySteps.
#>
function New-MemoriaInstallPlan {
    param([Parameter(Mandatory = $true)][hashtable]$Spec)
    $name = $Spec.ServiceName
    $account = Get-MemoriaServiceAccount -ServiceName $name
    $nssm = Join-Path $Spec.InstallDir "nssm.exe"

    $steps = @()
    $steps += @($Spec.BuildSteps)
    if ($Spec.Exists) {
        $steps += New-MemoriaExeStep -Exe "sc.exe" -Arguments @("stop", $name) -AllowFailure
        $steps += New-MemoriaStep -Kind "wait-stopped" -Description "Wait for $name to stop" -Data @{ ServiceName = $name }
    }
    $steps += New-MemoriaCopySteps -Spec $Spec
    $steps += @($Spec.PostCopySteps)
    $steps += New-MemoriaStep -Kind "verify-no-writers" -Description "Verify only administrators can change $($Spec.InstallDir)" -Data @{ Path = $Spec.InstallDir }

    if (-not $Spec.Exists) {
        $steps += New-MemoriaExeStep -Exe $nssm -Arguments @("install", $name, (Join-Path $Spec.InstallDir "node.exe"))
    }
    $steps += New-MemoriaNssmSettingSteps -Spec $Spec -Nssm $nssm

    # The account exists once the service does, so every ACL naming it comes after this.
    # sc.exe rather than `nssm set ObjectName`: NSSM documents that setting as needing a password.
    $steps += New-MemoriaExeStep -Exe "sc.exe" -Arguments @("sidtype", $name, "unrestricted")
    $steps += New-MemoriaExeStep -Exe "sc.exe" -Arguments @("config", $name, "obj=", $account)

    $steps += New-MemoriaExeStep -Exe "icacls.exe" -Arguments @($Spec.InstallDir, "/grant", "${account}:(OI)(CI)RX")
    $stateGrants = @(
        @{ Principal = "*$script:AdministratorsSid"; Rights = "FullControl" },
        @{ Principal = $account; Rights = "ReadAndExecute" }
    )
    $logGrants = @(
        @{ Principal = "*$script:AdministratorsSid"; Rights = "FullControl" },
        @{ Principal = $account; Rights = "Modify" }
    )
    $steps += New-MemoriaStep -Kind "secure-dir" -Description "Restrict $($Spec.StateDir) to Administrators and $account" -Data @{ Path = $Spec.StateDir; Grants = $stateGrants }
    $steps += New-MemoriaStep -Kind "secure-dir" -Description "Restrict $($Spec.LogDir) to Administrators and $account, which may write" -Data @{ Path = $Spec.LogDir; Grants = $logGrants }
    $steps += New-MemoriaSecretSteps -Spec $Spec -Account $account
    $steps += New-MemoriaGrantSteps -Spec $Spec -Account $account

    $steps += New-MemoriaExeStep -Exe "sc.exe" -Arguments @("start", $name)
    return $steps
}

<#
    Everything an install created, except the logs. Grants on data directories
    go first, while the account's name still resolves.
#>
function New-MemoriaUninstallPlan {
    param(
        [Parameter(Mandatory = $true)][string]$ServiceName,
        [Parameter(Mandatory = $true)][string]$Nssm,
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [Parameter(Mandatory = $true)][string]$SecretDir,
        [string[]]$DataPaths = @()
    )
    $account = Get-MemoriaServiceAccount -ServiceName $ServiceName
    $steps = @(New-MemoriaExeStep -Exe "sc.exe" -Arguments @("stop", $ServiceName) -AllowFailure)
    $steps += New-MemoriaStep -Kind "wait-stopped" -Description "Wait for $ServiceName to stop" -Data @{ ServiceName = $ServiceName }
    foreach ($path in $DataPaths) {
        $steps += New-MemoriaExeStep -Exe "icacls.exe" -Arguments @($path, "/remove", $account)
    }
    $steps += New-MemoriaExeStep -Exe $Nssm -Arguments @("remove", $ServiceName, "confirm")
    $steps += New-MemoriaStep -Kind "remove" -Description "Remove the secret at $SecretDir" -Data @{ Path = $SecretDir }
    $steps += New-MemoriaStep -Kind "remove" -Description "Remove $InstallDir" -Data @{ Path = $InstallDir }
    return $steps
}

function ConvertTo-MemoriaIdentity {
    param([Parameter(Mandatory = $true)][string]$Principal)
    if ($Principal.StartsWith("*")) { return New-Object Security.Principal.SecurityIdentifier($Principal.Substring(1)) }
    return New-Object Security.Principal.NTAccount($Principal)
}

function Get-MemoriaSidText {
    param([Parameter(Mandatory = $true)]$Identity)
    try {
        return $Identity.Translate([Security.Principal.SecurityIdentifier]).Value
    } catch {
        return [string]$Identity
    }
}

function Assert-MemoriaNotLink {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "$Path is a link. Remove it and run the install again."
        }
    }
}

<#
    Replace the path's whole DACL in one write, with inheritance off. ProgramData
    lets any user create folders, so this also takes ownership (when elevated)
    and refuses a link, in case someone created the path first.
#>
function Set-MemoriaRestrictedAcl {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable[]]$Grants,
        [switch]$TakeOwnership
    )
    Assert-MemoriaNotLink -Path $Path
    $isDirectory = Test-Path -LiteralPath $Path -PathType Container
    if ($isDirectory) {
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $inherit = [Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    } else {
        $acl = New-Object Security.AccessControl.FileSecurity
        $inherit = [Security.AccessControl.InheritanceFlags]::None
    }
    $acl.SetAccessRuleProtection($true, $false)
    if ($TakeOwnership) {
        $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier($script:AdministratorsSid)))
    }
    foreach ($grant in $Grants) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (ConvertTo-MemoriaIdentity -Principal $grant.Principal),
            [Security.AccessControl.FileSystemRights]$grant.Rights,
            $inherit,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Test-MemoriaWriteRights {
    param([Parameter(Mandatory = $true)][long]$Rights)
    # WriteData, AppendData, WriteExtendedAttributes, WriteAttributes, DeleteSubdirectoriesAndFiles,
    # Delete, ChangePermissions, TakeOwnership, and the generic ALL and WRITE that inherit-only rules
    # show as raw numbers, which the FileSystemRights names do not cover.
    $write = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
    return ($Rights -band $write) -ne 0
}

<# Principals other than $Allowed that have any allow rule on the path. Empty means the ACL is as restricted as intended. #>
function Get-MemoriaAclViolations {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Allowed
    )
    $allowedSids = @($Allowed | ForEach-Object {
        if ($_ -match "^S-\d") { $_ } else { Get-MemoriaSidText -Identity (New-Object Security.Principal.NTAccount($_)) }
    })
    $rules = (Get-Acl -LiteralPath $Path).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
    return @($rules | Where-Object { $_.AccessControlType -eq "Allow" -and $_.IdentityReference.Value -notin $allowedSids } |
        ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique)
}

<# Principals beyond the machine's own administrators who can change this path. Code run by a service must have none. #>
function Get-MemoriaNonAdminWriters {
    param([Parameter(Mandatory = $true)][string]$Path)
    $trusted = @($script:AdministratorsSid, $script:SystemSid, $script:TrustedInstallerSid, $script:CreatorOwnerSid)
    $rules = (Get-Acl -LiteralPath $Path).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
    return @($rules | Where-Object {
            $_.AccessControlType -eq "Allow" -and
            $_.IdentityReference.Value -notin $trusted -and
            (Test-MemoriaWriteRights -Rights ([long][int]$_.FileSystemRights))
        } | ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique)
}

function Invoke-MemoriaExeStep {
    param([Parameter(Mandatory = $true)]$Data)
    $moved = $false
    if ($Data.WorkingDirectory) {
        Push-Location -LiteralPath $Data.WorkingDirectory
        $moved = $true
    }
    try {
        $arguments = $Data.Arguments
        & $Data.Exe @arguments | Out-Host
        if ($LASTEXITCODE -ne 0 -and -not $Data.AllowFailure) {
            throw "$($Data.Exe) exited with code $LASTEXITCODE"
        }
    } finally {
        if ($moved) { Pop-Location }
    }
}

function Wait-MemoriaServiceStopped {
    param([Parameter(Mandatory = $true)][string]$ServiceName)
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) { return }
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
}

function Write-MemoriaSecretFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )
    Assert-MemoriaNotLink -Path $Path
    # The directory was restricted first, so the file inherits that ACL from its first byte. No BOM, one trailing newline.
    [IO.File]::WriteAllText($Path, "$Value`n", (New-Object Text.UTF8Encoding($false)))
}

function Invoke-MemoriaStep {
    param(
        [Parameter(Mandatory = $true)]$Step,
        [string]$SecretValue,
        [switch]$TakeOwnership
    )
    $data = $Step.Data
    switch ($Step.Kind) {
        "exe" { Invoke-MemoriaExeStep -Data $data }
        "ensure-dir" { New-Item -ItemType Directory -Force -Path $data.Path | Out-Null }
        "copy" { Copy-Item -LiteralPath $data.Source -Destination $data.Destination -Force }
        "replace" {
            if (Test-Path -LiteralPath $data.Destination) { Remove-Item -LiteralPath $data.Destination -Recurse -Force }
            Copy-Item -LiteralPath $data.Source -Destination $data.Destination -Recurse -Force
        }
        "remove" { if (Test-Path -LiteralPath $data.Path) { Remove-Item -LiteralPath $data.Path -Recurse -Force } }
        "wait-stopped" { Wait-MemoriaServiceStopped -ServiceName $data.ServiceName }
        "secure-dir" {
            Assert-MemoriaNotLink -Path $data.Path
            New-Item -ItemType Directory -Force -Path $data.Path | Out-Null
            Set-MemoriaRestrictedAcl -Path $data.Path -Grants $data.Grants -TakeOwnership:$TakeOwnership
        }
        "secure-file" { Set-MemoriaRestrictedAcl -Path $data.Path -Grants $data.Grants -TakeOwnership:$TakeOwnership }
        "write-secret" { Write-MemoriaSecretFile -Path $data.Path -Value $SecretValue }
        "verify-acl" {
            $extra = @(Get-MemoriaAclViolations -Path $data.Path -Allowed $data.Allowed)
            if ($extra.Count -gt 0) { throw "$($data.Path) can also be read by: $($extra -join ', ')" }
        }
        "verify-no-writers" {
            $writers = @(Get-MemoriaNonAdminWriters -Path $data.Path)
            if ($writers.Count -gt 0) { throw "$($data.Path) can also be changed by: $($writers -join ', '). Choose a location only administrators can write to." }
        }
        default { throw "Unknown step kind: $($Step.Kind)" }
    }
}

function Show-MemoriaPlan {
    param([Parameter(Mandatory = $true)][object[]]$Plan)
    $number = 0
    foreach ($step in $Plan) {
        $number++
        Write-Output ("{0,3}. {1}" -f $number, $step.Description)
    }
}

function Invoke-MemoriaPlan {
    param(
        [Parameter(Mandatory = $true)][object[]]$Plan,
        [string]$SecretValue
    )
    foreach ($step in $Plan) {
        Write-Host $step.Description
        Invoke-MemoriaStep -Step $step -SecretValue $SecretValue -TakeOwnership
    }
}

Export-ModuleMember -Function `
    Get-MemoriaServiceAccount, Test-MemoriaServiceExists, Test-MemoriaLoopback, Get-MemoriaSecretValue, Test-MemoriaNssmVersion, `
    New-MemoriaStep, New-MemoriaExeStep, New-MemoriaInstallPlan, New-MemoriaUninstallPlan, `
    Set-MemoriaRestrictedAcl, Test-MemoriaWriteRights, Get-MemoriaAclViolations, Get-MemoriaNonAdminWriters, `
    Write-MemoriaSecretFile, Assert-MemoriaNotLink, Show-MemoriaPlan, Invoke-MemoriaPlan
