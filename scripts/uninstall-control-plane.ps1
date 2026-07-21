[CmdletBinding()]
param([switch]$RemovePublishedFiles)

$ErrorActionPreference = "Stop"
# Include the retired Web service so this script also cleans up older installs.
$services = @("EndpointAgents.ControlPlane.Api", "EndpointAgents.ControlPlane.Web")

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell session."
}

foreach ($name in $services) {
    $service = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($null -eq $service) { continue }
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $name -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $name | Out-Null
}

if ($RemovePublishedFiles) {
    $installRoot = [IO.Path]::GetFullPath("$env:ProgramFiles\EndpointAgents\ControlPlane")
    $programFiles = [IO.Path]::GetFullPath($env:ProgramFiles)
    if (-not $installRoot.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside Program Files."
    }
    if (Test-Path -LiteralPath $installRoot) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
}

Write-Host "Control plane service removed."
