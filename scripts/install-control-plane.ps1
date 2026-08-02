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
$fileRepositoryRoot = Join-Path $env:ProgramData "EndpointAgents\ControlPlane\FileRepository"
$dataRoot = Join-Path $env:ProgramData "EndpointAgents\ControlPlane"
$officeRepositoryRoot = Join-Path $fileRepositoryRoot "Office"
$officeManifestPath = Join-Path $officeRepositoryRoot "office-artifacts.json"
$officeBugfixPath = Join-Path $fileRepositoryRoot "office-activation-repair.bat"
$sampleOfficeBugfixPath = Join-Path $repoRoot "deploy\control-plane\FileRepository\office-activation-repair.bat"

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
    & sc.exe delete $Name | Out-Null
}

Assert-Administrator

# Remove the retired supervisor during upgrades from the former two-service layout.
Remove-ServiceIfPresent $legacyWebService
Remove-ServiceIfPresent $apiService

if (Test-Path -LiteralPath $InstallRoot) {
    $resolved = [IO.Path]::GetFullPath($InstallRoot)
    $programFiles = [IO.Path]::GetFullPath($env:ProgramFiles)
    if (-not $resolved.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallRoot must remain under Program Files."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

dotnet publish (Join-Path $repoRoot "src\ControlPlane.Api\ControlPlane.Api.csproj") `
    -c $Configuration -r win-x64 --self-contained false -o $apiOutput

New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $officeRepositoryRoot -Force | Out-Null
& icacls.exe $dataRoot /inheritance:r `
    /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" /T | Out-Null
if (-not (Test-Path -LiteralPath $officeManifestPath)) {
    @{ version = 1; artifacts = @() } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $officeManifestPath -Encoding utf8
}
if (-not (Test-Path -LiteralPath $officeBugfixPath)) {
    Copy-Item -LiteralPath $sampleOfficeBugfixPath -Destination $officeBugfixPath
}

$apiExe = Join-Path $apiOutput "ControlPlane.Api.exe"

& sc.exe create $apiService binPath= "`"$apiExe`"" start= auto DisplayName= "EndpointAgents Control Plane API" | Out-Null
& sc.exe failure $apiService reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null

Start-Service -Name $apiService

Write-Host "Control plane API installed on http://127.0.0.1:5052."
Write-Host "Office remediation manifest: $officeManifestPath"
Write-Host "Office bugfix file: $officeBugfixPath"
Write-Host "SQLite chat database: $(Join-Path $dataRoot 'control-plane.db')"
Write-Host "Install the pinned Hermes runtime next with scripts\install-hermes-service.ps1."
Write-Host "ControlPlane.Web is separate; start it from src\ControlPlane.Web with npm.cmd run start."
