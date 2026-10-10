<#
.SYNOPSIS
Creates a new BAT package folder with a draft package.json. Kept for existing guides;
new-package.ps1 does the work and also handles EXE files and no-input packages.

.EXAMPLE
.\scripts\new-bat-package.ps1 -Bat .\deploy\samples\menu-repair-sample.bat `
    -Id menu-repair-sample -Kind office -Development
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Bat,
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9._-]{0,63}$')] [string]$Id,
    [Parameter(Mandatory)] [ValidateSet("office", "windows", "general")] [string]$Kind,
    [switch]$NoInput,
    [string]$RepositoryRoot = "$env:ProgramData\EndpointAgents\ControlPlane\FileRepository",
    [switch]$Development
)

& (Join-Path $PSScriptRoot "new-package.ps1") -File $Bat -Id $Id -Kind $Kind `
    -NoInput:$NoInput -RepositoryRoot $RepositoryRoot -Development:$Development
exit $LASTEXITCODE
