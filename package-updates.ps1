[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory -ErrorAction Stop).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.File]::GetAttributes($source) -band [IO.FileAttributes]::ReparsePoint) { throw 'The release payload root cannot be a reparse point.' }
if ([IO.Directory]::EnumerateFileSystemEntries($source).Count -eq 0) { throw 'The release payload directory is empty.' }
$releasePath = Join-Path $source 'release.json'
if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) { throw 'The release payload metadata is missing.' }
$release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
if ($release.application -ne 'ClipboardManager' -or $release.version -ne $Version -or
    $release.platform -ne 'win-x64' -or $release.packageKind -ne 'winui3') {
    throw 'Update payload identity or package kind does not match the WinUI 3 release.'
}
foreach ($required in @('ClipboardManager.App.exe', 'ClipboardManager.UpdateWorker.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) { throw "Missing WinUI update file: $required" }
}
if (Test-Path -LiteralPath (Join-Path $source 'ClipboardManager.exe')) {
    throw 'The legacy WPF launcher must not be included in the WinUI release payload.'
}
$installedManifestPath = Join-Path $source 'update-manifest.json'
if (Test-Path -LiteralPath $installedManifestPath) { throw 'Refusing to replace an existing installed update manifest.' }
$manifestName = "ClipboardManager-$Version-win-x64-update.json"
$manifestOutput = Join-Path $output $manifestName
if (Test-Path -LiteralPath $manifestOutput) { throw "Refusing to overwrite an existing update manifest asset: $manifestOutput" }
[IO.Directory]::CreateDirectory($output) | Out-Null

$entries = @(Get-ChildItem -LiteralPath $source -Force -Recurse)
$linked = @($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
if ($linked.Count -gt 0) { throw "Release payload contains a linked path: $($linked[0].FullName)" }
$files = @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
if ($files.Count -eq 0 -or $files.Count -gt 10000) { throw 'The release payload has an invalid file count.' }
$manifestFiles = [Collections.Generic.List[object]]::new()
$blobs = @{}
[long]$expandedTotal = 0

foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
    if ($relative.Length -gt 240 -or $relative.StartsWith('/') -or $relative.Contains('\') -or
        $relative.Contains(':') -or $relative -match '[<>"|?*\x00-\x1F]') { throw "Non-canonical update path: $relative" }
    $parts = $relative.Split('/')
    foreach ($part in $parts) {
        if ([string]::IsNullOrEmpty($part) -or $part -in @('.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ')) {
            throw "Non-canonical update path: $relative"
        }
        $deviceName = ($part.Split('.')[0]).TrimEnd(' ', '.')
        if ($deviceName -match '^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$') { throw "Reserved update path: $relative" }
    }
    if ($relative -match '(^|/)(\.clipboard-update|\.blobs|\.migration-backups|images|history\.db|history\.json|theme\.json|app\.log[^/]*|settings\.json|unins[^/]*)(/|$)') {
        throw "Protected update file: $relative"
    }
    if ($file.Length -gt 512MB) { throw "Update file exceeds the 512 MiB limit: $relative" }
    if ($file.Length -gt (2GB - $expandedTotal)) { throw 'The update payload exceeds the 2 GiB expanded size limit.' }
    $expandedTotal += $file.Length

    $inputStream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream)).ToLowerInvariant() }
    finally { $inputStream.Dispose() }
    $blobPath = Join-Path $output "update-$hash.gz"
    if (-not $blobs.ContainsKey($hash)) {
        if (Test-Path -LiteralPath $blobPath) {
            $compressed = [IO.File]::OpenRead($blobPath)
            try {
                $gzip = [IO.Compression.GZipStream]::new($compressed, [IO.Compression.CompressionMode]::Decompress)
                try {
                    $digest = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                    [long]$length = 0
                    $buffer = [byte[]]::new(131072)
                    while (($read = $gzip.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        if ($read -gt (2GB - $length)) { throw 'Existing content-addressed blob exceeds the expanded size limit.' }
                        $length += $read
                        $digest.AppendData($buffer, 0, $read)
                    }
                    $existingHash = [Convert]::ToHexString($digest.GetHashAndReset()).ToLowerInvariant()
                    $digest.Dispose()
                }
                finally { $gzip.Dispose() }
            }
            finally { $compressed.Dispose() }
            if ($existingHash -ne $hash -or $length -ne $file.Length) { throw "An existing content-addressed blob is corrupt: $blobPath" }
            $compressedSize = (Get-Item -LiteralPath $blobPath).Length
        }
        else {
            $temporary = Join-Path $output ('.blob-' + [guid]::NewGuid().ToString('N') + '.tmp')
            try {
                $inputStream = [IO.File]::OpenRead($file.FullName)
                try {
                    $outputStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try {
                        $gzip = [IO.Compression.GZipStream]::new($outputStream, [IO.Compression.CompressionLevel]::SmallestSize, $true)
                        try { $inputStream.CopyTo($gzip) }
                        finally { $gzip.Dispose() }
                    }
                    finally { $outputStream.Dispose() }
                }
                finally { $inputStream.Dispose() }
                [IO.File]::Move($temporary, $blobPath, $false)
            }
            finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
            $compressedSize = (Get-Item -LiteralPath $blobPath).Length
        }
        if ($compressedSize -le 0 -or $compressedSize -gt 512MB) { throw "Compressed update file has an invalid size: $relative" }
        $blobs[$hash] = $compressedSize
    }
    else { $compressedSize = $blobs[$hash] }
    $manifestFiles.Add([ordered]@{ path = $relative; sha256 = $hash; size = $file.Length; compressedSize = $compressedSize })
}

[long]$compressedTotal = 0
foreach ($compressedSize in $blobs.Values) {
    if ($compressedSize -gt (2GB - $compressedTotal)) { throw 'The update payload exceeds the 2 GiB compressed size limit.' }
    $compressedTotal += $compressedSize
}

$manifest = [ordered]@{
    schema = 1
    application = 'ClipboardManager'
    version = $Version
    platform = 'win-x64'
    packageKind = 'winui3'
    files = $manifestFiles.ToArray()
}
$json = $manifest | ConvertTo-Json -Depth 8
$manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
if ($manifestBytes.Length -gt 4MB) { throw 'The generated update manifest exceeds 4 MiB.' }
$sourceTemporary = Join-Path $source ('.update-manifest-' + [guid]::NewGuid().ToString('N') + '.tmp')
$outputTemporary = Join-Path $output ('.update-manifest-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllBytes($sourceTemporary, $manifestBytes)
    [IO.File]::WriteAllBytes($outputTemporary, $manifestBytes)
    [IO.File]::Move($sourceTemporary, $installedManifestPath, $false)
    [IO.File]::Move($outputTemporary, $manifestOutput, $false)
}
finally {
    foreach ($temporary in @($sourceTemporary, $outputTemporary)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}
Write-Host "WinUI update manifest: $($manifestFiles.Count) files, $($blobs.Count) unique blobs, packageKind=winui3."
