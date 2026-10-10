[CmdletBinding()]
param(
    # Dedicated Hermes profile used only for EndpointAgents helpdesk runs.
    [string]$ProfileName = "endpointagents",
    [string]$HermesHome = (Join-Path $env:LOCALAPPDATA "hermes\profiles\endpointagents"),
    # Reviewed skill folders (deploy\hermes\skills) promoted into the profile.
    [string[]]$Skills = @("office-support", "windows-support"),
    # First-time setup: write config.yaml and .env (API-server and MCP keys are taken from
    # src\ControlPlane.Api\appsettings.Development.json, the provider key from -SourceHermesHome).
    [switch]$Provision,
    [string]$SourceHermesHome = (Join-Path $env:LOCALAPPDATA "hermes"),
    # Only report differences; exit 1 when the profile is out of date.
    [switch]$Check
)

# Copies the reviewed Hermes assets (deploy\hermes) into the Hermes profile that answers
# control-plane chat runs. Run it after EVERY change to deploy\hermes\SOUL.md or to a
# promoted skill, then restart the profile gateway:  hermes -p endpointagents gateway run

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot "deploy\hermes"

function Get-TreeHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return "" }
    $items = Get-ChildItem -LiteralPath $Path -Recurse -File | Sort-Object FullName
    ($items | ForEach-Object {
        $relative = $_.FullName.Substring($Path.Length)
        "$relative=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }) -join "|"
}

if (-not (Test-Path -LiteralPath $HermesHome)) {
    throw "Hermes profile folder not found: $HermesHome. Create it with: hermes profile create $ProfileName --no-skills --no-alias"
}

$stale = @()
foreach ($skill in $Skills) {
    $from = Join-Path $source "skills\$skill"
    $to = Join-Path $HermesHome "skills\$skill"
    if (-not (Test-Path -LiteralPath $from)) { throw "Skill not found in the repository: $from" }
    if ((Get-TreeHash $from) -ne (Get-TreeHash $to)) { $stale += "skill $skill" }
}
$soulFrom = Join-Path $source "SOUL.md"
$soulTo = Join-Path $HermesHome "SOUL.md"
if ((Get-FileHash $soulFrom).Hash -ne (Get-FileHash $soulTo -ErrorAction SilentlyContinue).Hash) {
    $stale += "SOUL.md"
}

if ($Check) {
    if ($stale.Count -eq 0) { Write-Host "Hermes profile '$ProfileName' is in sync."; exit 0 }
    Write-Host "Hermes profile '$ProfileName' is OUT OF DATE: $($stale -join ', ')."
    Write-Host "Run scripts\sync-hermes-profile.ps1 and restart the profile gateway."
    exit 1
}

if ($Provision) {
    $settings = Get-Content (Join-Path $repoRoot "src\ControlPlane.Api\appsettings.Development.json") -Raw |
        ConvertFrom-Json
    $sourceConfig = Join-Path $SourceHermesHome "config.yaml"
    $sourceLines = Get-Content -LiteralPath $sourceConfig
    $model = ($sourceLines | Where-Object { $_ -match '^\s{2}default:\s*"?([^"#\s]+)' } |
        Select-Object -First 1) -replace '^\s{2}default:\s*"?([^"#\s]+).*$', '$1'
    $provider = ($sourceLines | Where-Object { $_ -match '^\s{2}provider:\s*"?([^"#\s]+)' } |
        Select-Object -First 1) -replace '^\s{2}provider:\s*"?([^"#\s]+).*$', '$1'
    $start = [array]::IndexOf($sourceLines, "providers:")
    if ($start -ge 0) {
        $end = $start + 1
        while ($end -lt $sourceLines.Count -and ($sourceLines[$end] -match '^\s' -or $sourceLines[$end] -eq '')) { $end++ }
        $providersBlock = $sourceLines[$start..($end - 1)] -join "`r`n"
    } else { $providersBlock = "" }

    $config = (Get-Content (Join-Path $source "config.yaml.example") -Raw).TrimEnd() +
        "`r`n`r`n# Provider copied from the default Hermes profile.`r`n$providersBlock`r`n"
    # This PC runs one dedicated gateway for the profile (the default profile is personal), so mark it standalone.
    $config = $config -replace "(?m)^gateway:\r?\n", "gateway:`r`n  standalone: true`r`n"
    Set-Content -LiteralPath (Join-Path $HermesHome "config.yaml") -Value $config -Encoding UTF8

    $envLines = @(
        "API_SERVER_ENABLED=true",
        "API_SERVER_HOST=127.0.0.1",
        "API_SERVER_PORT=8642",
        "API_SERVER_KEY=$($settings.Hermes.ApiKey)",
        "CONTROLPLANE_MCP_KEY=$($settings.Hermes.McpApiKey)",
        "ENDPOINTAGENTS_MODEL_PROVIDER=$provider",
        "ENDPOINTAGENTS_MODEL_ID=$model"
    )
    # Carry over only the LLM provider key(s) from the default profile.
    $envLines += Get-Content (Join-Path $SourceHermesHome ".env") |
        Where-Object { $_ -match '^HERMES_CUSTOM_.*_API_KEY=' -or $_ -match '^[A-Z_]*(OPENROUTER|ANTHROPIC|OPENAI|GEMINI|GLM)[A-Z_]*API_KEY=' }
    Set-Content -LiteralPath (Join-Path $HermesHome ".env") -Value $envLines -Encoding UTF8
    Write-Host "Provisioned config.yaml and .env (provider '$provider', model '$model')."
}

foreach ($skill in $Skills) {
    $from = Join-Path $source "skills\$skill"
    $to = Join-Path $HermesHome "skills\$skill"
    if (Test-Path -LiteralPath $to) { Remove-Item -LiteralPath $to -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
    Copy-Item -LiteralPath $from -Destination $to -Recurse -Force
    Write-Host "Synced skill: $skill"
}
Copy-Item -LiteralPath $soulFrom -Destination $soulTo -Force
Write-Host "Synced SOUL.md"
Write-Host "Done. Restart the profile gateway so Hermes reloads: hermes -p $ProfileName gateway run"

