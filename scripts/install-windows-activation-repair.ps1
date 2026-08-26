[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [Parameter(Mandatory)]
    [string]$SourcePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSha256
)

$ErrorActionPreference = "Stop"
$destinationRoot = Join-Path $env:ProgramData `
    "EndpointAgents\ControlPlane\FileRepository\Windows"
$destinationPath = Join-Path $destinationRoot "windowsbugfixer.bat"
$systemSid = "S-1-5-18"
$administratorsSid = "S-1-5-32-544"
$trustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

function Invoke-NativeChecked(
    [string]$Executable,
    [string]$Description,
    [string[]]$Arguments) {
    $output = & $Executable @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE`: $($output -join ' ')"
    }
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return -join ($algorithm.ComputeHash($stream) | ForEach-Object {
            $_.ToString("X2")
        })
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Assert-ProtectedRepository([string]$Path) {
    $directory = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $directory.PSIsContainer `
        -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The Windows artifact repository is missing or redirected."
    }

    $dangerousRights = [Security.AccessControl.FileSystemRights]::Write `
        -bor [Security.AccessControl.FileSystemRights]::Delete `
        -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles `
        -bor [Security.AccessControl.FileSystemRights]::ChangePermissions `
        -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    $replacementRights = [Security.AccessControl.FileSystemRights]::Delete `
        -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles `
        -bor [Security.AccessControl.FileSystemRights]::ChangePermissions `
        -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
    $current = $directory
    $isRepository = $true
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The Windows artifact repository path contains a reparse point."
        }
        $acl = Get-Acl -LiteralPath $current.FullName
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        $allowedOwners = if ($isRepository) {
            @($systemSid, $administratorsSid)
        }
        else {
            @($systemSid, $administratorsSid, $trustedInstallerSid)
        }
        $trustedIdentities = $allowedOwners
        if ($owner -notin $allowedOwners `
            -or ($isRepository -and -not $acl.AreAccessRulesProtected)) {
            throw "The Windows artifact repository path has an untrusted owner or ACL."
        }

        foreach ($rule in @($acl.GetAccessRules(
            $true, $true, [Security.Principal.SecurityIdentifier]))) {
            if ($rule.AccessControlType -ne `
                    [Security.AccessControl.AccessControlType]::Allow `
                -or $rule.IdentityReference.Value -in $trustedIdentities `
                -or (-not $isRepository -and ($rule.PropagationFlags -band `
                    [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0)) {
                continue
            }
            $blockedRights = if ($isRepository) {
                $dangerousRights
            }
            else {
                $replacementRights
            }
            if (($rule.FileSystemRights -band $blockedRights) -ne 0) {
                throw "The Windows artifact repository path is controlled by an untrusted account."
            }
        }
        $current = $current.Parent
        $isRepository = $false
    }
}

$source = [IO.FileInfo]::new([IO.Path]::GetFullPath($SourcePath))
$source.Refresh()
if (-not $source.Exists `
    -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 `
    -or $source.Extension -ine ".bat" `
    -or $source.Length -le 0 `
    -or $source.Length -gt 100MB) {
    throw "SourcePath must be a non-empty BAT file no larger than 100 MiB and cannot be redirected."
}

$expected = $ExpectedSha256.ToUpperInvariant()
$sourceHash = Get-Sha256 $source.FullName
if ($sourceHash -ne $expected) {
    throw "The seller BAT does not match ExpectedSha256."
}

if (-not $PSCmdlet.ShouldProcess(
        $destinationPath, "Install reviewed Windows activation repair $expected")) {
    [pscustomobject]@{
        Source = $source.FullName
        Destination = $destinationPath
        Sha256 = $sourceHash
        ValidatedOnly = $true
    }
    return
}

Assert-Administrator
Assert-ProtectedRepository $destinationRoot

$temporaryPath = Join-Path $destinationRoot `
    ".windowsbugfixer.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::Copy($source.FullName, $temporaryPath, $false)
    $temporaryHash = Get-Sha256 $temporaryPath
    if ($temporaryHash -ne $expected) {
        throw "The staged Windows activation repair failed SHA-256 verification."
    }

    Invoke-NativeChecked "icacls.exe" "Protecting the staged repair ACL" @(
        $temporaryPath, "/inheritance:r", "/grant:r",
        "*$systemSid`:F", "*$administratorsSid`:F")
    Invoke-NativeChecked "icacls.exe" "Setting the staged repair owner" @(
        $temporaryPath, "/setowner", "*$systemSid")

    $stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $stream.Flush($true) }
    finally { $stream.Dispose() }

    if (Test-Path -LiteralPath $destinationPath) {
        [IO.File]::Replace($temporaryPath, $destinationPath, $null, $true)
    }
    else {
        [IO.File]::Move($temporaryPath, $destinationPath)
    }

    Invoke-NativeChecked "icacls.exe" "Protecting the installed repair ACL" @(
        $destinationPath, "/inheritance:r", "/grant:r",
        "*$systemSid`:F", "*$administratorsSid`:F")
    Invoke-NativeChecked "icacls.exe" "Setting the installed repair owner" @(
        $destinationPath, "/setowner", "*$systemSid")

    $installedHash = Get-Sha256 $destinationPath
    if ($installedHash -ne $expected) {
        throw "The installed Windows activation repair failed SHA-256 verification."
    }

    [pscustomobject]@{
        Destination = $destinationPath
        Sha256 = $installedHash
        SizeBytes = (Get-Item -LiteralPath $destinationPath).Length
        InstalledAtUtc = [DateTimeOffset]::UtcNow
        ValidatedOnly = $false
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}
