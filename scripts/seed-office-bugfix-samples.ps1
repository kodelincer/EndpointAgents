[CmdletBinding()]
param(
    [string]$RepositoryRoot =
        "$env:ProgramData\EndpointAgents\ControlPlane\FileRepository",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$manifestPath = Join-Path $RepositoryRoot "office-bugfix-managers.json"
$notepadPath = Join-Path $env:WINDIR "System32\notepad.exe"
$samples = @(
    @{
        ArtifactId = "sample-office-bugfix-14"
        FileName = "officebugfixmanager_14.0.exe"
        MinVersion = "14.0"
        MaxVersionExclusive = "15.0"
    },
    @{
        ArtifactId = "sample-office-bugfix-15"
        FileName = "officebugfixmanager_15.0.exe"
        MinVersion = "15.0"
        MaxVersionExclusive = "16.0"
    },
    @{
        ArtifactId = "sample-office-bugfix-16"
        FileName = "officebugfixmanager_16.0.exe"
        MinVersion = "16.0"
        MaxVersionExclusive = "17.0"
    }
)

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

Assert-Administrator
if (-not (Test-Path -LiteralPath $notepadPath -PathType Leaf)) {
    throw "The harmless Notepad sample executable was not found at $notepadPath."
}

$samplePaths = $samples | ForEach-Object {
    Join-Path $RepositoryRoot $_.FileName
}
$hasSampleFiles = @($samplePaths | Where-Object {
    Test-Path -LiteralPath $_
}).Count -gt 0
$hasConfiguredArtifacts = $false
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    try {
        $current = Get-Content -LiteralPath $manifestPath -Raw |
            ConvertFrom-Json
        $hasConfiguredArtifacts = @($current.artifacts).Count -gt 0
    }
    catch {
        $hasConfiguredArtifacts = $true
    }
}
if (($hasSampleFiles -or $hasConfiguredArtifacts) -and -not $Force) {
    throw "Office bugfix artifacts already exist. Re-run with -Force only if replacing them is intentional."
}

New-Item -ItemType Directory -Path $RepositoryRoot -Force | Out-Null
$artifacts = foreach ($sample in $samples) {
    $destination = Join-Path $RepositoryRoot $sample.FileName
    Copy-Item -LiteralPath $notepadPath -Destination $destination -Force
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    [ordered]@{
        artifactId = $sample.ArtifactId
        displayName = "Sample Office bugfix manager $($sample.MinVersion)"
        description = "Sample only: opens Notepad and performs no Office repair."
        fileName = $sample.FileName
        sha256 = $hash
        minOfficeVersion = $sample.MinVersion
        maxOfficeVersionExclusive = $sample.MaxVersionExclusive
    }
}

[ordered]@{ version = 1; artifacts = @($artifacts) } |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $manifestPath -Encoding utf8

Write-Host "Seeded $($artifacts.Count) harmless Office bugfix samples."
Write-Host "Manifest: $manifestPath"
