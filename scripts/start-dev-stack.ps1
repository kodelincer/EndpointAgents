<#
.SYNOPSIS
Starts the whole development system on this PC, one window per part.

.DESCRIPTION
Opens, in order: the control plane API (5052), the Hermes gateway for the endpointagents
profile (8642), the dashboard (3000), and the endpoint service + Tray. The endpoint is
enrolled with a one-time code the first time; its credential is kept in
%LOCALAPPDATA%\EndpointAgents\dev-device-credential.bin. Close a window to stop that part.
With -Panes the parts share one Windows Terminal window named "endpointagents", split in four.
See docs\run-dev-stack.md.
#>
[CmdletBinding()]
param(
    [int]$StartupTimeoutSeconds = 180,
    [switch]$Panes
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ($Panes -and -not (Get-Command wt.exe -ErrorAction SilentlyContinue)) {
    throw "-Panes needs Windows Terminal (wt.exe), which was not found."
}
# Each new part splits the pane before it, ending in a two-by-two grid.
$paneLayouts = [Collections.Generic.Queue[string]]::new([string[]]@(
    "new-tab", "split-pane -V", "split-pane -H", "move-focus left ; split-pane -H"))

function Start-Part([string]$Title, [string]$Command) {
    $script = "`$host.UI.RawUI.WindowTitle = '$Title'; Set-Location '$repoRoot'; $Command"
    if ($Panes) {
        # Encoded because wt.exe treats a semicolon anywhere in its arguments as a new command.
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
        Start-Process wt.exe -ArgumentList ("-w endpointagents $($paneLayouts.Dequeue()) --title `"$Title`" " +
            "powershell -NoExit -ExecutionPolicy Bypass -EncodedCommand $encoded")
        Start-Sleep -Seconds 2 # Let the window take this pane before the next call targets it.
        return
    }
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

if (Get-Process -Name "EndpointAgents.Service", "EndpointAgents.Tray", "EndpointAgents.TrayWeb" -ErrorAction SilentlyContinue) {
    throw "An EndpointAgents service or Tray is already running. Close it first."
}

Start-Part "ControlPlane API" "dotnet run --project .\src\ControlPlane.Api\ControlPlane.Api.csproj --launch-profile http"
Wait-Port 5052 "Control plane API"

# A gateway left over from an earlier run blocks a new one and may hold a stale
# connection to the control plane, so stop it first.
& hermes -p endpointagents gateway stop 2>&1 | Out-Null
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
