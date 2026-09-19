[CmdletBinding()]
param(
    [string]$RepositoryRoot =
        "$env:ProgramData\EndpointAgents\ControlPlane\FileRepository",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactId = "office-activation-repair"
$fileName = "$artifactId.bat"
$source = Join-Path $repoRoot "deploy\control-plane\FileRepository\$fileName"
$destination = Join-Path $RepositoryRoot $fileName
$manifestPath = Join-Path $RepositoryRoot "office-bugfix-managers.json"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

Assert-Administrator
if (((Test-Path -LiteralPath $destination) `
        -or (Test-Path -LiteralPath $manifestPath)) -and -not $Force) {
    throw "The Office test artifact or manifest already exists. Use -Force to back up and replace the manifest with the harmless 16.x sample."
}

New-Item -ItemType Directory -Path $RepositoryRoot -Force | Out-Null
if (Test-Path -LiteralPath $manifestPath) {
    $timestamp = Get-Date -Format "yyyyMMddHHmmssfff"
    $backupPath = "$manifestPath.$timestamp.bak"
    Copy-Item -LiteralPath $manifestPath -Destination $backupPath
    Write-Host "Backed up existing manifest: $backupPath"
}

Copy-Item -LiteralPath $source -Destination $destination -Force
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
$manifest = [ordered]@{
    version = 1
    artifacts = @(
        [ordered]@{
            artifactId = $artifactId
            displayName = "Office activation repair test"
            description = "Harmless test BAT for the automatic Office activation repair flow."
            fileName = $fileName
            sha256 = $hash
            minOfficeVersion = "16.0"
            maxOfficeVersionExclusive = "17.0"
            playbook = [ordered]@{
                version = 1
                steps = @(
                    [ordered]@{
                        expect = "Select an item"
                        send = "1"
                        submit = "line"
                        timeoutSeconds = 30
                    },
                    [ordered]@{
                        expect = "Continue with harmless sample? [Y/N]"
                        send = "Y"
                        submit = "key"
                        timeoutSeconds = 30
                    }
                )
            }
        }
    )
}
$temporaryManifestPath = "$manifestPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $json = ($manifest | ConvertTo-Json -Depth 10) + [Environment]::NewLine
    [IO.File]::WriteAllText($temporaryManifestPath, $json,
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryManifestPath -Destination $manifestPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryManifestPath) {
        Remove-Item -LiteralPath $temporaryManifestPath -Force
    }
}

Write-Host "Seeded harmless Office 16.x sample: $destination"
Write-Host "Manifest: $manifestPath"
Write-Host "SHA-256: $hash"
