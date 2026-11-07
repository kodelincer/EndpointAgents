<#
.SYNOPSIS
Builds a new endpoint agent version and signs it, so every PC can update itself.

.DESCRIPTION
Publishes the endpoint service, the Tray and the updater with the version you give, packs
them into endpoint-agent-<version>.zip, writes agent-update.json (the zip's SHA-256, every
file's SHA-256 and the oldest Windows build) and signs it with this PC's package signing key
(the same key as repair packages: scripts\new-package-signing-key.ps1).

The result goes to <FileRepository>\agent-updates\<version>\. Then open the dashboard's
Agent updates page, try the version on the pilot PCs, and set it for everyone.

PCs check about once an hour, install when nobody is chatting and no repair runs, and go
back to the previous version by themselves if the new one does not connect or keeps crashing.

.EXAMPLE
.\scripts\new-agent-update.ps1 -Version 0.3.0 -ReleaseNotes "Event log checks" -Development -Sign
.\scripts\new-agent-update.ps1 -Version 0.3.0 -Sign      # straight into the server's file repository
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d{1,5}\.\d{1,5}\.\d{1,5}$')]
    [string]$Version,
    [string]$ReleaseNotes = "",
    # Write into the repository's development copy (deploy\control-plane\FileRepository).
    [switch]$Development,
    # Or any file repository root (default: the installed control plane's).
    [string]$RepositoryRoot = "",
    [switch]$Sign,
    [string]$KeyName = "EndpointAgents.PackageSigning",
    # Oldest Windows build the new version supports (17763 = Windows 10 1809).
    [int]$MinWindowsBuild = 17763
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $RepositoryRoot) {
    $RepositoryRoot = if ($Development) { Join-Path $repoRoot "deploy\control-plane\FileRepository" }
        else { "C:\ProgramData\EndpointAgents\ControlPlane\FileRepository" }
}
$outputDirectory = Join-Path $RepositoryRoot "agent-updates\$Version"
$work = Join-Path ([IO.Path]::GetTempPath()) "endpoint-agent-$Version-$([Guid]::NewGuid().ToString('N'))"
$layout = Join-Path $work "layout"

function Publish-Component([string]$Project, [string]$Folder, [string[]]$Extra = @()) {
    Write-Host "Publishing $Folder $Version..."
    & dotnet publish (Join-Path $repoRoot $Project) -c Release -r win-x64 --self-contained false `
        "-p:Version=$Version" -o (Join-Path $layout $Folder) @Extra
    if ($LASTEXITCODE -ne 0) { throw "Publishing $Folder failed with exit code $LASTEXITCODE." }
}

try {
    New-Item -ItemType Directory -Path $layout -Force | Out-Null
    Publish-Component "src\EndpointAgents.Service\EndpointAgents.Service.csproj" "Service"
    $prebuiltUi = Test-Path -LiteralPath (Join-Path $repoRoot "src\EndpointAgents.TrayWeb\ui\dist\index.html")
    $uiArguments = if ($prebuiltUi) { @("-p:SkipTrayUi=true") } else { @() }
    Publish-Component "src\EndpointAgents.TrayWeb\EndpointAgents.TrayWeb.csproj" "Tray" $uiArguments
    Publish-Component "src\EndpointAgents.Updater\EndpointAgents.Updater.csproj" "Updater"

    # Never ship development settings or debug symbols.
    Get-ChildItem -LiteralPath $layout -Recurse -File |
        Where-Object { $_.Name -eq "appsettings.Development.json" -or $_.Extension -eq ".pdb" } |
        Remove-Item -Force

    $files = @(Get-ChildItem -LiteralPath $layout -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($layout.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $bad = $files | Where-Object { $_.path -notmatch '^(Service|Tray|Updater)/([A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+$' }
    if ($bad) { throw "These file names cannot be shipped (letters, digits, . _ - only): $($bad.path -join ', ')" }

    $zipName = "endpoint-agent-$Version.zip"
    $zipPath = Join-Path $work $zipName
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($layout, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = Get-Item -LiteralPath $zipPath

    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    Copy-Item -LiteralPath $zipPath -Destination (Join-Path $outputDirectory $zipName) -Force
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        fileName = $zipName
        sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        sizeBytes = $zip.Length
        minWindowsBuild = $MinWindowsBuild
        architecture = "x64"
        releaseNotes = $ReleaseNotes
        files = $files
        signature = $null
    }
    $manifestPath = Join-Path $outputDirectory "agent-update.json"
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Host "Wrote $manifestPath ($($files.Count) files, $([Math]::Round($zip.Length / 1MB, 1)) MB)."

    if ($Sign) {
        $lab = Join-Path $repoRoot "src\EndpointAgents.PlaybookLab\EndpointAgents.PlaybookLab.csproj"
        & dotnet run --project $lab -- --sign-agent-update $manifestPath --key-name $KeyName
        if ($LASTEXITCODE -ne 0) { throw "Signing failed." }
    }
    else {
        Write-Warning "Not signed yet. PCs refuse unsigned updates; run again with -Sign on the admin PC."
    }
    Write-Host ""
    Write-Host "Next: dashboard > Agent updates > choose $Version for the pilot PCs, then for everyone."
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
