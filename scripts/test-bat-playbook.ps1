<#
.SYNOPSIS
Hashes, lints and validates a BAT and its playbook, and optionally runs it through
the real endpoint runner.

.DESCRIPTION
Without -Execute the BAT is only read. With -Execute it really runs as the current
user, exactly as the endpoint would answer its prompts, so test unknown BATs in a
disposable VM. -Discover additionally prints the screen that follows the last step,
which helps when writing the next playbook step. Prints a manifest entry draft.

.EXAMPLE
.\scripts\test-bat-playbook.ps1 -Bat .\deploy\samples\menu-scenarios-sample.bat `
    -Playbook .\deploy\samples\menu-scenarios-sample.playbook.json -Execute
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Bat,
    [Parameter(Mandatory)] [string]$Playbook,
    [string]$ArtifactId,
    [switch]$Execute,
    [switch]$Discover,
    # NAME=VALUE pairs set only for the BAT process, e.g. -Environment SAMPLE_PAGES=2
    [string[]]$Environment = @()
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\EndpointAgents.PlaybookLab\EndpointAgents.PlaybookLab.csproj"

$labArgs = @("--bat", (Resolve-Path $Bat).Path, "--playbook", (Resolve-Path $Playbook).Path)
if ($ArtifactId) { $labArgs += @("--artifact-id", $ArtifactId) }
if ($Execute) { $labArgs += "--execute" }
if ($Discover) { $labArgs += "--discover" }
# powershell -File passes "A=1,B=2" as one string, so split on commas as well.
foreach ($pair in ($Environment -split ",") | Where-Object { $_ }) {
    $labArgs += @("--env", $pair.Trim())
}

& dotnet run --project $project -- @labArgs
exit $LASTEXITCODE
