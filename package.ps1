[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'WinUI release packaging requires Windows and PowerShell 7.' }
$repository = [IO.Path]::GetFullPath($PSScriptRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$name = "ClipboardManager-$Version-win-x64"
$archive = Join-Path $output "$name.zip"
$checksum = Join-Path $output "$name.sha256"
$updateManifestAsset = Join-Path $output "$name-update.json"
foreach ($path in @($archive, $checksum, $updateManifestAsset, (Join-Path $output "$name-setup.exe"), (Join-Path $output "$name-setup.sha256"))) {
    if (Test-Path -LiteralPath $path) { throw "Release output already exists; choose a new version or isolated output directory: $path" }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$staging = Join-Path $output ('.package-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'payload'
$appPublish = Join-Path $staging 'app-publish'
$workerPublish = Join-Path $staging 'worker-publish'
$updateAssets = Join-Path $staging 'update-assets'
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source

function Test-ContentAddressedBlob([string]$Path, [string]$ExpectedHash, [long]$ExpectedSize) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
        try {
            $digest = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            [long]$length = 0
            $buffer = [byte[]]::new(131072)
            while (($read = $gzip.Read($buffer, 0, $buffer.Length)) -gt 0) {
                if ($read -gt (2GB - $length)) { throw 'Existing shared update blob exceeds the expanded size limit.' }
                $length += $read
                $digest.AppendData($buffer, 0, $read)
            }
            $hash = [Convert]::ToHexString($digest.GetHashAndReset()).ToLowerInvariant()
            $digest.Dispose()
            return $hash -eq $ExpectedHash -and $length -eq $ExpectedSize
        }
        finally { $gzip.Dispose() }
    }
    finally { $stream.Dispose() }
}

try {
    $sdk = (& $dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.0\.') { throw ".NET 10 SDK is required; resolved SDK was '$sdk'." }
    [IO.Directory]::CreateDirectory($payload) | Out-Null

    $appProject = Join-Path $repository 'src\ClipboardManager.App\ClipboardManager.App.csproj'
    $workerProject = Join-Path $repository 'src\ClipboardManager.UpdateWorker\ClipboardManager.UpdateWorker.csproj'
    & $dotnet publish $appProject --configuration Release --runtime win-x64 --self-contained true `
        --output $appPublish "-p:Version=$Version" `
        -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "WinUI app publish failed: $LASTEXITCODE" }
    & $dotnet publish $workerProject --configuration Release --runtime win-x64 --self-contained true `
        --output $workerPublish "-p:Version=$Version" -p:PublishTrimmed=false -p:PublishReadyToRun=false `
        -p:DebugType=None -p:DebugSymbols=false --nologo
    if ($LASTEXITCODE -ne 0) { throw "Update worker publish failed: $LASTEXITCODE" }

    $appExecutable = Join-Path $appPublish 'ClipboardManager.App.exe'
    $workerExecutable = Join-Path $workerPublish 'ClipboardManager.UpdateWorker.exe'
    foreach ($required in @($appExecutable, $workerExecutable)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Self-contained release executable is missing: $required" }
    }
    Get-ChildItem -LiteralPath $appPublish -Force | Copy-Item -Destination $payload -Recurse -Force
    Copy-Item -LiteralPath $workerExecutable -Destination (Join-Path $payload 'ClipboardManager.UpdateWorker.exe')

    $releaseMetadata = [ordered]@{
        application = 'ClipboardManager'
        version = $Version
        platform = 'win-x64'
        packageKind = 'winui3'
        dataDirectory = '%LOCALAPPDATA%\ClipboardManager'
        firstUpgrade = 'full-installer'
    }
    [IO.File]::WriteAllText((Join-Path $payload 'release.json'), ($releaseMetadata | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $repository 'docs\USAGE.md') -Destination (Join-Path $payload 'USAGE.md')

    & (Join-Path $repository 'package-updates.ps1') -SourceDirectory $payload -OutputDirectory $updateAssets -Version $Version
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'update-manifest.json') -PathType Leaf)) { throw 'The installed update manifest was not generated.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stagedArchive = Join-Path $staging "$name.zip"
    $stagedChecksum = Join-Path $staging "$name.sha256"
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $stagedArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($stagedChecksum, "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))

    $stagedAssets = @(Get-ChildItem -LiteralPath $updateAssets -File | Select-Object -ExpandProperty FullName)
    $stagedManifestPath = Join-Path $updateAssets "$name-update.json"
    if ($stagedManifestPath -notin $stagedAssets) { throw 'The versioned update manifest asset is missing from staging.' }
    $stagedManifest = Get-Content -LiteralPath $stagedManifestPath -Raw | ConvertFrom-Json
    $pendingMoves = [Collections.Generic.List[object]]::new()
    foreach ($file in $stagedAssets) {
        $target = Join-Path $output ([IO.Path]::GetFileName($file))
        if (-not (Test-Path -LiteralPath $target)) {
            $pendingMoves.Add([pscustomobject]@{ Source = $file; Target = $target })
            continue
        }
        $fileName = [IO.Path]::GetFileName($file)
        if ($fileName -notmatch '^update-([0-9a-f]{64})\.gz$') { throw "Release output collision: $target" }
        $digest = $Matches[1]
        $entry = @($stagedManifest.files | Where-Object sha256 -eq $digest | Select-Object -First 1)
        if ($entry.Count -eq 0 -or -not (Test-ContentAddressedBlob $target $digest $entry[0].size)) {
            throw "Existing shared content-addressed blob is corrupt: $target"
        }
    }
    foreach ($move in $pendingMoves) { [IO.File]::Move($move.Source, $move.Target, $false) }
    [IO.File]::Move($stagedArchive, $archive, $false)
    [IO.File]::Move($stagedChecksum, $checksum, $false)

    Write-Host "WinUI package: $archive"
    Write-Host "SHA256: $hash"
    Write-Host 'Package kind: winui3; app and worker are self-contained win-x64; first WPF-to-WinUI upgrade requires the full installer.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    $prefix = $output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^\.package-[0-9a-f]{32}$') {
        throw "Refusing to clean an unexpected package staging path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
