[CmdletBinding()]
param(
    [string]$InstallRoot = "$env:ProgramFiles\EndpointAgents\ControlPlane",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$apiService = "EndpointAgents.ControlPlane.Api"
$webService = "EndpointAgents.ControlPlane.Web"
$repoRoot = Split-Path -Parent $PSScriptRoot
$apiOutput = Join-Path $InstallRoot "Api"
$webOutput = Join-Path $InstallRoot "Web"

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

Push-Location (Join-Path $repoRoot "src\ControlPlane.Web")
try {
    npm.cmd ci
    npm.cmd run build
}
finally {
    Pop-Location
}

Remove-ServiceIfPresent $webService
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
dotnet publish (Join-Path $repoRoot "src\ControlPlane.WebHost\ControlPlane.WebHost.csproj") `
    -c $Configuration -r win-x64 --self-contained false -o $webOutput

$apiExe = Join-Path $apiOutput "ControlPlane.Api.exe"
$webExe = Join-Path $webOutput "ControlPlane.WebHost.exe"

& sc.exe create $apiService binPath= "`"$apiExe`"" start= auto DisplayName= "EndpointAgents Control Plane API" | Out-Null
& sc.exe create $webService binPath= "`"$webExe`"" start= auto DisplayName= "EndpointAgents Control Plane Web" | Out-Null
& sc.exe failure $apiService reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null
& sc.exe failure $webService reset= 86400 actions= restart/5000/restart/15000/restart/30000 | Out-Null

Start-Service -Name $apiService
Start-Service -Name $webService

Write-Host "Control plane installed. Open http://127.0.0.1:3000"
