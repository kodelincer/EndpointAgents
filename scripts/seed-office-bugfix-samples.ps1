[CmdletBinding()]
param(
    [string]$RepositoryRoot =
        "$env:ProgramData\EndpointAgents\ControlPlane\FileRepository",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot "deploy\control-plane\FileRepository\office-activation-repair.bat"
$destination = Join-Path $RepositoryRoot "office-activation-repair.bat"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

Assert-Administrator
if ((Test-Path -LiteralPath $destination) -and -not $Force) {
    throw "office-activation-repair.bat already exists. Use -Force only to replace it with the sample."
}

New-Item -ItemType Directory -Path $RepositoryRoot -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $destination -Force
$hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
Write-Host "Seeded harmless sample: $destination"
Write-Host "SHA-256: $hash"
