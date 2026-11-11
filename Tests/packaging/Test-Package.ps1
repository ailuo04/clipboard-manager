[CmdletBinding()]
param([switch]$BuildPackage, [string]$PackageDirectory, [ValidatePattern('^\d+\.\d+\.\d+$')][string]$PackageVersion = '99.0.0')

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('cm-package-contract-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'payload'
$output = Join-Path $root 'updates'
[IO.Directory]::CreateDirectory($source) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    $release = [ordered]@{ application = 'ClipboardManager'; version = '1.2.3'; platform = 'win-x64'; packageKind = 'winui3' }
    [IO.File]::WriteAllText((Join-Path $source 'release.json'), ($release | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $source 'ClipboardManager.App.exe'), 'synthetic winui application')
    [IO.File]::WriteAllText((Join-Path $source 'ClipboardManager.UpdateWorker.exe'), 'synthetic self-contained worker')
    [IO.File]::WriteAllText((Join-Path $source 'ClipboardManager.Core.dll'), 'synthetic app dependency')

    & (Join-Path $PSScriptRoot '..\..\package-updates.ps1') -SourceDirectory $source -OutputDirectory $output -Version '1.2.3'

    $manifestPath = Join-Path $output 'ClipboardManager-1.2.3-win-x64-update.json'
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'The update manifest asset was not produced.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-True ($manifest.packageKind -eq 'winui3') 'The manifest must identify the WinUI 3 package kind.'
    Assert-True ($manifest.platform -eq 'win-x64' -and $manifest.version -eq '1.2.3') 'The manifest identity is incorrect.'
    Assert-True (-not ($manifest.PSObject.Properties.Name -contains 'packaging')) 'The legacy packaging field must not be emitted.'

    $declared = @($manifest.files | ForEach-Object path | Sort-Object)
    $expected = @('ClipboardManager.App.exe', 'ClipboardManager.Core.dll', 'ClipboardManager.UpdateWorker.exe', 'release.json') | Sort-Object
    Assert-True ((Compare-Object $expected $declared).Count -eq 0) 'The manifest must list every packaged program file and no unrelated files.'
    foreach ($entry in $manifest.files) {
        $blob = Join-Path $output "update-$($entry.sha256).gz"
        Assert-True (Test-Path -LiteralPath $blob -PathType Leaf) "Missing content-addressed update blob: $($entry.path)"
        Assert-True ((Get-Item -LiteralPath $blob).Length -eq $entry.compressedSize) "Compressed size mismatch: $($entry.path)"
        $stream = [IO.File]::OpenRead($blob)
        try {
            $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
            try {
                $memory = [IO.MemoryStream]::new()
                try {
                    $gzip.CopyTo($memory)
                    $bytes = $memory.ToArray()
                }
                finally { $memory.Dispose() }
            }
            finally { $gzip.Dispose() }
        }
        finally { $stream.Dispose() }
        $destination = Join-Path $source ($entry.path.Replace('/', [IO.Path]::DirectorySeparatorChar))
        Assert-True ($bytes.Length -eq $entry.size) "Expanded size mismatch: $($entry.path)"
        Assert-True (([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).ToLowerInvariant() -eq $entry.sha256) "Expanded SHA256 mismatch: $($entry.path)"
        Assert-True ([Convert]::ToBase64String($bytes) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($destination))) "Expanded content mismatch: $($entry.path)"
    }

    $packageScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\package.ps1') -Raw
    Assert-True ($packageScript.Contains('ClipboardManager.App.csproj')) 'The package entrypoint must publish the new WinUI app project.'
    Assert-True (-not $packageScript.Contains("'ClipboardManager.csproj'")) 'The legacy WPF project must not be the release build entrypoint.'
    if ($BuildPackage) {
        if (-not [string]::IsNullOrWhiteSpace($PackageDirectory)) { throw 'Choose either -BuildPackage or -PackageDirectory, not both.' }
        $PackageDirectory = Join-Path $root 'real-build'
        & (Join-Path $PSScriptRoot '..\..\package.ps1') -Version $PackageVersion -OutputDirectory $PackageDirectory
    }
    if (-not [string]::IsNullOrWhiteSpace($PackageDirectory)) {
        $artifactRoot = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
        $archive = Join-Path $artifactRoot "ClipboardManager-$PackageVersion-win-x64.zip"
        $sidecar = Join-Path $artifactRoot "ClipboardManager-$PackageVersion-win-x64.sha256"
        Assert-True (Test-Path -LiteralPath $archive -PathType Leaf) 'The portable release ZIP was not produced.'
        Assert-True (Test-Path -LiteralPath $sidecar -PathType Leaf) 'The ZIP SHA256 sidecar was not produced.'
        $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $sidecarParts = (Get-Content -LiteralPath $sidecar -Raw).Trim() -split '\s+', 2
        Assert-True ($sidecarParts.Count -eq 2 -and $sidecarParts[0] -eq $actualHash -and $sidecarParts[1] -eq [IO.Path]::GetFileName($archive)) 'The ZIP SHA256 sidecar does not match its archive.'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $zipEntries = @($zip.Entries | ForEach-Object FullName)
            foreach ($required in @('ClipboardManager.App.exe', 'ClipboardManager.UpdateWorker.exe', 'release.json', 'update-manifest.json')) {
                Assert-True ($required -in $zipEntries) "The real release ZIP is missing $required"
            }
            $metadataReader = [IO.StreamReader]::new($zip.GetEntry('release.json').Open())
            try { $releaseMetadata = $metadataReader.ReadToEnd() | ConvertFrom-Json }
            finally { $metadataReader.Dispose() }
            Assert-True ($releaseMetadata.packageKind -eq 'winui3' -and $releaseMetadata.firstUpgrade -eq 'full-installer') 'Real package metadata has the wrong migration policy.'
            $manifestReader = [IO.StreamReader]::new($zip.GetEntry('update-manifest.json').Open())
            try { $installedManifest = $manifestReader.ReadToEnd() | ConvertFrom-Json }
            finally { $manifestReader.Dispose() }
            Assert-True ($installedManifest.packageKind -eq 'winui3' -and $installedManifest.version -eq $PackageVersion) 'Real update manifest identity mismatch.'
            foreach ($entry in $installedManifest.files) {
                Assert-True ($entry.path -in $zipEntries) "Installed manifest lists a file missing from the ZIP: $($entry.path)"
            }
        }
        finally { $zip.Dispose() }
        $overwriteRejected = $false
        try { & (Join-Path $PSScriptRoot '..\..\package.ps1') -Version $PackageVersion -OutputDirectory $artifactRoot }
        catch { $overwriteRejected = $true }
        Assert-True $overwriteRejected 'Packaging an already-existing version must be refused.'
        Write-Host "Real package artifact passed: $archive; SHA256=$actualHash; ZIP entries=$($zipEntries.Count)."
    }
    Write-Host 'Package contract passed: WinUI manifest identity, complete program-file inventory, compressed payload hashes, and build entrypoint.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^cm-package-contract-[0-9a-f]{32}$') {
        throw "Refusing to clean an unexpected package test directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
