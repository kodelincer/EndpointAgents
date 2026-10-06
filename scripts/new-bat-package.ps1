<#
.SYNOPSIS
Creates a new BAT package folder with a draft package.json.

.DESCRIPTION
Creates <FileRepository>\packages\<Id>\, copies the BAT into it, records its SHA-256,
and writes a draft package.json with TODO markers for everything a person must fill
in (names, playbook screens, success text, compatibility, review). The control plane
does not publish a package while any TODO marker remains. The BAT is not run.
The script then validates the folder and prints what is left to do.

.EXAMPLE
.\scripts\new-bat-package.ps1 -Bat .\deploy\samples\menu-repair-sample.bat `
    -Id menu-repair-sample -Kind office -Development
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Bat,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9._-]{0,63}$')] [string]$Id,
    [Parameter(Mandatory)] [ValidateSet("office", "windows")] [string]$Kind,
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
$source = (Resolve-Path -LiteralPath $Bat).Path
if ([IO.Path]::GetExtension($source) -ne ".bat") { throw "Only .bat files can be packaged." }

$folder = Join-Path $RepositoryRoot "packages\$Id"
if (Test-Path -LiteralPath $folder) { throw "The package folder already exists: $folder" }
New-Item -ItemType Directory -Path $folder | Out-Null
$batName = Split-Path -Leaf $source
Copy-Item -LiteralPath $source -Destination (Join-Path $folder $batName)
$batHash = (Get-FileHash -LiteralPath (Join-Path $folder $batName) -Algorithm SHA256).Hash

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
    bat = [ordered]@{ fileName = $batName; sha256 = $batHash }
    supportFiles = @()
    playbook = [ordered]@{
        version = 2
        steps = @(
            [ordered]@{
                expect = "TODO: short piece of the first prompt text"
                send = "1"
                submit = "line"
                timeoutSeconds = 60
            }
        )
        successExitCodes = @(0)
        successMarker = "TODO: text that only appears when the repair worked"
        failureMarkers = @()
    }
    playbookSha256 = ""
    $Kind = $compatibility
    review = [ordered]@{
        reviewedBy = "TODO: your name"
        reviewedOn = (Get-Date -Format "yyyy-MM-dd")
        source = "TODO: where the BAT came from"
        notes = ""
    }
}
$json = ($package | ConvertTo-Json -Depth 10) + [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $folder "package.json"), $json,
    [Text.UTF8Encoding]::new($false))

Write-Host "Created $folder"
Write-Host "Next: edit package.json, then discover the screens one round at a time:"
Write-Host "  .\scripts\test-bat-playbook.ps1 -Package `"$folder`" -Execute -Discover"
Write-Host ""
& (Join-Path $PSScriptRoot "test-bat-playbook.ps1") -Package $folder
exit 0
