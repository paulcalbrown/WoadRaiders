<#
.SYNOPSIS
Builds and packages a WoadRaiders client release, and optionally publishes it
to GitHub Releases together with the latest.json manifest that every client
polls at startup (see WoadRaiders.Shared/UpdateManifest.cs).

.DESCRIPTION
  .\tools\release.ps1                 export the client (both platforms), publish the server
                                      (win-x64 + linux-x64), build the server container image,
                                      hash, write build/latest.json (dry run)
  .\tools\release.ps1 -Publish        ...then create the GitHub release via gh and push the
                                      image to ghcr.io (push needs a token with write:packages;
                                      a failed push warns instead of sinking the release —
                                      the loadable image archive ships as an asset regardless)
  .\tools\release.ps1 -Tag v13.1      override the tag (default: v<N> from the ConnectionKey;
                                      use this for a re-release within one protocol version)
  .\tools\release.ps1 -SkipExport     reuse the CLIENT artifacts already in build/ (dev).
                                      The server is always re-published — dotnet publish is
                                      seconds, unlike the Godot exports.
  .\tools\release.ps1 -SkipImage      skip the container image (machines with no docker/podman)

Needs: godot-mono on PATH with the version-matched export templates installed,
docker or podman for the image, and (for -Publish) an authenticated gh CLI.
Windows PowerShell 5.1 compatible.
#>
param(
    [string]$Tag,
    [switch]$Publish,
    [switch]$SkipExport,
    [switch]$SkipImage,
    # The Godot editor binary. The Scoop shim name locally; CI passes the path
    # of the freshly downloaded Linux editor. Runs under Windows PowerShell 5.1
    # and pwsh-on-Linux alike — keep every change compatible with both.
    [string]$GodotBin = 'godot-mono'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$buildDir = Join-Path $repoRoot 'build'
$repoUrl = 'https://github.com/paulcalbrown/WoadRaiders'

# The protocol version IS the release identity: read it straight from the source
# of truth so the manifest can never disagree with the build being shipped.
$netConfig = Get-Content (Join-Path $repoRoot 'WoadRaiders.Shared/NetConfig.cs') -Raw
if ($netConfig -notmatch 'ConnectionKey\s*=\s*"WoadRaiders\.v(\d+)"') {
    throw 'Could not find ConnectionKey = "WoadRaiders.vN" in WoadRaiders.Shared/NetConfig.cs'
}
$version = [int]$Matches[1]
$key = "WoadRaiders.v$version"
if (-not $Tag) { $Tag = "v$version" }
Write-Host "Releasing $key as tag $Tag"

if (-not $SkipExport) {
    # Godot will not create the output directory; export errors without it.
    New-Item -ItemType Directory -Force $buildDir | Out-Null
    $clientDir = Join-Path $repoRoot 'WoadRaiders.Client'

    # Build the asset-import cache first: .godot/ is gitignored, so a clean
    # clone (CI in particular) has none and the export would ship broken
    # resources. Costs nothing when the cache is already current.
    Write-Host 'Importing assets ...'
    & $GodotBin --headless --path $clientDir --import
    if ($LASTEXITCODE -ne 0) { throw "Asset import failed with exit code $LASTEXITCODE" }

    foreach ($preset in @('Windows Desktop', 'macOS')) {
        Write-Host "Exporting `"$preset`" (the headless exporter lingers after DONE; be patient) ..."
        & $GodotBin --headless --path $clientDir --export-release $preset
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

# The container image: the same staged linux-x64 publish, deployable anywhere
# docker or podman runs. Alongside the registry ref, a loadable archive ships
# as a release asset so deploying needs no registry access at all:
#   docker load -i WoadRaiders-Server-image.tar.gz
$imageRepo = 'ghcr.io/paulcalbrown/woadraiders-server'
$imageRef = "${imageRepo}:$Tag"
$imageArchive = $null
$engine = $null
if (-not $SkipImage) {
    foreach ($candidate in @('docker', 'podman')) {
        if (Get-Command $candidate -ErrorAction SilentlyContinue) { $engine = $candidate; break }
    }
    if (-not $engine) { throw 'No docker or podman on PATH - install one, or pass -SkipImage.' }

    Write-Host "Building $imageRef with $engine ..."
    & $engine build -f (Join-Path $repoRoot 'tools/server.Dockerfile') `
        -t $imageRef -t "${imageRepo}:latest" (Join-Path $buildDir 'server-linux-x64')
    if ($LASTEXITCODE -ne 0) { throw "$engine build failed with exit code $LASTEXITCODE" }

    $imageArchive = Join-Path $buildDir 'WoadRaiders-Server-image.tar.gz'
    $imageTar = Join-Path $buildDir 'WoadRaiders-Server-image.tar'
    foreach ($stale in @($imageTar, $imageArchive)) {
        if (Test-Path $stale) { Remove-Item $stale -Force }
    }
    if ($engine -eq 'podman') {
        & $engine save --format docker-archive -o $imageTar $imageRef  # docker-compatible, not OCI
    } else {
        & $engine save -o $imageTar $imageRef
    }
    if ($LASTEXITCODE -ne 0) { throw "$engine save failed with exit code $LASTEXITCODE" }

    # gzip via .NET — PowerShell 5.1 has no compression cmdlet for streams.
    Add-Type -AssemblyName System.IO.Compression
    $rawStream = [System.IO.File]::OpenRead($imageTar)
    try {
        $outStream = [System.IO.File]::Create($imageArchive)
        try {
            $gzip = New-Object System.IO.Compression.GZipStream(
                $outStream, [System.IO.Compression.CompressionLevel]::Optimal)
            try { $rawStream.CopyTo($gzip) } finally { $gzip.Dispose() }
        } finally { $outStream.Dispose() }
    } finally { $rawStream.Dispose() }
    Remove-Item $imageTar -Force
}

$artifacts = @($exe, $zip) + @($serverZips.Values)
if ($imageArchive) { $artifacts += $imageArchive }
foreach ($artifact in $artifacts) {
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
if ($imageArchive) {
    $manifest['server']['image'] = [ordered]@{
        ref = $imageRef
        url = "$repoUrl/releases/download/$Tag/WoadRaiders-Server-image.tar.gz"
        sha256 = (Get-FileHash $imageArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$manifestPath = Join-Path $buildDir 'latest.json'
$json = $manifest | ConvertTo-Json -Depth 5
# WriteAllText, not Out-File: PowerShell 5.1's default encoding is UTF-16.
[System.IO.File]::WriteAllText($manifestPath, $json)
Write-Host "Wrote $manifestPath"
Write-Host $json

if ($Publish) {
    $notes = "Protocol $key. Clients from v13 on learn about this release automatically " +
        "via ``releases/latest/download/latest.json`` and point outdated players here.`n`n" +
        "To host a dedicated server, unzip the server build for your platform and run it " +
        "(Linux: ``chmod +x WoadRaiders.Server`` first - zip archives don't carry the execute bit), " +
        "or deploy the container: ``docker run -d -p 9050:9050/udp $imageRef`` " +
        "(also attached as a loadable archive: ``docker load -i WoadRaiders-Server-image.tar.gz``). " +
        "It listens on udp/9050 by default."
    & gh release create $Tag $artifacts $manifestPath `
        --repo paulcalbrown/WoadRaiders --title "WoadRaiders $Tag" --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE" }

    if ($imageArchive) {
        # The registry push needs write:packages; a plain repo-scoped token
        # doesn't have it. The loadable archive already shipped above, so a
        # refused push is a warning, not a sunk release.
        if ($env:GH_TOKEN) {
            $env:GH_TOKEN | & $engine login ghcr.io --username paulcalbrown --password-stdin
        }
        & $engine push $imageRef
        $pushOk = $LASTEXITCODE -eq 0
        if ($pushOk) {
            & $engine push "${imageRepo}:latest"
            $pushOk = $LASTEXITCODE -eq 0
        }
        if ($pushOk) {
            Write-Host "Pushed $imageRef and ${imageRepo}:latest"
        } else {
            Write-Warning ("Image push to ghcr.io failed - it needs a token with the " +
                "write:packages scope. The loadable image archive shipped with the release " +
                "regardless. Push later with: $engine push $imageRef; $engine push ${imageRepo}:latest")
        }
    }
    Write-Host "Published $repoUrl/releases/tag/$Tag"
    Write-Host 'Sanity-check the live manifest with: dotnet run tools/UpdateProbe.cs'
} else {
    Write-Host "Dry run - nothing published. Publish with: .\tools\release.ps1 -SkipExport -Publish"
}
