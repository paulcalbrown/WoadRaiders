<#
.SYNOPSIS
Builds and packages a WoadRaiders client release, and optionally publishes it
to GitHub Releases together with the latest.json manifest that every client
polls at startup (see WoadRaiders.Shared/UpdateManifest.cs).

.DESCRIPTION
  .\tools\release.ps1                 export the client (both platforms), publish the server
                                      (win-x64 + linux-x64), hash, write build/latest.json (dry run)
  .\tools\release.ps1 -Publish        ...then create the GitHub release via gh
  .\tools\release.ps1 -Tag v13.1      override the tag (default: v<N> from the ConnectionKey;
                                      use this for a re-release within one protocol version)
  .\tools\release.ps1 -SkipExport     reuse the CLIENT artifacts already in build/ (dev).
                                      The server is always re-published — dotnet publish is
                                      seconds, unlike the Godot exports.

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

# The dedicated server: a plain dotnet publish per platform, zipped with the
# maps that land beside the binary (the csproj links them in as Content).
# Entry paths MUST be forward-slash or Linux unzip tools extract files
# literally named "maps\Barrow.json"; on the .NET Framework that PowerShell
# 5.1 hosts, ZipFile only does that with this legacy switch turned off
# (Compress-Archive has the same disease with no cure — hence ZipFile).
Add-Type -AssemblyName System.IO.Compression.FileSystem
[AppContext]::SetSwitch('Switch.System.IO.Compression.ZipFile.UseBackslash', $false)
$serverZips = [ordered]@{}
foreach ($rid in @('win-x64', 'linux-x64')) {
    Write-Host "Publishing the dedicated server for $rid ..."
    $stage = Join-Path $buildDir "server-$rid"
    # A pristine stage: dotnet publish overwrites but never removes strays, and
    # everything in this directory ends up in the shipped zip.
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    & dotnet publish (Join-Path $repoRoot 'WoadRaiders.Server') -c Release -r $rid `
        --self-contained -p:PublishSingleFile=true -p:DebugType=embedded -o $stage --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish for $rid failed with exit code $LASTEXITCODE" }
    $serverZip = Join-Path $buildDir "WoadRaiders-Server-$rid.zip"
    if (Test-Path $serverZip) { Remove-Item $serverZip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $serverZip)
    $serverZips[$rid] = $serverZip
}

foreach ($artifact in @($exe, $zip) + @($serverZips.Values)) {
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
    server = [ordered]@{
        windows = [ordered]@{
            url = "$repoUrl/releases/download/$Tag/WoadRaiders-Server-win-x64.zip"
            sha256 = (Get-FileHash $serverZips['win-x64'] -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        linux = [ordered]@{
            url = "$repoUrl/releases/download/$Tag/WoadRaiders-Server-linux-x64.zip"
            sha256 = (Get-FileHash $serverZips['linux-x64'] -Algorithm SHA256).Hash.ToLowerInvariant()
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
        "via ``releases/latest/download/latest.json`` and point outdated players here.`n`n" +
        "To host a dedicated server, unzip the server build for your platform and run it " +
        "(Linux: ``chmod +x WoadRaiders.Server`` first - zip archives don't carry the execute bit). " +
        "It listens on udp/9050 by default."
    & gh release create $Tag $exe $zip $serverZips['win-x64'] $serverZips['linux-x64'] $manifestPath `
        --repo paulcalbrown/WoadRaiders --title "WoadRaiders $Tag" --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE" }
    Write-Host "Published $repoUrl/releases/tag/$Tag"
    Write-Host 'Sanity-check the live manifest with: dotnet run tools/UpdateProbe.cs'
} else {
    Write-Host "Dry run - nothing published. Publish with: .\tools\release.ps1 -SkipExport -Publish"
}
