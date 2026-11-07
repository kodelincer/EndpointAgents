<#
.SYNOPSIS
Starts the whole development system on this PC, one window per part.

.DESCRIPTION
Opens, in order: the control plane API (5052), the Hermes gateway for the endpointagents
profile (8642), the dashboard (3000), and the endpoint service + Tray. The endpoint is
enrolled with a one-time code the first time; its credential is kept in
%LOCALAPPDATA%\EndpointAgents\dev-device-credential.bin. Close a window to stop that part.
With -Panes the parts share one Windows Terminal window named "endpointagents", split in four.
Anything left from an earlier run is stopped first, so running this again restarts everything.
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

function Stop-Tree([int]$ProcessId) {
    # Through cmd so a process that has already gone is not an error here.
    cmd /c "taskkill /PID $ProcessId /T /F >nul 2>&1"
}

# Closing a window does not stop everything: "next dev" survives and keeps port 3000, and a
# Hermes gateway blocks a new one and may hold a stale connection to the control plane.
function Stop-OldStack {
    $ports = 5052, 8642, 3000
    $endpointNames = "EndpointAgents.Service", "EndpointAgents.Tray", "EndpointAgents.TrayWeb"
    $all = @(Get-CimInstance Win32_Process)
    $owners = @(Get-NetTCPConnection -State Listen -LocalPort $ports -ErrorAction SilentlyContinue)
    foreach ($owner in $owners) {
        $process = $all | Where-Object ProcessId -eq $owner.OwningProcess
        if ($process.Name -notmatch '^(ControlPlane\.Api|dotnet|node|python|hermes)\.exe$') {
            throw "Port $($owner.LocalPort) is used by $($process.Name), which is not part of this system. Close it first."
        }
    }

    & hermes -p endpointagents gateway stop 2>&1 | Out-Null

    $stop = @{}
    foreach ($owner in $owners) {
        # Stop from the top of its launcher chain (npm -> next -> server) so none of it stays behind.
        $top = $all | Where-Object ProcessId -eq $owner.OwningProcess
        while ($parent = $all | Where-Object { $_.ProcessId -eq $top.ParentProcessId -and
                $_.CommandLine -match 'npm|next|hermes|ControlPlane' -and $_.Name -ne 'powershell.exe' }) {
            $top = $parent
        }
        $stop[[int]$top.ProcessId] = $true
    }
    $all | Where-Object { $_.Name -replace '\.exe$' -in $endpointNames } | ForEach-Object { $stop[[int]$_.ProcessId] = $true }

    # The windows and panes of an earlier run, found by the title Start-Part gives them.
    $terminals = @{}
    foreach ($shell in $all | Where-Object Name -eq 'powershell.exe') {
        $text = "$($shell.CommandLine)"
        if ($text -match 'EncodedCommand (\S+)') { $text = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Matches[1])) }
        if ($text -match "WindowTitle = '(ControlPlane API|Hermes gateway|Dashboard|Endpoint service \+ tray)'") {
            $stop[[int]$shell.ProcessId] = $true
            $terminals[[int]$shell.ParentProcessId] = $true
        }
    }

    foreach ($id in $stop.Keys) { Stop-Tree $id }
    if ($stop.Count) { Write-Host "Stopped what was left from an earlier run." }

    # Their panes close with them, but a window with nothing left in it can stay open.
    Start-Sleep -Seconds 2
    $all = @(Get-CimInstance Win32_Process)
    foreach ($id in $terminals.Keys) {
        $terminal = $all | Where-Object { $_.ProcessId -eq $id -and $_.Name -eq 'WindowsTerminal.exe' }
        if ($terminal -and -not ($all | Where-Object { $_.ParentProcessId -eq $id -and $_.Name -ne 'OpenConsole.exe' })) {
            Stop-Tree $id
        }
    }

    $deadline = (Get-Date).AddSeconds(20)
    while (Get-NetTCPConnection -State Listen -LocalPort $ports -ErrorAction SilentlyContinue) {
        if ((Get-Date) -gt $deadline) { throw "Ports $($ports -join ', ') are still in use after stopping the earlier run." }
        Start-Sleep -Seconds 1
    }
    if (Get-Process -Name $endpointNames -ErrorAction SilentlyContinue) {
        throw "An EndpointAgents service or Tray could not be stopped. An installed service needs stopping as administrator."
    }
}

Stop-OldStack

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
