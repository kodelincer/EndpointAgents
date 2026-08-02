[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$HermesExecutable,

    [Parameter(Mandatory)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory)]
    [string]$NssmPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Z][A-Z0-9_]*_API_KEY$')]
    [string]$ProviderEnvironmentName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9-]+$')]
    [string]$ModelProvider,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9._:/-]+$')]
    [string]$ModelId,

    [Parameter(Mandatory)]
    [Security.SecureString]$ProviderApiKey,

    [string]$HermesHome = "$env:ProgramData\EndpointAgents\Hermes",
    [string]$ControlPlaneSettingsPath =
        "$env:ProgramFiles\EndpointAgents\ControlPlane\Api\appsettings.json"
)

$ErrorActionPreference = "Stop"
$serviceName = "EndpointAgents.Hermes"
$serviceIdentity = "NT SERVICE\$serviceName"
$repoRoot = Split-Path -Parent $PSScriptRoot
$templateRoot = Join-Path $repoRoot "deploy\hermes"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session."
    }
}

function New-RandomSecret {
    $bytes = [byte[]]::new(32)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    return [Convert]::ToBase64String($bytes)
}

function ConvertTo-PlainText([Security.SecureString]$Value) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

Assert-Administrator

$hermesExe = [IO.Path]::GetFullPath($HermesExecutable)
$nssmExe = [IO.Path]::GetFullPath($NssmPath)
$home = [IO.Path]::GetFullPath($HermesHome)
$settingsPath = [IO.Path]::GetFullPath($ControlPlaneSettingsPath)
if (-not (Test-Path -LiteralPath $hermesExe -PathType Leaf)) {
    throw "Hermes executable was not found: $hermesExe"
}
if (-not (Test-Path -LiteralPath $nssmExe -PathType Leaf)) {
    throw "NSSM was not found: $nssmExe"
}
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "ControlPlane appsettings file was not found: $settingsPath"
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service '$serviceName' already exists. Uninstall or rename it before reinstalling."
}

$versionOutput = (& $hermesExe --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    $versionOutput.IndexOf($ExpectedVersion, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw "Hermes version mismatch. Expected '$ExpectedVersion'; found '$versionOutput'."
}

New-Item -ItemType Directory -Path $home -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $home "logs") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $templateRoot "config.yaml.example") `
    -Destination (Join-Path $home "config.yaml") -Force

$apiServerKey = New-RandomSecret
$controlPlaneMcpKey = New-RandomSecret
$providerKey = ConvertTo-PlainText $ProviderApiKey
try {
    $environmentLines = @(
        "API_SERVER_ENABLED=true"
        "API_SERVER_HOST=127.0.0.1"
        "API_SERVER_PORT=8642"
        "API_SERVER_KEY=$apiServerKey"
        "CONTROLPLANE_MCP_KEY=$controlPlaneMcpKey"
        "ENDPOINTAGENTS_MODEL_PROVIDER=$ModelProvider"
        "ENDPOINTAGENTS_MODEL_ID=$ModelId"
        "$ProviderEnvironmentName=$providerKey"
    )
    [IO.File]::WriteAllLines((Join-Path $home ".env"), $environmentLines,
        [Text.UTF8Encoding]::new($false))
}
finally {
    $providerKey = $null
}

$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$settings.Hermes.Enabled = $true
$settings.Hermes.BaseUrl = "http://127.0.0.1:8642"
$settings.Hermes.ApiKey = $apiServerKey
$settings.Hermes.McpApiKey = $controlPlaneMcpKey
$settings.Hermes.Model = ""
$settings | ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath $settingsPath -Encoding utf8
& icacls.exe $settingsPath /inheritance:r `
    /grant:r "SYSTEM:F" "BUILTIN\Administrators:F" | Out-Null

& $nssmExe install $serviceName $hermesExe gateway | Out-Null
if ($LASTEXITCODE -ne 0) { throw "NSSM could not create $serviceName." }
& $nssmExe set $serviceName DisplayName "EndpointAgents Hermes Agent" | Out-Null
& $nssmExe set $serviceName Description `
    "Loopback-only Hermes runtime for isolated EndpointAgents support." | Out-Null
& $nssmExe set $serviceName AppDirectory (Split-Path -Parent $hermesExe) | Out-Null
& $nssmExe set $serviceName AppEnvironmentExtra "HERMES_HOME=$home" | Out-Null
& $nssmExe set $serviceName AppStdout (Join-Path $home "logs\service.out.log") | Out-Null
& $nssmExe set $serviceName AppStderr (Join-Path $home "logs\service.err.log") | Out-Null
& $nssmExe set $serviceName AppRotateFiles 1 | Out-Null
& $nssmExe set $serviceName AppRotateBytes 10485760 | Out-Null
& $nssmExe set $serviceName Start SERVICE_AUTO_START | Out-Null
& $nssmExe set $serviceName AppExit Default Restart | Out-Null

& sc.exe config $serviceName obj= $serviceIdentity password= "" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Windows could not assign the virtual service account $serviceIdentity."
}
& sc.exe failure $serviceName reset= 86400 `
    actions= restart/5000/restart/15000/restart/30000 | Out-Null

& icacls.exe $home /inheritance:r `
    /grant:r "SYSTEM:(OI)(CI)F" "BUILTIN\Administrators:(OI)(CI)F" `
    "${serviceIdentity}:(OI)(CI)M" | Out-Null
& icacls.exe (Join-Path $home ".env") /inheritance:r `
    /grant:r "SYSTEM:F" "BUILTIN\Administrators:F" "${serviceIdentity}:R" | Out-Null
& icacls.exe (Join-Path $home "config.yaml") /inheritance:r `
    /grant:r "SYSTEM:F" "BUILTIN\Administrators:F" "${serviceIdentity}:R" | Out-Null

Start-Service -Name $serviceName
Restart-Service -Name "EndpointAgents.ControlPlane.Api"

$headers = @{ Authorization = "Bearer $apiServerKey" }
$capabilities = $null
for ($attempt = 1; $attempt -le 20; $attempt++) {
    try {
        $capabilities = Invoke-RestMethod `
            -Uri "http://127.0.0.1:8642/v1/capabilities" `
            -Headers $headers -TimeoutSec 5
        break
    }
    catch {
        Start-Sleep -Seconds 1
    }
}
if ($null -eq $capabilities -or
    -not $capabilities.features.run_submission -or
    -not $capabilities.features.run_stop) {
    throw "Hermes started but did not advertise the required run and stop APIs. Check $home\logs."
}

Write-Host "Hermes $ExpectedVersion is running as $serviceIdentity."
Write-Host "API: http://127.0.0.1:8642 (loopback only)"
Write-Host "Import reviewed skills from $templateRoot\skills into the Hermes profile."
Write-Host "ControlPlane was configured and restarted."
