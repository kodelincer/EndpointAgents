[CmdletBinding()]
param(
    [string]$RepositoryRoot =
        "$env:ProgramData\EndpointAgents\ControlPlane\FileRepository",
    [switch]$Force,
    [switch]$Development
)

# Installs the harmless office-activation-repair sample package (a menu demo that
# changes nothing) and pins this machine's Click-to-Run product IDs in its
# compatibility, so the sample is offered for the local Office installation.

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageId = "office-activation-repair"
$sourceFolder = Join-Path $repoRoot "deploy\control-plane\FileRepository\packages\$packageId"
if ($Development) {
    if ($PSBoundParameters.ContainsKey("RepositoryRoot")) {
        throw "-Development uses the repository's fixed development FileRepository path."
    }
    $RepositoryRoot = Join-Path $repoRoot "deploy\control-plane\FileRepository"
} else {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}
$folder = Join-Path $RepositoryRoot "packages\$packageId"
$sameFolder = [IO.Path]::GetFullPath($sourceFolder) -eq [IO.Path]::GetFullPath($folder)

if (-not $sameFolder) {
    if ((Test-Path -LiteralPath $folder) -and -not $Force) {
        throw "The sample package already exists: $folder. Use -Force to replace it."
    }
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceFolder "*") -Destination $folder -Force
}

$configuration = Get-ItemProperty `
    -Path "HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration" `
    -ErrorAction SilentlyContinue
if ($configuration.ProductReleaseIds) {
    # Compatibility is not covered by the pinned hashes, so this keeps the package valid.
    $manifestPath = Join-Path $folder "package.json"
    $package = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $package.office.officeProductReleaseIds = @($configuration.ProductReleaseIds -split '[,;]' |
        ForEach-Object { $_.Trim() } | Where-Object { $_ })
    [IO.File]::WriteAllText($manifestPath,
        ($package | ConvertTo-Json -Depth 10) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

Write-Host "Seeded harmless Office 16.x menu demo package (not a repair): $folder"
