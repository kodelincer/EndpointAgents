<#
.SYNOPSIS
Turns a vendor installer (MSI or EXE) into a signed install package the support agent can run.

.DESCRIPTION
Creates <FileRepository>\packages\<Id>\ from deploy\package-templates\install-app.bat: a
BAT that checks whether the application is installed (its DisplayName in the Uninstall
registry keys), installs it silently for all users when it is missing, and checks again.
The installer is copied to files\ and pinned by SHA-256 with the BAT and playbook.

MSI: the product name, used for detection, is read from the MSI; it runs through
msiexec /qn. EXE: the script recognises the installer technology and picks its silent
switches:

  Inno Setup         /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
  NSIS (Nullsoft)    /S
  WiX Burn bundle    /quiet /norestart
  InstallShield      /s /v"/qn REBOOT=ReallySuppress"
  Squirrel           --silent
  Other              pass -SilentArguments from the vendor's documentation

Installers whose wizard asks questions:
  -RecordAnswers runs the wizard once on this admin PC (it really installs the app here)
  and records your answers: InstallShield setup.iss (/r /f1) or Inno Setup setup.inf
  (/SAVEINF). Endpoints then replay them silently (/s /f1 or /LOADINF).
  An installer with no silent mode cannot run unattended; see docs/software-install.md.

System requirements (checked on each PC before approval and again before installing):
  -MinWindowsBuild 19045 -MinMemoryGB 8 -MinFreeDiskGB 4 -MinCpuCores 2 -MinScreen 1366x768
  -Architectures x64 -Editions Professional,Enterprise -Runtimes vcredist-2015-x64,webview2
  -RequireDomain. Copy them from the vendor's "system requirements" page.
Approval: every install waits for a helpdesk member unless an admin puts it on the
"No approval needed" list (dashboard > Packages). -Licensed keeps it on approval always.

-Typed builds a package without a BAT: the PC runs the installer itself with compiled code
(software.install: msi, inno, nsis, wix-burn, installshield, squirrel or exe) and checks the
detection rule afterwards. Use it for every new package; the BAT template stays for
installers that need recorded answers. -Manual makes a package for an installer that cannot
run silently: the agent then creates a helpdesk task instead of installing.
-Provides marks a runtime package (for example vcredist-2015-x64), so applications that need
it get it installed first.

The package is created with a draft Persian symptom marked TODO; the control plane does not
publish it until you replace that line, then run with -Sign (or
test-bat-playbook.ps1 -Package <folder> -Fix -Sign).

.EXAMPLE
.\scripts\new-install-package.ps1 -Installer C:\Vendor\7z2408-x64.msi -Id 7zip-install -App "7-Zip" -Development

.EXAMPLE
.\scripts\new-install-package.ps1 -Installer C:\Vendor\npp.8.7.Installer.x64.exe -Id notepadpp-install `
    -App "Notepad++" -PersianSymptom "نوت‌پد پلاس پلاس نصب نیست"

.EXAMPLE
# A package that waits for its installer (place it later with set-package-files.ps1):
.\scripts\new-install-package.ps1 -InstallerName FoxitPDFReader.msi -Id foxit-reader-install `
    -App "Foxit PDF Reader" -Detect "Foxit PDF Reader" -DetectAlt "Foxit Reader" -Development
#>
[CmdletBinding(DefaultParameterSetName = "Installer")]
param(
    [Parameter(Mandatory, ParameterSetName = "Installer")] [string]$Installer,
    # Create the package without the installer; it is stored under this name later.
    [Parameter(Mandatory, ParameterSetName = "Placeholder")] [string]$InstallerName,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9._-]{0,55}-install$')] [string]$Id,
    # The application's name as users and the agent know it.
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9 .+()_-]{2,60}$')] [string]$App,
    # Text in the installed product's DisplayName; defaults to the MSI ProductName or EXE ProductName.
    [string]$Detect,
    [string]$DetectAlt,
    # A raw findstr /r pattern in place of -Detect, for names that need it, such as
    # 'REG_SZ *Adobe Connect *[0-9.]*$' (so "Adobe Connect Add-in" does not count).
    [string]$DetectPattern,
    [ValidateSet("msi", "inno", "nsis", "burn", "installshield", "squirrel", "exe")] [string]$Technology,
    [string]$SilentArguments,
    # Fixed MSI properties such as ALLUSERS=1 or a vendor's AUTO_UPDATE=0.
    [string]$MsiProperties,
    [switch]$RecordAnswers,
    [string[]]$Symptoms,
    [string]$PersianSymptom,
    [string]$MinWindowsVersion = "10.0.10240",
    [ValidateRange(60, 3600)] [int]$TimeoutSeconds = 900,
    # System requirements (software.requirements).
    [ValidateRange(10240, 99999)] [int]$MinWindowsBuild,
    [ValidateRange(1, 1024)] [double]$MinMemoryGB,
    [ValidateRange(1, 10240)] [double]$MinFreeDiskGB,
    [ValidateRange(1, 256)] [int]$MinCpuCores,
    [ValidatePattern('^\d{3,5}x\d{3,5}$')] [string]$MinScreen,
    [ValidateSet("x64", "x86", "arm64")] [string[]]$Architectures,
    [string[]]$Editions,
    [ValidateSet("dotnet-desktop-6", "dotnet-desktop-8", "dotnet-desktop-9", "dotnet-desktop-10",
        "vcredist-2015-x64", "vcredist-2015-x86", "webview2", "java-8")] [string[]]$Runtimes,
    [switch]$RequireDomain,
    [switch]$Licensed,
    [ValidateSet("dotnet-desktop-6", "dotnet-desktop-8", "dotnet-desktop-9", "dotnet-desktop-10",
        "vcredist-2015-x64", "vcredist-2015-x86", "webview2", "java-8")] [string]$Provides,
    # The PC runs the installer itself (no BAT); see the description.
    [switch]$Typed,
    [string]$Manual,
    [string]$RepositoryRoot = "$env:ProgramData\EndpointAgents\ControlPlane\FileRepository",
    [switch]$Development,
    [switch]$Sign
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Development) { $RepositoryRoot = Join-Path $repoRoot "deploy\control-plane\FileRepository" }
$folder = Join-Path $RepositoryRoot "packages\$Id"
if (Test-Path -LiteralPath $folder) { throw "The package folder already exists: $folder" }

function Get-MsiProperty([string]$Path, [string]$Name) {
    $msi = New-Object -ComObject WindowsInstaller.Installer
    $database = $msi.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $msi, @($Path, 0))
    $view = $database.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $database,
        @("SELECT Value FROM Property WHERE Property = '$Name'"))
    $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
    $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
    if ($null -eq $record) { return $null }
    $record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, 1)
}

function Get-ExeTechnology([string]$Path) {
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    $stream = [IO.File]::OpenRead($Path)
    try {
        $buffer = New-Object byte[] ([Math]::Min($stream.Length, 16MB))
        $read = $stream.Read($buffer, 0, $buffer.Length)
    } finally { $stream.Dispose() }
    $text = [Text.Encoding]::GetEncoding(28591).GetString($buffer, 0, $read)
    if ("$($info.Comments)" -match 'Inno Setup' -or $text.Contains("Inno Setup Setup Data")) { return "inno" }
    if ($text.Contains("Nullsoft.NSIS") -or $text.Contains("NullsoftInst")) { return "nsis" }
    if ($text.Contains(".wixburn")) { return "burn" }
    if ("$($info.ProductName)$($info.LegalCopyright)$($info.FileDescription)" -match 'InstallShield' `
        -or $text.Contains("InstallShield")) { return "installshield" }
    if ($text.Contains("SquirrelSetup") -or $text.Contains("Squirrel.Windows")) { return "squirrel" }
    return "exe"
}

# A DisplayName that starts with the name as a whole word: "Git" matches "Git 2.56"
# but not "GitHub Desktop". findstr /r special characters are escaped.
function ConvertTo-FindstrPattern([string]$Text) {
    $name = $Text.Trim()
    $pattern = "REG_SZ *" + ([regex]::Replace($name, '[\\.\*\[\]\^\$]', '\$0'))
    if ($name -match '\w$') { $pattern += '\>' }
    $pattern
}

function Assert-BatValue([string]$Name, [string]$Value, [switch]$AllowQuotes) {
    $bad = if ($AllowQuotes) { '[&|<>^!\r\n]' } else { '[&|<>^!"\r\n]' }
    if ($Value -match $bad -or ($Value -replace '%~dp0', '') -match '%') {
        throw "$Name contains a character that cmd.exe would interpret: $Value"
    }
}

# --- Work out the installer kind, detection and arguments --------------------------------
if ($PSCmdlet.ParameterSetName -eq "Installer") {
    $source = (Resolve-Path -LiteralPath $Installer).Path
    # Package file names allow letters, digits, '.', '_' and '-' only.
    $InstallerName = (Split-Path -Leaf $source) -replace '[^A-Za-z0-9._-]', '-'
    $signature = Get-AuthenticodeSignature -LiteralPath $source
    if ($signature.Status -ne "Valid") {
        throw "The installer is not validly signed ($($signature.Status)). Only vendor-signed installers can be packaged."
    }
    Write-Host "Signed by:        $($signature.SignerCertificate.Subject.Split(',')[0])"
}
if ($InstallerName -notmatch '^[A-Za-z0-9._-]{1,128}$') {
    throw "Rename the installer to letters, digits, '.', '_' and '-' only (for example $($Id -replace '-install$','')-setup.exe)."
}
$extension = [IO.Path]::GetExtension($InstallerName).ToLowerInvariant()
if ($extension -notin ".msi", ".exe") { throw "Only .msi and .exe installers are supported." }
$kind = $extension.TrimStart(".")
if (-not $Technology) {
    $Technology = if ($kind -eq "msi") { "msi" } elseif ($source) { Get-ExeTechnology $source } else { "exe" }
}
if (($kind -eq "msi") -ne ($Technology -eq "msi")) { throw "-Technology msi needs an .msi installer, and an .msi needs -Technology msi." }
Write-Host "Technology:       $Technology"

if (-not $Detect -and -not $DetectPattern -and $source) {
    $Detect = if ($kind -eq "msi") { Get-MsiProperty $source "ProductName" }
        else { (Get-Item -LiteralPath $source).VersionInfo.ProductName }
    # Version numbers change with every release; detect on the name only.
    if ($Detect) { $Detect = ($Detect -replace '\s*\(?(x64|x86|64-bit|32-bit)\)?', '' -replace '\s+v?\d+(\.\d+)+.*$', '').Trim() }
}
if (-not $Detect -and -not $DetectPattern) { throw "Could not read the product name; pass -Detect with text from the installed DisplayName (Apps & features)." }
Write-Host "Detects:          $(if ($DetectPattern) { "findstr pattern $DetectPattern" } else { "DisplayName starting with '$Detect'" })"

$answerFile = $null
if ($RecordAnswers) {
    if (-not $source) { throw "-RecordAnswers needs -Installer." }
    $answerFile = switch ($Technology) {
        "installshield" { "setup.iss" }
        "inno" { "setup.inf" }
        default { throw "-RecordAnswers works for InstallShield and Inno Setup installers only." }
    }
}

if ($kind -eq "msi") {
    $arguments = "$MsiProperties".Trim()
} elseif ($SilentArguments) {
    $arguments = $SilentArguments.Trim()
} else {
    $arguments = switch ($Technology) {
        "inno" { if ($answerFile) { '/LOADINF="%~dp0setup.inf" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-' }
                 else { "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-" } }
        "nsis" { "/S" }
        "burn" { "/quiet /norestart" }
        "installshield" { if ($answerFile) { '/s /f1"%~dp0setup.iss"' } else { '/s /v"/qn REBOOT=ReallySuppress"' } }
        "squirrel" { "--silent" }
        default { throw "Unknown EXE installer: pass -SilentArguments from the vendor's silent-install documentation, or -RecordAnswers for InstallShield or Inno Setup." }
    }
}
Assert-BatValue "Arguments" $arguments -AllowQuotes
Assert-BatValue "App" $App
$detectPattern = if ($DetectPattern) { $DetectPattern } else { ConvertTo-FindstrPattern $Detect }
$detectAltPattern = if ($DetectAlt) { ConvertTo-FindstrPattern $DetectAlt } else { $detectPattern }
foreach ($pattern in $detectPattern, $detectAltPattern) {
    if ($pattern -match '["%\r\n]') { throw "A detection pattern cannot contain quotes or %: $pattern" }
}
Write-Host "Install command:  $(if ($kind -eq 'msi') { "msiexec /i $InstallerName /qn /norestart $arguments" } else { "$InstallerName $arguments" })"

# --- The signed software section: requirements, typed installer, detection ----------------
$requirements = [ordered]@{}
if ($MinWindowsBuild) { $requirements.minWindowsBuild = $MinWindowsBuild }
if ($MinMemoryGB) { $requirements.minMemoryMb = [int]($MinMemoryGB * 1024) }
if ($MinFreeDiskGB) { $requirements.minFreeDiskMb = [int]($MinFreeDiskGB * 1024) }
if ($MinCpuCores) { $requirements.minCpuCores = $MinCpuCores }
if ($MinScreen) { $w, $h = $MinScreen.Split("x"); $requirements.minScreenWidth = [int]$w; $requirements.minScreenHeight = [int]$h }
if ($Architectures) { $requirements.architectures = @($Architectures) }
if ($Editions) { $requirements.editions = @($Editions) }
if ($Runtimes) { $requirements.runtimes = @($Runtimes) }
if ($RequireDomain) { $requirements.domainJoined = $true }
$software = [ordered]@{ name = $App; licensed = [bool]$Licensed }
if ($requirements.Count -gt 0) { $software.requirements = $requirements }
if ($Provides) { $software.provides = $Provides }
if ($Detect) { $software.detection = @([ordered]@{ kind = "uninstall-entry"; displayName = $Detect.Trim() }) }
if ($Typed -or $Manual) {
    if ($answerFile -or $arguments -match '%~dp0') { throw "-Typed cannot replay recorded answers; leave out -Typed to use the BAT template." }
    if (-not $Detect -and -not $Manual) { throw "-Typed needs -Detect (DisplayName text), so the PC can tell whether the install worked." }
    $installKind = if ($Manual) { "manual" } elseif ($Technology -eq "burn") { "wix-burn" } else { $Technology }
    $install = [ordered]@{ kind = $installKind }
    if ($Manual) { $install.manualReason = $Manual }
    elseif ($installKind -eq "msi" -and $MsiProperties) {
        $props = [ordered]@{}
        foreach ($pair in ($MsiProperties -split '\s+' | Where-Object { $_ })) {
            $name, $value = $pair.Split("=", 2); $props[$name] = $value.Trim('"')
        }
        $install.msiProperties = $props
    }
    elseif ($installKind -eq "exe" -or $SilentArguments) { $install.arguments = @($arguments -split '\s+' | Where-Object { $_ }) }
    else { $install.timeoutSeconds = $TimeoutSeconds }
    if (-not $install.Contains("timeoutSeconds") -and -not $Manual) { $install.timeoutSeconds = $TimeoutSeconds }
    $software.install = $install
}

# --- Write the package ---------------------------------------------------------------------
$template = Get-Content -LiteralPath (Join-Path $repoRoot "deploy\package-templates\install-app.bat") -Raw
$bat = $template.Replace("@@APP@@", $App).Replace("@@KIND@@", $kind).Replace("@@INSTALLER@@", $InstallerName).
    Replace("@@INSTALL_ARGS@@", $arguments).Replace("@@DETECT@@", $detectPattern).
    Replace("@@DETECT_ALT@@", $detectAltPattern).Replace("@@LOG@@", "$Id.log")
if ($bat.Contains("@@")) { throw "The template has a placeholder this script does not fill." }
$bat = $bat -replace "`r?`n", "`r`n"

New-Item -ItemType Directory -Path (Join-Path $folder "files") -Force | Out-Null
try {
    $batName = "$Id.bat"
    [IO.File]::WriteAllText((Join-Path $folder $batName), $bat, [Text.Encoding]::ASCII)
    $support = @()
    if ($source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $folder "files\$InstallerName")
        if ($answerFile) {
            $answerPath = Join-Path $folder "files\$answerFile"
            Write-Host "Recording:        complete the wizard as endpoints should install the app (this installs it on this PC)."
            $record = if ($Technology -eq "installshield") { @("/r", "/f1`"$answerPath`"") } else { @("/SAVEINF=`"$answerPath`"") }
            $process = Start-Process -FilePath $source -ArgumentList $record -Wait -PassThru
            if (-not (Test-Path -LiteralPath $answerPath)) { throw "The wizard did not save its answers (exit code $($process.ExitCode))." }
            Write-Host "Recorded:         $answerFile"
        }
        $support = @(Get-ChildItem -LiteralPath (Join-Path $folder "files") -File | Sort-Object Name | ForEach-Object {
            [ordered]@{ fileName = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    } else {
        $support = @([ordered]@{ fileName = $InstallerName; sha256 = "0" * 64 })
    }

    if (-not $Symptoms) { $Symptoms = @("$App is not installed", "I need $App installed on my PC") }
    if (-not $PersianSymptom) { $PersianSymptom = "TODO: how a user asks for $App in Persian" }
    $package = [ordered]@{
        schemaVersion = 2
        id = $Id
        displayName = "Install $App"
        description = "Checks whether $App is installed and, if not, installs it silently for all users. Exit code 10 means it was already installed."
        problem = "install-" + ($Id -replace '-install$', '')
        order = 100
        symptoms = @($Symptoms) + @($PersianSymptom)
        file = [ordered]@{ type = "bat"; fileName = $batName; sha256 = (Get-FileHash -LiteralPath (Join-Path $folder $batName) -Algorithm SHA256).Hash; publisher = $null }
        software = $software
        compatibility = [ordered]@{ minWindowsVersion = $MinWindowsVersion; maxWindowsVersionExclusive = $null }
        supportFiles = $support
        playbook = [ordered]@{
            version = 3; arguments = @(); steps = @(); timeoutSeconds = $TimeoutSeconds
            successExitCodes = @(0, 10); restartExitCodes = @(3010)
            successMarker = "$App install completed."; failureMarkers = @("ERROR:")
        }
        playbookSha256 = ""
        review = [ordered]@{
            reviewedBy = "TODO: your name"; reviewedOn = (Get-Date -Format "yyyy-MM-dd")
            source = if ($source) { "TODO: where $InstallerName came from" } else { "TODO: where the installer comes from" }
            notes = "Generated by new-install-package.ps1 ($Technology)."
        }
    }
    if ($Typed -and -not $Manual) {
        # The installer itself is the package file; no BAT, no supporting files.
        Remove-Item -LiteralPath (Join-Path $folder $batName) -Force
        $installerPath = Join-Path $folder "files\$InstallerName"
        if (Test-Path -LiteralPath $installerPath) { Move-Item -LiteralPath $installerPath -Destination (Join-Path $folder $InstallerName) }
        $hash = if (Test-Path -LiteralPath (Join-Path $folder $InstallerName)) { (Get-FileHash -LiteralPath (Join-Path $folder $InstallerName) -Algorithm SHA256).Hash } else { "0" * 64 }
        $publisher = if ($signature) { $signature.SignerCertificate.Subject } else { "TODO: the installer's signer subject" }
        $package.file = [ordered]@{ type = $kind; fileName = $InstallerName; sha256 = $hash; publisher = $publisher }
        $package.supportFiles = @()
        $package.playbook = [ordered]@{ version = 3; arguments = @(); steps = @(); timeoutSeconds = $TimeoutSeconds }
        $package.description = "Checks this PC's system requirements and whether $App is installed and, if not, installs it silently for all users."
    }
    $json = $package | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText((Join-Path $folder "package.json"), $json + "`n", (New-Object Text.UTF8Encoding $false))
} catch {
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
    throw
}

Write-Host ""
Write-Host "Created $folder"
Write-Host "Next: replace every TODO in package.json, then:"
Write-Host "  .\scripts\test-bat-playbook.ps1 -Package `"$folder`" -Fix -Sign"
Write-Host "Optional: add a row for '$App' to deploy\hermes\skills\software-install\SKILL.md and run scripts\sync-hermes-profile.ps1."
$lab = Join-Path $PSScriptRoot "test-bat-playbook.ps1"
if ($Sign) { & $lab -Package $folder -Fix -Sign } else { & $lab -Package $folder -Validate }
