[CmdletBinding()]
param(
    [string]$InstallRoot = "$env:ProgramFiles\EndpointAgents\ControlPlane",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$apiService = "EndpointAgents.ControlPlane.Api"
$legacyWebService = "EndpointAgents.ControlPlane.Web"
$repoRoot = Split-Path -Parent $PSScriptRoot
$apiOutput = Join-Path $InstallRoot "Api"
$endpointAgentsDataRoot = Join-Path $env:ProgramData "EndpointAgents"
$fileRepositoryRoot = Join-Path $env:ProgramData "EndpointAgents\ControlPlane\FileRepository"
$dataRoot = Join-Path $env:ProgramData "EndpointAgents\ControlPlane"
$officeRepositoryRoot = Join-Path $fileRepositoryRoot "Office"
$officeManifestPath = Join-Path $officeRepositoryRoot "office-artifacts.json"
$officeBugfixManifestPath = Join-Path $fileRepositoryRoot "office-bugfix-managers.json"
$windowsRepositoryRoot = Join-Path $fileRepositoryRoot "Windows"
$windowsBugfixPath = Join-Path $windowsRepositoryRoot "windowsbugfixer.bat"
$windowsHelloPath = Join-Path $windowsRepositoryRoot "windows-hello-sample.bat"
$sampleWindowsBugfixPath = Join-Path $repoRoot `
    "deploy\control-plane\FileRepository\Windows\windowsbugfixer.bat"
$disabledWindowsBugfixPath = Join-Path $windowsRepositoryRoot `
    "windowsbugfixer.development-sample.disabled"
$sampleWindowsHelloPath = Join-Path $repoRoot "deploy\control-plane\FileRepository\Windows\windows-hello-sample.bat"
$apiSettingsPath = Join-Path $apiOutput "appsettings.json"
$legacyOfficeBugfixBackupPath = Join-Path $fileRepositoryRoot `
    "office-bugfix-managers.legacy-exe-v1.json.bak"
$systemSid = "S-1-5-18"
$administratorsSid = "S-1-5-32-544"
$usersSid = "S-1-5-32-545"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

function Remove-ServiceIfPresent([string]$Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) { return }
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $Name -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    Invoke-NativeChecked "sc.exe" "Deleting service $Name" @("delete", $Name)
}

function Invoke-NativeChecked(
    [string]$Executable,
    [string]$Description,
    [string[]]$Arguments) {
    $output = & $Executable @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode`: $($output -join ' ')"
    }
}

function Assert-NoReparsePointInPath([string]$Path) {
    $currentPath = [IO.Path]::GetFullPath($Path)
    while ($null -ne $currentPath) {
        $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Protected path contains a reparse point: $($item.FullName)"
        }

        $parent = [IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent) { break }
        $currentPath = $parent.FullName
    }
}

function New-SafeDirectory([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $existingPath = $fullPath
    while (-not (Test-Path -LiteralPath $existingPath)) {
        $parent = [IO.Directory]::GetParent($existingPath)
        if ($null -eq $parent) {
            throw "Cannot find an existing parent for protected path: $fullPath"
        }
        $existingPath = $parent.FullName
    }

    Assert-NoReparsePointInPath $existingPath
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    Assert-NoReparsePointInPath $fullPath
}

function Get-SafeTreeItems([string]$Root) {
    Assert-NoReparsePointInPath $Root
    $rootItem = Get-Item -LiteralPath $Root -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer) {
        throw "Protected tree root is not a directory: $Root"
    }

    $items = New-Object 'Collections.Generic.List[IO.FileSystemInfo]'
    $pending = New-Object 'Collections.Generic.Queue[IO.DirectoryInfo]'
    $items.Add($rootItem)
    $pending.Enqueue($rootItem)

    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($child in @(Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction Stop)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Protected tree contains a reparse point: $($child.FullName)"
            }

            $items.Add($child)
            if ($child.PSIsContainer) {
                $pending.Enqueue($child)
            }
        }
    }

    return $items.ToArray()
}

function Get-SafeInstallRoot([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@("\", "/"))
    $programFiles = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd([char[]]@("\", "/"))
    $requiredPrefix = $programFiles + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallRoot must remain below Program Files."
    }

    if (Test-Path -LiteralPath $resolved) {
        $null = @(Get-SafeTreeItems $resolved)
    }
    else {
        $existingPath = $resolved
        while (-not (Test-Path -LiteralPath $existingPath)) {
            $existingPath = [IO.Directory]::GetParent($existingPath).FullName
        }
        Assert-NoReparsePointInPath $existingPath
    }
    return $resolved
}

function Assert-ExactProtectedAcl(
    [IO.FileSystemInfo]$Item,
    [switch]$AllowUsersReadAndExecute,
    [switch]$ThisItemOnly) {
    Assert-NoReparsePointInPath $Item.FullName
    $currentItem = Get-Item -LiteralPath $Item.FullName -Force -ErrorAction Stop
    $acl = Get-Acl -LiteralPath $currentItem.FullName
    $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($ownerSid -ne $systemSid -or -not $acl.AreAccessRulesProtected) {
        throw "Protected ACL owner or inheritance verification failed: $($currentItem.FullName)"
    }

    $rules = @($acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    $expectedRuleCount = if ($AllowUsersReadAndExecute) { 3 } else { 2 }
    if ($rules.Count -ne $expectedRuleCount) {
        throw "Protected ACL contains an unexpected number of access rules: $($currentItem.FullName)"
    }

    $expectedInheritance = [Security.AccessControl.InheritanceFlags]::None
    if ($currentItem.PSIsContainer -and -not $ThisItemOnly) {
        $expectedInheritance = [Security.AccessControl.InheritanceFlags]::ObjectInherit -bor `
            [Security.AccessControl.InheritanceFlags]::ContainerInherit
    }
    $expectedUserRights = [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor `
        [Security.AccessControl.FileSystemRights]::Synchronize

    $actualSids = @()
    foreach ($rule in $rules) {
        $ruleSid = $rule.IdentityReference.Value
        $actualSids += $ruleSid
        $expectedRights = [Security.AccessControl.FileSystemRights]::FullControl
        if ($ruleSid -eq $usersSid) {
            $expectedRights = $expectedUserRights
        }
        if ($rule.IsInherited `
            -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow `
            -or $rule.FileSystemRights -ne $expectedRights `
            -or $rule.InheritanceFlags -ne $expectedInheritance `
            -or $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw "Protected ACL access rule verification failed: $($currentItem.FullName)"
        }
    }

    if (@($actualSids | Sort-Object -Unique).Count -ne $expectedRuleCount `
        -or $actualSids -notcontains $systemSid `
        -or $actualSids -notcontains $administratorsSid `
        -or ($AllowUsersReadAndExecute -and $actualSids -notcontains $usersSid) `
        -or (-not $AllowUsersReadAndExecute -and $actualSids -contains $usersSid)) {
        throw "Protected ACL principals verification failed: $($currentItem.FullName)"
    }
}

function Set-ExactProtectedItemAcl(
    [IO.FileSystemInfo]$Item,
    [switch]$AllowUsersReadAndExecute,
    [switch]$ThisItemOnly) {
    Assert-NoReparsePointInPath $Item.FullName
    $inheritance = if ($Item.PSIsContainer -and -not $ThisItemOnly) {
        "(OI)(CI)F"
    }
    else {
        "F"
    }
    $userAccess = if ($Item.PSIsContainer -and -not $ThisItemOnly) {
        "(OI)(CI)RX"
    }
    else {
        "RX"
    }
    Invoke-NativeChecked "icacls.exe" "Taking temporary ownership of $($Item.FullName)" @(
        $Item.FullName, "/setowner", "*$administratorsSid")
    Invoke-NativeChecked "icacls.exe" "Resetting the ACL on $($Item.FullName)" @(
        $Item.FullName, "/reset")
    Invoke-NativeChecked "icacls.exe" "Protecting the ACL on $($Item.FullName)" @(
        $Item.FullName, "/inheritance:r")
    $grantArguments = @(
        $Item.FullName,
        "/grant:r",
        "*$systemSid`:$inheritance",
        "*$administratorsSid`:$inheritance")
    if ($AllowUsersReadAndExecute) {
        $grantArguments += "*$usersSid`:$userAccess"
    }
    Invoke-NativeChecked "icacls.exe" "Setting the ACL on $($Item.FullName)" `
        $grantArguments
    Invoke-NativeChecked "icacls.exe" "Setting the owner on $($Item.FullName)" @(
        $Item.FullName, "/setowner", "*$systemSid")
}

function Set-ExactProtectedDirectoryAcl(
    [string]$Path,
    [switch]$AllowUsersReadAndExecute) {
    Assert-NoReparsePointInPath $Path
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) {
        throw "Protected path is not a directory: $Path"
    }
    Set-ExactProtectedItemAcl $item `
        -AllowUsersReadAndExecute:$AllowUsersReadAndExecute `
        -ThisItemOnly
    Assert-ExactProtectedAcl $item `
        -AllowUsersReadAndExecute:$AllowUsersReadAndExecute `
        -ThisItemOnly
}

function Set-ExactProtectedTreeAcl(
    [string]$Root,
    [switch]$AllowUsersReadAndExecute) {
    $items = @(Get-SafeTreeItems $Root)
    foreach ($item in $items) {
        Set-ExactProtectedItemAcl $item `
            -AllowUsersReadAndExecute:$AllowUsersReadAndExecute
    }

    foreach ($item in @(Get-SafeTreeItems $Root)) {
        Assert-ExactProtectedAcl $item `
            -AllowUsersReadAndExecute:$AllowUsersReadAndExecute
    }
}

function Write-JsonAtomic([string]$Path, [object]$Value) {
    $directory = Split-Path -Parent $Path
    Assert-NoReparsePointInPath $directory
    if (Test-Path -LiteralPath $Path) {
        Assert-NoReparsePointInPath $Path
    }
    $temporaryPath = Join-Path $directory `
        (".{0}.{1}.tmp" -f [IO.Path]::GetFileName($Path), [Guid]::NewGuid().ToString("N"))
    $replacementBackupPath = "$temporaryPath.bak"
    $json = ($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine
    $utf8NoBom = [Text.UTF8Encoding]::new($false)

    try {
        [IO.File]::WriteAllText($temporaryPath, $json, $utf8NoBom)
        if (Test-Path -LiteralPath $Path) {
            [IO.File]::Replace($temporaryPath, $Path, $replacementBackupPath)
            [IO.File]::Delete($replacementBackupPath)
        }
        else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            [IO.File]::Delete($temporaryPath)
        }
        if (Test-Path -LiteralPath $replacementBackupPath) {
            [IO.File]::Delete($replacementBackupPath)
        }
    }
}

function Get-ExistingHermesSettings([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    Assert-NoReparsePointInPath $Path

    try {
        $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Cannot preserve Hermes settings because the installed appsettings.json is invalid."
    }

    if ($settings.PSObject.Properties.Name -contains "Hermes") {
        return $settings.Hermes
    }

    return $null
}

function Restore-HermesSettings([string]$Path, [object]$HermesSettings) {
    if ($null -eq $HermesSettings) { return }
    Assert-NoReparsePointInPath $Path

    try {
        $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Cannot restore Hermes settings because the published appsettings.json is invalid."
    }

    if ($settings.PSObject.Properties.Name -contains "Hermes") {
        $settings.Hermes = $HermesSettings
    }
    else {
        $settings | Add-Member -MemberType NoteProperty -Name "Hermes" -Value $HermesSettings
    }
    Write-JsonAtomic $Path $settings
}

function Initialize-OfficeBugfixManifest {
    if (-not (Test-Path -LiteralPath $officeBugfixManifestPath)) {
        Write-JsonAtomic $officeBugfixManifestPath ([ordered]@{ version = 1; artifacts = @() })
        return
    }

    try {
        $manifest = Get-Content -LiteralPath $officeBugfixManifestPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Office bugfix manifest is invalid JSON; it was not changed: $($_.Exception.Message)"
    }
    if ($null -eq $manifest `
        -or $manifest.PSObject.Properties.Name -notcontains "artifacts") {
        throw "Office bugfix manifest has no artifacts property; it was not changed."
    }
    if ($manifest.version -isnot [int] -or $manifest.version -ne 1) {
        throw "Office bugfix manifest version must be 1; it was not changed."
    }
    if ($null -eq $manifest.artifacts) {
        throw "Office bugfix manifest artifacts cannot be null; it was not changed."
    }

    $entries = @($manifest.artifacts)
    $legacyExeEntries = @($entries | Where-Object {
        [IO.Path]::GetExtension([string]$_.fileName) -ieq ".exe"
    })
    if ($legacyExeEntries.Count -eq 0) { return }

    $sourceHash = (Get-FileHash -LiteralPath $officeBugfixManifestPath -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath $legacyOfficeBugfixBackupPath) {
        $backupHash = (Get-FileHash -LiteralPath $legacyOfficeBugfixBackupPath -Algorithm SHA256).Hash
        if ($backupHash -ne $sourceHash) {
            throw "The one-time legacy Office manifest backup differs from the current manifest; migration was refused."
        }
    }
    else {
        $temporaryBackupPath = "$legacyOfficeBugfixBackupPath.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            [IO.File]::Copy($officeBugfixManifestPath, $temporaryBackupPath, $false)
            $backupStream = [IO.File]::Open(
                $temporaryBackupPath,
                [IO.FileMode]::Open,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            try {
                $backupStream.Flush($true)
            }
            finally {
                $backupStream.Dispose()
            }
            if ((Get-FileHash -LiteralPath $temporaryBackupPath -Algorithm SHA256).Hash `
                -ne $sourceHash) {
                throw "The legacy Office manifest backup did not verify; migration was refused."
            }
            [IO.File]::Move($temporaryBackupPath, $legacyOfficeBugfixBackupPath)
        }
        finally {
            if (Test-Path -LiteralPath $temporaryBackupPath) {
                [IO.File]::Delete($temporaryBackupPath)
            }
        }
    }
    if ((Get-FileHash -LiteralPath $legacyOfficeBugfixBackupPath -Algorithm SHA256).Hash `
        -ne $sourceHash) {
        throw "The legacy Office manifest backup did not verify; migration was refused."
    }

    $batEntries = @($entries | Where-Object {
        [IO.Path]::GetExtension([string]$_.fileName) -ieq ".bat"
    })
    Write-JsonAtomic $officeBugfixManifestPath `
        ([ordered]@{ version = 1; artifacts = $batEntries })
    Write-Host "Migrated legacy Office EXE manifest entries; backup: $legacyOfficeBugfixBackupPath"
}

Assert-Administrator
$installRootExists = Test-Path -LiteralPath $InstallRoot
$resolvedInstallRoot = Get-SafeInstallRoot $InstallRoot
$preservedHermesSettings = Get-ExistingHermesSettings $apiSettingsPath

# Remove the retired supervisor during upgrades from the former two-service layout.
Remove-ServiceIfPresent $legacyWebService
Remove-ServiceIfPresent $apiService

if ($installRootExists) {
    Remove-Item -LiteralPath $resolvedInstallRoot -Recurse -Force
}

dotnet publish (Join-Path $repoRoot "src\ControlPlane.Api\ControlPlane.Api.csproj") `
    -c $Configuration -r win-x64 --self-contained false -o $apiOutput
if ($LASTEXITCODE -ne 0) {
    throw "Control plane API publish failed with exit code $LASTEXITCODE."
}
Set-ExactProtectedTreeAcl $apiOutput
Restore-HermesSettings $apiSettingsPath $preservedHermesSettings
if ($null -ne $preservedHermesSettings) {
    Set-ExactProtectedTreeAcl $apiOutput
}

New-SafeDirectory $endpointAgentsDataRoot
Set-ExactProtectedDirectoryAcl $endpointAgentsDataRoot -AllowUsersReadAndExecute
New-SafeDirectory $dataRoot
New-SafeDirectory $officeRepositoryRoot
New-SafeDirectory $windowsRepositoryRoot
New-SafeDirectory (Join-Path $fileRepositoryRoot "packages")
Set-ExactProtectedTreeAcl $dataRoot
if (Test-Path -LiteralPath $windowsBugfixPath) {
    $runtimeHash = (Get-FileHash -LiteralPath $windowsBugfixPath -Algorithm SHA256).Hash
    $sampleHash = (Get-FileHash -LiteralPath $sampleWindowsBugfixPath `
        -Algorithm SHA256).Hash
    if ($runtimeHash -eq $sampleHash) {
        if (Test-Path -LiteralPath $disabledWindowsBugfixPath) {
            throw "The development Windows repair sample cannot be disabled because the disabled target already exists."
        }
        Move-Item -LiteralPath $windowsBugfixPath `
            -Destination $disabledWindowsBugfixPath
        Write-Host "Disabled the development Windows repair sample; install an approved seller BAT separately."
    }
}
if (-not (Test-Path -LiteralPath $officeManifestPath)) {
    Write-JsonAtomic $officeManifestPath ([ordered]@{ version = 1; artifacts = @() })
}
Initialize-OfficeBugfixManifest
if (-not (Test-Path -LiteralPath $windowsHelloPath)) {
    Copy-Item -LiteralPath $sampleWindowsHelloPath -Destination $windowsHelloPath
}
Set-ExactProtectedTreeAcl $dataRoot

$apiExe = Join-Path $apiOutput "ControlPlane.Api.exe"

Invoke-NativeChecked "sc.exe" "Creating service $apiService" @(
    "create", $apiService,
    "binPath=", "`"$apiExe`"",
    "start=", "auto",
    "DisplayName=", "EndpointAgents Control Plane API")
Invoke-NativeChecked "sc.exe" "Configuring recovery for service $apiService" @(
    "failure", $apiService,
    "reset=", "86400",
    "actions=", "restart/5000/restart/15000/restart/30000")

Start-Service -Name $apiService

Write-Host "Control plane API installed on http://127.0.0.1:5052."
Write-Host "Office remediation manifest: $officeManifestPath"
Write-Host "Office bugfix manifest: $officeBugfixManifestPath"
Write-Host "Windows bugfix file (administrator supplied; not seeded): $windowsBugfixPath"
Write-Host "Windows bugfix installer: $(Join-Path $repoRoot 'scripts\install-windows-activation-repair.ps1')"
Write-Host "Windows Hello sample file: $windowsHelloPath"
Write-Host "SQLite chat database: $(Join-Path $dataRoot 'control-plane.db')"
Write-Host "Install the pinned Hermes runtime next with scripts\install-hermes-service.ps1."
Write-Host "ControlPlane.Web is separate; start it from src\ControlPlane.Web with npm.cmd run start."
