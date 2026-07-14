<#
.SYNOPSIS
Builds and packages a WoadRaiders client release, and optionally publishes it
to GitHub Releases together with the latest.json manifest that every client
polls at startup (see WoadRaiders.Shared/UpdateManifest.cs).

.DESCRIPTION
  .\tools\release.ps1                 export both platforms, hash, write build/latest.json (dry run)
  .\tools\release.ps1 -Publish        ...then create the GitHub release via gh
  .\tools\release.ps1 -Tag v13.1      override the tag (default: v<N> from the ConnectionKey;
                                      use this for a re-release within one protocol version)
  .\tools\release.ps1 -SkipExport     reuse the artifacts already in build/ (dev)

Needs: godot-mono on PATH with the version-matched export templates installed,
and (for -Publish) an authenticated gh CLI. Windows PowerShell 5.1 compatible.
#>
param(
    [string]$Tag,
    [switch]$Publish,
    [switch]$SkipExport
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$buildDir = Join-Path $repoRoot 'build'
$repoUrl = 'https://github.com/paulcalbrown/WoadRaiders'

# The protocol version IS the release identity: read it straight from the source
# of truth so the manifest can never disagree with the build being shipped.
$netConfig = Get-Content (Join-Path $repoRoot 'WoadRaiders.Shared\NetConfig.cs') -Raw
if ($netConfig -notmatch 'ConnectionKey\s*=\s*"WoadRaiders\.v(\d+)"') {
    throw 'Could not find ConnectionKey = "WoadRaiders.vN" in WoadRaiders.Shared\NetConfig.cs'
}
$version = [int]$Matches[1]
$key = "WoadRaiders.v$version"
if (-not $Tag) { $Tag = "v$version" }
Write-Host "Releasing $key as tag $Tag"

if (-not $SkipExport) {
    # Godot will not create the output directory; export errors without it.
    New-Item -ItemType Directory -Force $buildDir | Out-Null
    $clientDir = Join-Path $repoRoot 'WoadRaiders.Client'

    foreach ($preset in @('Windows Desktop', 'macOS')) {
        Write-Host "Exporting `"$preset`" (the headless exporter lingers after DONE; be patient) ..."
        & godot-mono --headless --path $clientDir --export-release $preset
        if ($LASTEXITCODE -ne 0) { throw "Export `"$preset`" failed with exit code $LASTEXITCODE" }
    }
}

$exe = Join-Path $buildDir 'WoadRaiders.exe'
$zip = Join-Path $buildDir 'WoadRaiders-macOS.zip'
foreach ($artifact in @($exe, $zip)) {
    if (-not (Test-Path $artifact) -or (Get-Item $artifact).Length -lt 10MB) {
        throw "Artifact missing or suspiciously small: $artifact"
    }
}

# The manifest the client fetches from releases/latest/download/latest.json.
# The sha256 digests are unread today; a future self-updater verifies with them.
$manifest = [ordered]@{
    key = $key
    page = "$repoUrl/releases/tag/$Tag"
    published = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    downloads = [ordered]@{
        windows = [ordered]@{
            url = "$repoUrl/releases/download/$Tag/WoadRaiders.exe"
            sha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        macos = [ordered]@{
            url = "$repoUrl/releases/download/$Tag/WoadRaiders-macOS.zip"
            sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}
$manifestPath = Join-Path $buildDir 'latest.json'
$json = $manifest | ConvertTo-Json -Depth 4
# WriteAllText, not Out-File: PowerShell 5.1's default encoding is UTF-16.
[System.IO.File]::WriteAllText($manifestPath, $json)
Write-Host "Wrote $manifestPath"
Write-Host $json

if ($Publish) {
    $notes = "Protocol $key. Clients from v13 on learn about this release automatically " +
        "via ``releases/latest/download/latest.json`` and point outdated players here."
    & gh release create $Tag $exe $zip $manifestPath `
        --repo paulcalbrown/WoadRaiders --title "WoadRaiders $Tag" --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE" }
    Write-Host "Published $repoUrl/releases/tag/$Tag"
    Write-Host 'Sanity-check the live manifest with: dotnet run tools/UpdateProbe.cs'
} else {
    Write-Host "Dry run - nothing published. Publish with: .\tools\release.ps1 -SkipExport -Publish"
}
