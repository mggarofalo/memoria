<#
    Checks for deploy/windows/MemoriaService.psm1, run by test/install-scripts.test.ts.
    Exits non-zero on the first failure. The ACL section uses a temp directory and
    the current user only: it creates no account and touches no service.
#>
param([Parameter(Mandatory = $true)][string]$ModulePath)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0
Import-Module $ModulePath -Force

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "FAILED: $Message" }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern)
    try {
        & $Action
    } catch {
        Assert-True ($_.Exception.Message -match $Pattern) "expected /$Pattern/, got: $($_.Exception.Message)"
        return
    }
    throw "FAILED: expected an error matching /$Pattern/"
}

# --- pure helpers ------------------------------------------------------------

Assert-True (Test-MemoriaLoopback -BindHost "127.0.0.1") "127.0.0.1 is loopback"
Assert-True (Test-MemoriaLoopback -BindHost "127.8.8.8") "127/8 is loopback"
Assert-True (Test-MemoriaLoopback -BindHost "localhost") "localhost is loopback"
Assert-True (-not (Test-MemoriaLoopback -BindHost "192.168.1.20")) "a LAN address is not loopback"
Assert-True ((Get-MemoriaServiceAccount -ServiceName "memoria-qmd") -eq "NT SERVICE\memoria-qmd") "the account is named after the service"

$stable = "NSSM: The non-sucking service manager`nVersion 2.24 64-bit, 2014-08-31"
$pre = "NSSM: The non-sucking service manager`nVersion 2.24-101-g897c7ad 64-bit, 2017-04-26"
Assert-True (-not (Test-MemoriaNssmVersion -VersionText $stable)) "stable 2.24 is flagged"
Assert-True (Test-MemoriaNssmVersion -VersionText $pre) "2.24-101 is fine"
Assert-True (-not (Test-MemoriaNssmVersion -VersionText ($stable.ToCharArray() -join "`0"))) "UTF-16 output of 2.24 is flagged"
Assert-True (Test-MemoriaNssmVersion -VersionText "") "no output is not a false alarm"

$good = "0123456789abcdefXYZ"
Assert-True ($null -eq (Get-MemoriaSecretValue -SourceFile "" -EnvValue "" -Label "secret")) "nothing set is no secret"
Assert-True ((Get-MemoriaSecretValue -SourceFile "" -EnvValue " $good `n" -Label "secret") -eq $good) "the environment value is trimmed"
Assert-Throws { Get-MemoriaSecretValue -SourceFile "" -EnvValue "short" -Label "secret" } "at least 16"
Assert-Throws { Get-MemoriaSecretValue -SourceFile "" -EnvValue "$good`n$good" -Label "secret" } "one line"
Assert-Throws { Get-MemoriaSecretValue -SourceFile (Join-Path ([IO.Path]::GetTempPath()) "memoria-absent-$PID") -EnvValue "" -Label "secret" } "not found"

foreach ($rights in @(0x2, 0x10000, 0x40000, 0x80000, 0x10000000, 0x40000000, 0x1F01FF, 0x1301BF)) {
    Assert-True (Test-MemoriaWriteRights -Rights $rights) "rights $rights can change a file"
}
# ReadAndExecute, Read, and GENERIC_READ|GENERIC_EXECUTE as a signed 32-bit value.
foreach ($rights in @(0x1200A9, 0x120089, -1610612736)) {
    Assert-True (-not (Test-MemoriaWriteRights -Rights $rights)) "rights $rights cannot change a file"
}

# --- the plan ---------------------------------------------------------------

function New-TestSpec {
    param([bool]$Exists = $false, [bool]$HasSecret = $true)
    return @{
        ServiceName = "memoria-test"; DisplayName = "T"; Description = "T"
        SourceDir = "/src"; Entry = "dist/main.js"; CopyItems = @("dist")
        InstallDir = "/pf/memoria-test"; StateDir = "/pd/memoria-test"; LogDir = "/pd/memoria-test/logs"
        SecretPath = "/pd/memoria-test/secret/secret.txt"; HasSecret = $HasSecret
        NodeSource = "/bin/node"; NssmSource = "/bin/nssm"
        Environment = @("A=1", "B=2"); ReadPaths = @("/read"); ModifyPaths = @("/modify")
        KillProcessTree = $true; Exists = $Exists; BuildSteps = @(); PostCopySteps = @()
    }
}

function Get-StepIndex {
    param([object[]]$Plan, [string]$Pattern)
    for ($i = 0; $i -lt $Plan.Count; $i++) {
        if ($Plan[$i].Description -match $Pattern) { return $i }
    }
    throw "FAILED: no step matches /$Pattern/"
}

$plan = New-MemoriaInstallPlan -Spec (New-TestSpec)
$serviceInstall = Get-StepIndex $plan "nssm\.exe install memoria-test"
$account = Get-StepIndex $plan "sc\.exe config memoria-test obj= "
$sidType = Get-StepIndex $plan "sc\.exe sidtype memoria-test unrestricted"
$secretDir = Get-StepIndex $plan "Restrict .*secret to Administrators"
$write = Get-StepIndex $plan "Write the secret"
$verify = Get-StepIndex $plan "Verify nobody else"
$start = Get-StepIndex $plan "sc\.exe start"
$noWriters = Get-StepIndex $plan "Verify only administrators"
Assert-True ($sidType -lt $account) "the SID type is set before the account"
Assert-True ($serviceInstall -lt $account) "the service exists before its account is assigned"
Assert-True ($account -lt $secretDir) "the account resolves before an ACL names it"
Assert-True ($secretDir -lt $write) "the directory is locked before the secret is written"
Assert-True ($write -lt $verify) "the ACL is checked after the write"
Assert-True ($verify -lt $start) "nothing starts until the secret is verified"
Assert-True ($noWriters -lt $serviceInstall) "code location is checked before it is registered"
Assert-True ($start -eq ($plan.Count - 1)) "starting is the last step"

$text = ($plan | ForEach-Object { $_.Description }) -join "`n"
Assert-True ($text -match "AppRotateOnline 1") "online rotation is on"
Assert-True ($text -match "AppRotateFiles 1") "rotation is on"
Assert-True ($text -match "AppKillProcessTree 1") "process tree setting follows the spec"
Assert-True ($text -match "AppEnvironmentExtra A=1 B=2") "the environment is passed as separate arguments"
Assert-True ($text -notmatch "(?i)localsystem") "nothing runs as LocalSystem"
Assert-True ($text -match "/read /grant .*RX") "read paths get read access"
Assert-True ($text -match "/modify /grant .*\(M\)|/modify /grant .*:\(OI\)\(CI\)M") "modify paths get change access"
Assert-True ($text -notmatch "nssm\.exe set memoria-test ObjectName") "the account is not set through NSSM"

$upgrade = New-MemoriaInstallPlan -Spec (New-TestSpec -Exists $true)
$stop = Get-StepIndex $upgrade "sc\.exe stop memoria-test"
$copy = Get-StepIndex $upgrade "Copy node"
Assert-True ($stop -lt $copy) "an upgrade stops the service before replacing its files"
Assert-True ((($upgrade | ForEach-Object { $_.Description }) -join "`n") -notmatch "nssm\.exe install") "an upgrade does not install again"

$noSecret = (New-MemoriaInstallPlan -Spec (New-TestSpec -HasSecret $false) | ForEach-Object { $_.Description }) -join "`n"
Assert-True ($noSecret -notmatch "Write the secret") "no secret, no secret file"
Assert-True ($noSecret -match "Remove any earlier secret") "a stale secret does not outlive being unset"

$uninstall = New-MemoriaUninstallPlan -ServiceName "memoria-test" -Nssm "/pf/nssm.exe" -InstallDir "/pf/memoria-test" -SecretDir "/pd/memoria-test/secret" -DataPaths @("/data")
$revoke = Get-StepIndex $uninstall "/data /remove"
$remove = Get-StepIndex $uninstall "nssm\.exe remove"
$removeSecret = Get-StepIndex $uninstall "Remove the secret"
Assert-True ($revoke -lt $remove) "grants come off while the account still resolves"
Assert-True ($remove -lt $removeSecret) "the secret goes after the service"
Assert-True ((($uninstall | ForEach-Object { $_.Description }) -join "`n") -notmatch "logs") "logs stay"

# --- real ACLs on a temp directory (Windows only) ------------------------------

$onWindows = ($PSVersionTable.PSVersion.Major -lt 6) -or $IsWindows
if ($onWindows) {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $admins = "S-1-5-32-544"
    $root = Join-Path ([IO.Path]::GetTempPath()) "memoria-acl-$PID-$(Get-Random)"
    New-Item -ItemType Directory -Path $root | Out-Null
    try {
        $dir = Join-Path $root "secret"
        New-Item -ItemType Directory -Path $dir | Out-Null
        $grants = @(@{ Principal = "*$admins"; Rights = "FullControl" }, @{ Principal = "*$me"; Rights = "FullControl" })

        # Something a planted directory might carry, which the replacement must remove.
        & icacls.exe $dir /grant "*S-1-5-32-545:(OI)(CI)R" | Out-Null
        Assert-True (@(Get-MemoriaAclViolations -Path $dir -Allowed @($admins, $me)).Count -gt 0) "the planted rule is visible first"
        Set-MemoriaRestrictedAcl -Path $dir -Grants $grants
        Assert-True (@(Get-MemoriaAclViolations -Path $dir -Allowed @($admins, $me)).Count -eq 0) "the directory holds only the given principals"
        Assert-True ((Get-Acl -LiteralPath $dir).AreAccessRulesProtected) "inheritance is off"

        $file = Join-Path $dir "secret.txt"
        Write-MemoriaSecretFile -Path $file -Value $good
        Assert-True ([IO.File]::ReadAllText($file) -ceq "$good`n") "one trailing newline"
        $bytes = [IO.File]::ReadAllBytes($file)
        Assert-True ($bytes[0] -ne 0xEF) "no BOM"
        Assert-True (@(Get-MemoriaAclViolations -Path $file -Allowed @($admins, $me)).Count -eq 0) "the file inherits the locked directory"

        Set-MemoriaRestrictedAcl -Path $file -Grants $grants
        Assert-True ((Get-Acl -LiteralPath $file).AreAccessRulesProtected) "the file has its own protected ACL"
        Assert-True (@(Get-MemoriaAclViolations -Path $file -Allowed @($admins, $me)).Count -eq 0) "the file holds only the given principals"
        Assert-True (@(Get-MemoriaAclViolations -Path $file -Allowed @($admins)).Count -gt 0) "the check does see a principal outside the list"

        # The same check that guards the install location, on a directory a normal user can change.
        Assert-True ((Get-MemoriaNonAdminWriters -Path $file) -contains $me) "a non-admin writer is reported"
        $readOnly = Join-Path $root "readonly.txt"
        Set-Content -LiteralPath $readOnly -Value "x"
        Set-MemoriaRestrictedAcl -Path $readOnly -Grants @(@{ Principal = "*$admins"; Rights = "FullControl" }, @{ Principal = "*$me"; Rights = "Read" }, @{ Principal = "*S-1-5-18"; Rights = "FullControl" })
        Assert-True (@(Get-MemoriaNonAdminWriters -Path $readOnly).Count -eq 0) "read access is not a writer"

        $target = Join-Path $root "target"
        New-Item -ItemType Directory -Path $target | Out-Null
        $link = Join-Path $root "link"
        New-Item -ItemType Junction -Path $link -Target $target | Out-Null
        Assert-Throws { Set-MemoriaRestrictedAcl -Path $link -Grants $grants } "is a link"
        Assert-Throws { Write-MemoriaSecretFile -Path $link -Value $good } "is a link"
    } finally {
        & icacls.exe $root /grant "*${me}:(OI)(CI)F" /T /C /Q 2>$null | Out-Null
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Output "all checks passed"
