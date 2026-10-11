<#
.SYNOPSIS
Starts the whole development system on this PC, one window per part.

.DESCRIPTION
Opens, in order: the control plane API (5052), the Hermes gateway for the endpointagents
profile (8642), the dashboard (3000), and the endpoint service + Tray. The endpoint is
enrolled with a one-time code the first time; its credential is kept in
%LOCALAPPDATA%\EndpointAgents\dev-device-credential.bin. Close a window to stop that part.
See docs\run-dev-stack.md.
#>
[CmdletBinding()]
param([int]$StartupTimeoutSeconds = 180)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Start-Part([string]$Title, [string]$Command) {
    $script = "`$host.UI.RawUI.WindowTitle = '$Title'; Set-Location '$repoRoot'; $Command"
    Start-Process powershell -ArgumentList "-NoExit", "-ExecutionPolicy", "Bypass", "-Command", $script
}

function Wait-Port([int]$Port, [string]$Name) {
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
            Write-Host "$Name is up on port $Port."
            return
        }
        Start-Sleep -Seconds 2
    }
    throw "$Name did not start on port $Port within $StartupTimeoutSeconds seconds; check its window."
}

if (Get-Process -Name "EndpointAgents.Service", "EndpointAgents.Tray" -ErrorAction SilentlyContinue) {
    throw "An EndpointAgents service or Tray is already running. Close it first."
}

Start-Part "ControlPlane API" "dotnet run --project .\src\ControlPlane.Api\ControlPlane.Api.csproj --launch-profile http"
Wait-Port 5052 "Control plane API"

Start-Part "Hermes gateway" "hermes -p endpointagents gateway run"
Start-Part "Dashboard" "Set-Location .\src\ControlPlane.Web; npm.cmd run dev"

$credential = Join-Path $env:LOCALAPPDATA "EndpointAgents\dev-device-credential.bin"
New-Item -ItemType Directory -Force -Path (Split-Path $credential) | Out-Null
$endpointEnv = "`$env:ControlPlane__DeviceCredentialPath = '$credential'; "
if (-not (Test-Path $credential)) {
    $code = (Invoke-RestMethod -Method Post http://127.0.0.1:5052/api/agent-enrollments).enrollmentCode
    $endpointEnv += "`$env:ControlPlane__EnrollmentCode = '$code'; "
    Write-Host "Enrolling this PC with a new one-time code."
}
Start-Part "Endpoint service + tray" ($endpointEnv + "& .\scripts\run-endpoint-dev.ps1")

Wait-Port 8642 "Hermes gateway"
Wait-Port 3000 "Dashboard"
Write-Host "Everything is starting. Dashboard: http://127.0.0.1:3000"
