<#
.SYNOPSIS
Puts Google's Chrome Enterprise MSI into the chrome-install package, pins its hash and signs it.

.DESCRIPTION
The MSI (about 170 MB) is too large for git, so every control plane gets it from here.
The script downloads GoogleChromeStandaloneEnterprise64.msi from Google, or takes one you
already have with -Msi, and refuses it unless Windows verifies its Authenticode signature
as Google LLC. It then copies it to packages\chrome-install\files\, records the new hashes
with test-bat-playbook.ps1 -Fix and, with -Sign, signs the package with your key.

Run it again whenever you want endpoints to install a newer Chrome; endpoints that already
staged the old MSI download the new one on the next run.

.EXAMPLE
.\scripts\update-chrome-package.ps1 -Development -Sign

.EXAMPLE
# A PC without internet access: download the MSI elsewhere and pass it in.
.\scripts\update-chrome-package.ps1 -Msi D:\Installers\GoogleChromeStandaloneEnterprise64.msi -Sign
#>
[CmdletBinding()]
param(
    [string]$Msi,
    [string]$RepositoryRoot = "$env:ProgramData\EndpointAgents\ControlPlane\FileRepository",
    # Use the repository's development FileRepository (deploy\control-plane\FileRepository).
    [switch]$Development,
    [switch]$Sign
)

$ErrorActionPreference = "Stop"
$downloadUrl = "https://dl.google.com/dl/chrome/install/googlechromestandaloneenterprise64.msi"
$msiName = "GoogleChromeStandaloneEnterprise64.msi"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Development) {
    $RepositoryRoot = Join-Path $repoRoot "deploy\control-plane\FileRepository"
}
$package = Join-Path $RepositoryRoot "packages\chrome-install"
if (-not (Test-Path -LiteralPath (Join-Path $package "package.json"))) {
    throw "The chrome-install package is not in $RepositoryRoot. Copy deploy\control-plane\FileRepository\packages\chrome-install there first."
}

$work = Join-Path ([IO.Path]::GetTempPath()) "EndpointAgents-chrome-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $candidate = Join-Path $work $msiName
    if ($Msi) {
        Copy-Item -LiteralPath (Resolve-Path -LiteralPath $Msi).Path -Destination $candidate
    } else {
        Write-Host "Downloading $downloadUrl ..."
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest -Uri $downloadUrl -OutFile $candidate -UseBasicParsing
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $candidate
    if ($signature.Status -ne "Valid" `
        -or $signature.SignerCertificate.Subject -notmatch '^CN=Google LLC,') {
        throw "The MSI is not validly signed by Google LLC ($($signature.Status)); it was not used."
    }
    Write-Host "Signature:        Valid, $($signature.SignerCertificate.Subject.Split(',')[0])"

    $files = Join-Path $package "files"
    New-Item -ItemType Directory -Path $files -Force | Out-Null
    Copy-Item -LiteralPath $candidate -Destination (Join-Path $files $msiName) -Force
    Write-Host "MSI SHA-256:      $((Get-FileHash -LiteralPath (Join-Path $files $msiName) -Algorithm SHA256).Hash)"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$lab = Join-Path $PSScriptRoot "test-bat-playbook.ps1"
if ($Sign) { & $lab -Package $package -Fix -Sign } else { & $lab -Package $package -Fix }
exit $LASTEXITCODE
