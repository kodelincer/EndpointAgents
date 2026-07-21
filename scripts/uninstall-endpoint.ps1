[CmdletBinding()]
param(
    [string]$InstallRoot = "$env:ProgramFiles\EndpointAgents\Endpoint",
    [switch]$RemovePublishedFiles
)

$ErrorActionPreference = "Stop"
$serviceName = "EndpointAgents.Service"
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell session."
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $serviceName | Out-Null
}

Remove-ItemProperty -Path $runKey -Name "EndpointAgents.Tray" -ErrorAction SilentlyContinue

foreach ($eventSource in "EndpointAgents.Service", "EndpointAgents.Tray") {
    if ([Diagnostics.EventLog]::SourceExists($eventSource)) {
        [Diagnostics.EventLog]::DeleteEventSource($eventSource)
    }
}

if ($RemovePublishedFiles -and (Test-Path -LiteralPath $InstallRoot)) {
    $resolved = [IO.Path]::GetFullPath($InstallRoot)
    $programFiles = [IO.Path]::GetFullPath($env:ProgramFiles)
    if (-not $resolved.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallRoot must remain under Program Files."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

Write-Host "EndpointAgents Service and Tray startup registration removed."
