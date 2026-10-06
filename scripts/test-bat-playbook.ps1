<#
.SYNOPSIS
Validates a BAT package (or a loose BAT and playbook) and optionally runs it through
the real endpoint runner.

.DESCRIPTION
-Package <folder> checks a FileRepository\packages\<id> folder with the control
plane's own rules and prints the exact fields to fix. -Fix writes the current BAT,
supporting-file and playbook hashes into package.json; use it only after reviewing
the change. Without -Execute nothing is run. With -Execute the BAT really runs as the
current user, answered exactly as an endpoint would answer it. -Discover additionally
prints the screen that follows the last step, to write the next playbook step.

.EXAMPLE
.\scripts\test-bat-playbook.ps1 -Package .\deploy\control-plane\FileRepository\packages\office-activation-repair -Validate

.EXAMPLE
.\scripts\test-bat-playbook.ps1 -Bat .\deploy\samples\menu-repair-sample.bat `
    -Playbook .\deploy\samples\menu-repair-sample.playbook.json -Execute
#>
[CmdletBinding(DefaultParameterSetName = "Package")]
param(
    [Parameter(Mandatory, ParameterSetName = "Package")] [string]$Package,
    [Parameter(ParameterSetName = "Package")] [switch]$Validate,
    [Parameter(ParameterSetName = "Package")] [switch]$Fix,
    [Parameter(Mandatory, ParameterSetName = "Loose")] [string]$Bat,
    [Parameter(Mandatory, ParameterSetName = "Loose")] [string]$Playbook,
    [switch]$Execute,
    [switch]$Discover,
    # NAME=VALUE pairs set only for the BAT process, e.g. -Environment SAMPLE_FAIL=1
    [string[]]$Environment = @()
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\EndpointAgents.PlaybookLab\EndpointAgents.PlaybookLab.csproj"

$labArgs = if ($PSCmdlet.ParameterSetName -eq "Package") {
    @("--package", (Resolve-Path $Package).Path)
} else {
    @("--bat", (Resolve-Path $Bat).Path, "--playbook", (Resolve-Path $Playbook).Path)
}
if ($Fix) { $labArgs += "--fix" }
if ($Execute) { $labArgs += "--execute" }
if ($Discover) { $labArgs += "--discover" }
# powershell -File passes "A=1,B=2" as one string, so split on commas as well.
foreach ($pair in ($Environment -split ",") | Where-Object { $_ }) {
    $labArgs += @("--env", $pair.Trim())
}

& dotnet run --project $project -- @labArgs
exit $LASTEXITCODE
