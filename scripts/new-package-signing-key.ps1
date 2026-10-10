<#
.SYNOPSIS
Creates the administrator's package signing key on this PC and prints its public key.

.DESCRIPTION
Every package must be signed before endpoints run it (test-bat-playbook.ps1 -Sign).
This creates a non-exportable ECDSA P-256 key that Windows keeps in your user profile
on this admin PC; it never needs to leave the PC. The script prints the public key to
trust in two places:

  control plane  appsettings.json  FileRepository:TrustedPackageSigningKeys
  endpoints      appsettings.json  PackageSigning:TrustedPublicKeys

Several keys can be trusted at once, so a second administrator, or a replacement key
after a PC is lost, is just another entry. Run again with -Show to print the public key.

.EXAMPLE
.\scripts\new-package-signing-key.ps1
.\scripts\new-package-signing-key.ps1 -Show
#>
[CmdletBinding()]
param(
    [string]$KeyName = "EndpointAgents.PackageSigning",
    [switch]$Show
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\EndpointAgents.PlaybookLab\EndpointAgents.PlaybookLab.csproj"
$command = if ($Show) { "--show-signing-key" } else { "--new-signing-key" }
& dotnet run --project $project -- $command --key-name $KeyName
exit $LASTEXITCODE
