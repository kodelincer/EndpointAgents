[CmdletBinding()]
param(
    [string]$InstallRoot = "$env:ProgramFiles\EndpointAgents\Endpoint",
    [Parameter(Mandatory)]
    [uri]$ControlPlaneHubUrl,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$serviceName = "EndpointAgents.Service"
$runKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
$repoRoot = Split-Path -Parent $PSScriptRoot
$serviceOutput = Join-Path $InstallRoot "Service"
$trayOutput = Join-Path $InstallRoot "Tray"

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

function Ensure-EventSource([string]$Source) {
    if (-not [Diagnostics.EventLog]::SourceExists($Source)) {
        [Diagnostics.EventLog]::CreateEventSource($Source, "Application")
    }
}

Assert-Administrator
Ensure-EventSource "EndpointAgents.Service"
Ensure-EventSource "EndpointAgents.Tray"
if ($ControlPlaneHubUrl.Scheme -notin @("http", "https") `
    -or -not $ControlPlaneHubUrl.AbsolutePath.EndsWith("/agent-hub", [StringComparison]::OrdinalIgnoreCase)) {
    throw "ControlPlaneHubUrl must be an absolute HTTP(S) URL ending in /agent-hub."
}
Remove-ServiceIfPresent $serviceName

if (Test-Path -LiteralPath $InstallRoot) {
    $resolved = [IO.Path]::GetFullPath($InstallRoot)
    $programFiles = [IO.Path]::GetFullPath($env:ProgramFiles)
    if (-not $resolved.StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) {
        throw "InstallRoot must remain under Program Files."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

dotnet publish (Join-Path $repoRoot "src\EndpointAgents.Service\EndpointAgents.Service.csproj") `
    -c $Configuration -r win-x64 --self-contained false -o $serviceOutput
dotnet publish (Join-Path $repoRoot "src\EndpointAgents.Tray\EndpointAgents.Tray.csproj") `
    -c $Configuration -r win-x64 --self-contained false -o $trayOutput

$serviceSettingsPath = Join-Path $serviceOutput "appsettings.json"
$serviceSettings = Get-Content -LiteralPath $serviceSettingsPath -Raw | ConvertFrom-Json
$serviceSettings.ControlPlane.HubUrl = $ControlPlaneHubUrl.AbsoluteUri
$serviceSettings | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $serviceSettingsPath `
    -Encoding utf8

$serviceExe = Join-Path $serviceOutput "EndpointAgents.Service.exe"
$trayExe = Join-Path $trayOutput "EndpointAgents.Tray.exe"

& sc.exe create $serviceName binPath= "`"$serviceExe`"" start= auto `
    DisplayName= "EndpointAgents Service" | Out-Null
& sc.exe failure $serviceName reset= 86400 `
    actions= restart/5000/restart/15000/restart/30000 | Out-Null

New-Item -Path $runKey -Force | Out-Null
New-ItemProperty -Path $runKey -Name "EndpointAgents.Tray" `
    -PropertyType String -Value "`"$trayExe`"" -Force | Out-Null
New-Item -ItemType Directory -Path "C:\ProgramData\EndpointAgents\Quarantine\Ibsng" `
    -Force | Out-Null

Start-Service -Name $serviceName

Write-Host "Endpoint Agent installed: the background service is running."
Write-Host "The Tray is registered to start for each user at their next interactive logon."
