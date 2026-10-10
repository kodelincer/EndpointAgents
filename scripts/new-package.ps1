<#
.SYNOPSIS
Creates a new package folder (BAT or EXE) with a draft package.json.

.DESCRIPTION
Creates <FileRepository>\packages\<Id>\, copies the file into it, records its SHA-256,
and writes a draft package.json with TODO markers for everything a person must fill
in. The control plane does not publish a package while any TODO marker remains. The
file is not run. The script then validates the folder and prints what is left to do.

-Kind general (the default) creates a schema 2 repair package: a BAT or EXE, the
symptoms the support agent matches user requests against, and a version 3 playbook.
-Kind office or windows creates a schema 1 BAT package for the activation catalogs.

-NoInput writes an empty step list: the file gets no answers, its input is closed at
start, and it just runs to the end. Without it the playbook starts with one TODO step
for the first prompt; use the playbook lab with -Execute -Discover to fill it in.

.EXAMPLE
.\scripts\new-package.ps1 -File .\deploy\samples\no-input-repair-sample.bat `
    -Id no-input-repair-sample -NoInput -Development

.EXAMPLE
.\scripts\new-package.ps1 -File C:\Vendor\SpoolerFix.exe -Id spooler-fix -NoInput
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [Alias("Bat")] [string]$File,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9._-]{0,63}$')] [string]$Id,
    [ValidateSet("general", "office", "windows")] [string]$Kind = "general",
    [switch]$NoInput,
    [string]$RepositoryRoot = "$env:ProgramData\EndpointAgents\ControlPlane\FileRepository",
    # Use the repository's development FileRepository (deploy\control-plane\FileRepository).
    [switch]$Development
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Development) {
    $RepositoryRoot = Join-Path $repoRoot "deploy\control-plane\FileRepository"
}
if ($Id -notmatch '[a-z0-9]$') { throw "The id must end with a letter or number." }
$source = (Resolve-Path -LiteralPath $File).Path
$type = [IO.Path]::GetExtension($source).TrimStart(".").ToLowerInvariant()
if ($type -notin @("bat", "exe")) { throw "Only .bat and .exe files can be packaged." }
if ($Kind -ne "general" -and $type -ne "bat") {
    throw "Office and Windows activation packages must be .bat files; use -Kind general for an EXE."
}

$folder = Join-Path $RepositoryRoot "packages\$Id"
if (Test-Path -LiteralPath $folder) { throw "The package folder already exists: $folder" }
New-Item -ItemType Directory -Path $folder | Out-Null
$fileName = Split-Path -Leaf $source
Copy-Item -LiteralPath $source -Destination (Join-Path $folder $fileName)
$fileHash = (Get-FileHash -LiteralPath (Join-Path $folder $fileName) -Algorithm SHA256).Hash

# @( if ... ) keeps an empty array; assigning @() straight from an if gives $null.
$steps = @(if (-not $NoInput) {
    [ordered]@{
        expect = "TODO: short piece of the first prompt text"
        send = "1"
        submit = "line"
        timeoutSeconds = 60
    }
})
$review = [ordered]@{
    reviewedBy = "TODO: your name"
    reviewedOn = (Get-Date -Format "yyyy-MM-dd")
    source = "TODO: where the file came from"
    notes = ""
}

if ($Kind -eq "general") {
    $signer = $null
    if ($type -eq "exe") {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $folder $fileName)
        if ($signature.Status -eq "Valid") { $signer = $signature.SignerCertificate.Subject }
    }
    $package = [ordered]@{
        schemaVersion = 2
        id = $Id
        displayName = "TODO: short name shown to operators"
        description = "TODO: what this repair does and when to use it"
        symptoms = @(
            "TODO: how a user would describe the problem",
            "TODO: another way to say it (Persian is fine)"
        )
        file = [ordered]@{
            type = $type
            fileName = $fileName
            sha256 = $fileHash
            publisher = $signer
        }
        compatibility = [ordered]@{
            minWindowsVersion = "10.0.17763"
            maxWindowsVersionExclusive = $null
        }
        supportFiles = @()
        playbook = [ordered]@{
            version = 3
            arguments = @()
            steps = $steps
            timeoutSeconds = 480
            successExitCodes = @(0)
            restartExitCodes = @()
            successMarker = $null
            failureMarkers = @()
        }
        playbookSha256 = ""
        review = $review
    }
} else {
    $compatibility = if ($Kind -eq "office") {
        [ordered]@{
            minOfficeVersion = "16.0"
            maxOfficeVersionExclusive = "17.0"
            officeProductReleaseIds = @()
        }
    } else {
        [ordered]@{
            minWindowsVersion = "10.0.19041"
            maxWindowsVersionExclusive = "10.0.30000"
            editionIds = @()
        }
    }
    $package = [ordered]@{
        schemaVersion = 1
        id = $Id
        displayName = "TODO: short name shown to operators"
        description = "TODO: what this repair does and when to use it"
        bat = [ordered]@{ fileName = $fileName; sha256 = $fileHash }
        supportFiles = @()
        playbook = [ordered]@{
            version = if ($NoInput) { 3 } else { 2 }
            steps = $steps
            successExitCodes = @(0)
            successMarker = "TODO: text that only appears when the repair worked"
            failureMarkers = @()
        }
        playbookSha256 = ""
        $Kind = $compatibility
        review = $review
    }
}
$json = ($package | ConvertTo-Json -Depth 10) + [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $folder "package.json"), $json,
    [Text.UTF8Encoding]::new($false))

Write-Host "Created $folder"
if ($type -eq "exe") {
    Write-Host ($(if ($signer) { "Signed by: $signer (pinned in file.publisher)" }
        else { "The EXE has no valid embedded signature; file.publisher is left empty." }))
}
Write-Host "Next: replace every TODO in package.json, then try the package:"
Write-Host "  .\scripts\test-bat-playbook.ps1 -Package `"$folder`" -Execute$(if (-not $NoInput) { ' -Discover' })"
Write-Host "and pin the reviewed hashes with -Fix."
Write-Host ""
& (Join-Path $PSScriptRoot "test-bat-playbook.ps1") -Package $folder
exit 0
