[CmdletBinding()]
param(
    [string]$InstallRoot = "$env:ProgramFiles\EndpointAgents\Endpoint",
    [Parameter(Mandatory)]
    [uri]$ControlPlaneHubUrl,
    [string]$EnrollmentCode = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    # Public key(s) of the administrators who sign packages (new-package-signing-key.ps1 -Show).
    # Without one, the endpoint refuses every repair package.
    [string[]]$PackageSigningPublicKey = @(),
    # Also require signatures on Office and Windows activation repairs.
    [switch]$RequireSignedActivationRepairs,
    # Install the previous WinUI Tray instead of the WebView2 Tray.
    [switch]$LegacyTray,
    # Microsoft Edge WebView2 Runtime "Evergreen Standalone Installer" (x64), used only when the
    # runtime is missing. Defaults to deploy\endpoint\MicrosoftEdgeWebView2RuntimeInstallerX64.exe.
    [string]$WebView2InstallerPath = ""
)

$ErrorActionPreference = "Stop"
$serviceName = "EndpointAgents.Service"
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$repoRoot = Split-Path -Parent $PSScriptRoot
$serviceOutput = Join-Path $InstallRoot "Service"
$trayOutput = Join-Path $InstallRoot "Tray"
$endpointAgentsDataRoot = Join-Path $env:ProgramData "EndpointAgents"
$credentialRoot = Join-Path $env:ProgramData "EndpointAgents\Credentials"
$officeRemediationRoot = Join-Path $env:ProgramData "EndpointAgents\OfficeRemediation"
$windowsRemediationRoot = Join-Path $env:ProgramData "EndpointAgents\WindowsRemediation"
# This PC's own settings live outside the program folder, so self-updates keep them.
$configRoot = Join-Path $env:ProgramData "EndpointAgents\Config"
$siteSettingsPath = Join-Path $configRoot "appsettings.site.json"
$updatesRoot = Join-Path $env:ProgramData "EndpointAgents\Updates"
$systemSid = "S-1-5-18"
$administratorsSid = "S-1-5-32-544"
$usersSid = "S-1-5-32-545"

# If anything below fails, leave a log on this PC and send one small report to the control plane,
# so a PC that never finished installing shows on the Problems page. There is no agent and no token
# yet: the report holds the step, the first line of the error with personal data cut, and Windows facts.
$script:InstallStep = "Starting"

function Get-CleanInstallError([string]$Text) {
    $line = (($Text -split "`r?`n") | Select-Object -First 1)
    $line = $line -replace '(?i)([A-Z]:\\(Users|Documents and Settings))\\[^\\\s"'']+', '$1\<user>'
    $line = $line -replace '\\\\[A-Za-z0-9._-]+\\[^\s"'']+', '\\<server>\<share>'
    $line = $line -replace '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}', '<email>'
    if ($line.Length -gt 300) { $line = $line.Substring(0, 300) }
    return $line
}

function Send-InstallFailure([string]$Step, [string]$ErrorText) {
    $message = Get-CleanInstallError $ErrorText
    try {
        $reports = Join-Path $env:ProgramData "EndpointAgents\Reports"
        New-Item -ItemType Directory -Path $reports -Force | Out-Null
        Add-Content -LiteralPath (Join-Path $reports "install-failure.log") -Encoding UTF8 `
            -Value "$((Get-Date).ToUniversalTime().ToString('o')) Install failed at step '$Step': $message"
    }
    catch { Write-Warning "The install log could not be written: $($_.Exception.Message)" }
    try {
        $windows = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $body = @{
            machineName = $env:COMPUTERNAME
            step = $Step
            message = $message
            osName = $(if ([int]$windows.CurrentBuildNumber -ge 22000) { "Windows 11" } else { "Windows 10" })
            osVersion = $windows.DisplayVersion
            osBuild = "$($windows.CurrentBuildNumber).$($windows.UBR)"
        } | ConvertTo-Json -Compress
        $url = $ControlPlaneHubUrl.GetLeftPart([UriPartial]::Authority) + "/api/install-reports"
        Invoke-RestMethod -Uri $url -Method Post -ContentType "application/json" `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 10 | Out-Null
        Write-Host "The control plane was told that this install failed at step '$Step'."
    }
    catch { Write-Warning "The control plane could not be told about this failure: $($_.Exception.Message)" }
}

trap {
    Send-InstallFailure $script:InstallStep $_.Exception.Message
    break
}

function Test-WebView2Runtime {
    $clientKey = "Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
    foreach ($path in "HKLM:\SOFTWARE\WOW6432Node\$clientKey", "HKLM:\SOFTWARE\$clientKey") {
        $version = (Get-ItemProperty -Path $path -Name pv -ErrorAction SilentlyContinue).pv
        if ($version -and $version -ne "0.0.0.0") { return $true }
    }
    return $false
}

# The WebView2 Tray needs the Edge WebView2 Runtime. Windows 11 ships it; some Windows 10 PCs do
# not. Install it from Microsoft's offline installer, only after checking that Microsoft signed it.
function Ensure-WebView2Runtime([string]$InstallerPath) {
    if (Test-WebView2Runtime) {
        Write-Host "Microsoft Edge WebView2 Runtime is installed."
        return
    }
    if (-not $InstallerPath) {
        $InstallerPath = Join-Path $repoRoot "deploy\endpoint\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
    }
    if (-not (Test-Path -LiteralPath $InstallerPath)) {
        throw ("Microsoft Edge WebView2 Runtime is not installed and no offline installer was found at " +
            "$InstallerPath. Download the x64 Evergreen Standalone Installer from " +
            "https://developer.microsoft.com/microsoft-edge/webview2/ and pass -WebView2InstallerPath, " +
            "or install with -LegacyTray.")
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
    if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
        throw "The WebView2 installer at $InstallerPath is not validly signed by Microsoft; refusing to run it."
    }
    Write-Host "Installing Microsoft Edge WebView2 Runtime from $InstallerPath..."
    $process = Start-Process -FilePath $InstallerPath -ArgumentList "/silent", "/install" -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-WebView2Runtime)) {
        throw "Microsoft Edge WebView2 Runtime installation failed with exit code $($process.ExitCode)."
    }
}

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

function Ensure-EventSource([string]$Source) {
    if (-not [Diagnostics.EventLog]::SourceExists($Source)) {
        [Diagnostics.EventLog]::CreateEventSource($Source, "Application")
    }
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

$script:InstallStep = "Check administrator"
Assert-Administrator
$script:InstallStep = "Check settings"
Ensure-EventSource "EndpointAgents.Service"
Ensure-EventSource "EndpointAgents.Tray"
if ($ControlPlaneHubUrl.Scheme -notin @("http", "https") `
    -or -not $ControlPlaneHubUrl.AbsolutePath.EndsWith("/agent-hub", [StringComparison]::OrdinalIgnoreCase)) {
    throw "ControlPlaneHubUrl must be an absolute HTTP(S) URL ending in /agent-hub."
}
if ($ControlPlaneHubUrl.Scheme -ne "https" `
    -and $ControlPlaneHubUrl.Host -notin @("localhost", "127.0.0.1", "::1")) {
    throw "Remote endpoint connections must use HTTPS."
}

$script:InstallStep = "Remove old install"
$installRootExists = Test-Path -LiteralPath $InstallRoot
$resolvedInstallRoot = Get-SafeInstallRoot $InstallRoot
Remove-ServiceIfPresent $serviceName
if ($installRootExists) {
    Remove-Item -LiteralPath $resolvedInstallRoot -Recurse -Force
}

$script:InstallStep = "Publish service"
dotnet publish (Join-Path $repoRoot "src\EndpointAgents.Service\EndpointAgents.Service.csproj") `
    -c $Configuration -r win-x64 --self-contained false -o $serviceOutput
if ($LASTEXITCODE -ne 0) {
    throw "Endpoint service publish failed with exit code $LASTEXITCODE."
}
$script:InstallStep = "Publish Tray"
if ($LegacyTray) {
    dotnet publish (Join-Path $repoRoot "src\EndpointAgents.Tray\EndpointAgents.Tray.csproj") `
        -c $Configuration -r win-x64 --self-contained false -o $trayOutput
}
else {
    $script:InstallStep = "WebView2 runtime"
    Ensure-WebView2Runtime $WebView2InstallerPath
    $script:InstallStep = "Publish Tray"
    # A prebuilt UI (ui\dist) means Node.js is not needed on this PC.
    $prebuiltUi = Test-Path -LiteralPath (Join-Path $repoRoot "src\EndpointAgents.TrayWeb\ui\dist\index.html")
    $uiArguments = if ($prebuiltUi) { @("-p:SkipTrayUi=true") } else { @() }
    dotnet publish (Join-Path $repoRoot "src\EndpointAgents.TrayWeb\EndpointAgents.TrayWeb.csproj") `
        -c $Configuration -r win-x64 --self-contained false -o $trayOutput @uiArguments
}
if ($LASTEXITCODE -ne 0) {
    throw "Endpoint Tray publish failed with exit code $LASTEXITCODE."
}

$script:InstallStep = "Write settings"
$serviceSettingsPath = Join-Path $serviceOutput "appsettings.json"
Set-ExactProtectedTreeAcl $serviceOutput
$serviceSettings = Get-Content -LiteralPath $serviceSettingsPath -Raw | ConvertFrom-Json
$serviceSettings.ControlPlane.HubUrl = $ControlPlaneHubUrl.AbsoluteUri
$serviceSettings.ControlPlane.EnrollmentCode = $EnrollmentCode
$serviceSettings.PackageSigning.TrustedPublicKeys = @($PackageSigningPublicKey)
$serviceSettings.PackageSigning.RequireForActivationRepairs = [bool]$RequireSignedActivationRepairs
if ($PackageSigningPublicKey.Count -eq 0) {
    Write-Warning "No package signing key was given; this endpoint will refuse every repair package."
}
$serviceSettingsJson = ($serviceSettings | ConvertTo-Json -Depth 10) + [Environment]::NewLine
Remove-Item -LiteralPath $serviceSettingsPath -Force
[IO.File]::WriteAllText(
    $serviceSettingsPath,
    $serviceSettingsJson,
    [Text.UTF8Encoding]::new($false))
Set-ExactProtectedTreeAcl $serviceOutput

$serviceExe = Join-Path $serviceOutput "EndpointAgents.Service.exe"
$trayExe = Join-Path $trayOutput $(if ($LegacyTray) { "EndpointAgents.Tray.exe" } else { "EndpointAgents.TrayWeb.exe" })

$script:InstallStep = "Create service"
Invoke-NativeChecked "sc.exe" "Creating service $serviceName" @(
    "create", $serviceName,
    "binPath=", "`"$serviceExe`"",
    "start=", "auto",
    "obj=", "LocalSystem",
    "DisplayName=", "EndpointAgents Service")
Invoke-NativeChecked "sc.exe" "Configuring recovery for service $serviceName" @(
    "failure", $serviceName,
    "reset=", "86400",
    "actions=", "restart/5000/restart/15000/restart/30000")

$script:InstallStep = "Protect folders"
New-Item -Path $runKey -Force | Out-Null
New-ItemProperty -Path $runKey -Name "EndpointAgents.Tray" `
    -PropertyType String -Value "`"$trayExe`"" -Force | Out-Null
New-SafeDirectory $endpointAgentsDataRoot
Set-ExactProtectedDirectoryAcl $endpointAgentsDataRoot -AllowUsersReadAndExecute
New-Item -ItemType Directory -Path "C:\ProgramData\EndpointAgents\Quarantine\Ibsng" `
    -Force | Out-Null
New-SafeDirectory $credentialRoot
New-SafeDirectory $officeRemediationRoot
New-SafeDirectory $windowsRemediationRoot
Set-ExactProtectedTreeAcl $credentialRoot
Set-ExactProtectedTreeAcl $officeRemediationRoot -AllowUsersReadAndExecute
Set-ExactProtectedTreeAcl $windowsRemediationRoot
New-SafeDirectory $configRoot
$siteSettings = [ordered]@{
    ControlPlane = [ordered]@{ HubUrl = $ControlPlaneHubUrl.AbsoluteUri }
    PackageSigning = [ordered]@{
        TrustedPublicKeys = @($PackageSigningPublicKey)
        RequireForActivationRepairs = [bool]$RequireSignedActivationRepairs
    }
}
[IO.File]::WriteAllText($siteSettingsPath, (($siteSettings | ConvertTo-Json -Depth 5) + [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false))
Set-ExactProtectedTreeAcl $configRoot -AllowUsersReadAndExecute
New-SafeDirectory $updatesRoot
Set-ExactProtectedTreeAcl $updatesRoot

$script:InstallStep = "Start service"
Start-Service -Name $serviceName

Write-Host "Endpoint Agent installed: the background service is running."
Write-Host "The Tray is registered to start for each user at their next interactive logon."
