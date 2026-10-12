[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    # Run the previous WinUI Tray instead of the WebView2 Tray.
    [switch]$LegacyTray
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$serviceProject = Join-Path $repoRoot "src\EndpointAgents.Service\EndpointAgents.Service.csproj"
$trayName = if ($LegacyTray) { "EndpointAgents.Tray" } else { "EndpointAgents.TrayWeb" }
$trayProject = Join-Path $repoRoot "src\$trayName\$trayName.csproj"
$serviceExe = Join-Path $repoRoot `
    "src\EndpointAgents.Service\bin\$Configuration\net10.0\EndpointAgents.Service.exe"
$trayExe = Join-Path $repoRoot `
    "src\$trayName\bin\$Configuration\net10.0-windows10.0.17763.0\win-x64\$trayName.exe"
$serviceProcess = $null
$trayProcess = $null

function Assert-NoEndpointProcesses {
    $installedService = Get-Service -Name "EndpointAgents.Service" -ErrorAction SilentlyContinue
    if ($null -ne $installedService -and $installedService.Status -ne "Stopped") {
        throw "The installed EndpointAgents.Service is $($installedService.Status). Stop it before starting the development host."
    }

    $existingService = Get-Process -Name "EndpointAgents.Service" -ErrorAction SilentlyContinue
    if ($null -ne $existingService) {
        throw "An EndpointAgents.Service process is already running. Stop it before starting the development host."
    }

    $currentSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $existingTray = Get-Process -Name "EndpointAgents.Tray", "EndpointAgents.TrayWeb" -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $currentSessionId
    if ($null -ne $existingTray) {
        throw "An EndpointAgents Tray is already running in this Windows session. Exit it before starting the development host."
    }
}

function Invoke-DotnetBuild([string]$Project) {
    & dotnet build $Project --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $Project with exit code $LASTEXITCODE."
    }
}

function Stop-StartedProcess([Diagnostics.Process]$Process, [string]$Name) {
    if ($null -eq $Process -or $Process.HasExited) { return }

    Write-Host "Stopping $Name..."
    try {
        if (-not $Process.WaitForExit(2000)) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            $Process.WaitForExit(5000)
        }
    }
    catch { }
}

Assert-NoEndpointProcesses

Write-Host "Building Endpoint Agent service..."
Invoke-DotnetBuild $serviceProject
Write-Host "Building Endpoint Agent tray..."
Invoke-DotnetBuild $trayProject

if (-not (Test-Path -LiteralPath $serviceExe)) {
    throw "The service executable was not produced at $serviceExe."
}
if (-not (Test-Path -LiteralPath $trayExe)) {
    throw "The tray executable was not produced at $trayExe."
}

$previousEnvironment = $env:DOTNET_ENVIRONMENT
try {
    try {
        $env:DOTNET_ENVIRONMENT = "Development"
        Write-Host "Starting Endpoint Agent service and tray..."
        $serviceProcess = Start-Process -FilePath $serviceExe `
            -WorkingDirectory (Split-Path -Parent $serviceExe) -NoNewWindow -PassThru
        $trayProcess = Start-Process -FilePath $trayExe `
            -WorkingDirectory (Split-Path -Parent $trayExe) -PassThru
    }
    finally {
        $env:DOTNET_ENVIRONMENT = $previousEnvironment
    }

    Write-Host "Endpoint Agent is running. Press Ctrl+C to stop both processes."
    while (-not $serviceProcess.HasExited -and -not $trayProcess.HasExited) {
        Start-Sleep -Milliseconds 250
    }

    if ($serviceProcess.HasExited) {
        Write-Host "EndpointAgents.Service exited with code $($serviceProcess.ExitCode)."
    }
    if ($trayProcess.HasExited) {
        Write-Host "$trayName exited with code $($trayProcess.ExitCode)."
    }
}
finally {
    Stop-StartedProcess $trayProcess $trayName
    Stop-StartedProcess $serviceProcess "EndpointAgents.Service"
}
