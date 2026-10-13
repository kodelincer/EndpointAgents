<#
.SYNOPSIS
Puts installer or supporting files into a package's files\ folder, pins their hashes and signs.

.DESCRIPTION
Use it when a vendor installer becomes available for a package whose installer is not in
git (adobe-connect-install, foxit-reader-install, foxit-editor-install, photos-install), or
to add files to one that has them (persian-fonts-install).

Each file is copied into <package>\files\. Its name in the package is chosen like this:
-As names it explicitly (one file only); otherwise, when the package already lists exactly
one supporting file with the same extension, the file takes that name (so any
FoxitPDFReader*.msi becomes FoxitPDFReader.msi, which the package's BAT expects); otherwise
the file keeps its own name. Names may use letters, digits, '.', '_' and '-' only.

EXE, MSI, MSIX and APPX files must carry a valid Authenticode signature; the signer is
printed so you can check it is the vendor. package.json then lists every file in files\,
and test-bat-playbook.ps1 -Fix pins the hashes (and -Sign signs with your key).

-Replace removes the files the package already has before copying.

.EXAMPLE
.\scripts\set-package-files.ps1 -Id foxit-reader-install -File C:\Downloads\FoxitPDFReader20252_enu_Setup.msi -Development -Sign

.EXAMPLE
# Photos: the bundle and every dependency package, replacing what was there.
.\scripts\set-package-files.ps1 -Id photos-install -Replace -Development -Sign -File `
    C:\Photos\Microsoft.Windows.Photos_2025.11010.12001.0_neutral_~_8wekyb3d8bbwe.msixbundle, `
    C:\Photos\Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe.appx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9][a-z0-9._-]{0,63}$')] [string]$Id,
    [Parameter(Mandatory)] [string[]]$File,
    [string]$As,
    [switch]$Replace,
    [string]$RepositoryRoot = "$env:ProgramData\EndpointAgents\ControlPlane\FileRepository",
    # Use the repository's development FileRepository (deploy\control-plane\FileRepository).
    [switch]$Development,
    [switch]$Sign
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ($Development) {
    $RepositoryRoot = Join-Path $repoRoot "deploy\control-plane\FileRepository"
}
$package = Join-Path $RepositoryRoot "packages\$Id"
$manifestPath = Join-Path $package "package.json"
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Package not found: $package" }
if ($As -and $File.Count -ne 1) { throw "-As names one file; pass a single -File with it." }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$listed = @($manifest.supportFiles | ForEach-Object { $_.fileName })
$batName = $manifest.file.fileName

$files = Join-Path $package "files"
New-Item -ItemType Directory -Path $files -Force | Out-Null
if ($Replace) { Get-ChildItem -LiteralPath $files -File | Remove-Item -Force }

foreach ($item in $File) {
    $source = (Resolve-Path -LiteralPath $item).Path
    $extension = [IO.Path]::GetExtension($source)
    $sameType = @($listed | Where-Object { [IO.Path]::GetExtension($_) -eq $extension })
    $name = if ($As) { $As }
        elseif ($sameType.Count -eq 1 -and $File.Count -eq 1) { $sameType[0] }
        elseif ($sameType.Count -eq 1 -and $extension -in ".msi", ".msixbundle") { $sameType[0] }
        else { Split-Path -Leaf $source }
    if ($name -notmatch '^[A-Za-z0-9._-]{1,128}$') {
        throw "'$name' is not a valid package file name; rename it or use -As."
    }
    if ($name -eq $batName) { throw "'$name' would shadow the package's own file." }
    if ($extension -in ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle") {
        $signature = Get-AuthenticodeSignature -LiteralPath $source
        if ($signature.Status -ne "Valid") {
            throw "$source is not validly signed ($($signature.Status)); it was not used."
        }
        Write-Host "Signed by:        $($signature.SignerCertificate.Subject.Split(',')[0])  ($name)"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $files $name) -Force
    Write-Host "Added:            $name"
}

# package.json lists exactly the files that are in files\; -Fix pins their hashes.
$entries = @(Get-ChildItem -LiteralPath $files -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ fileName = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
if ($entries.Count -gt 32) { throw "A package can have at most 32 supporting files." }
$manifest.supportFiles = $entries
$json = $manifest | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText($manifestPath, $json + "`n", (New-Object Text.UTF8Encoding $false))

$lab = Join-Path $PSScriptRoot "test-bat-playbook.ps1"
if ($Sign) { & $lab -Package $package -Fix -Sign } else { & $lab -Package $package -Fix }
exit $LASTEXITCODE
