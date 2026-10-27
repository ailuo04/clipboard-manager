[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$release = Get-Content -LiteralPath (Join-Path $source 'release.json') -Raw | ConvertFrom-Json
if ($release.version -ne $Version -or $release.platform -ne 'win-x64') { throw 'Update version or platform does not match the application.' }
foreach ($required in @('ClipboardManager.exe', 'ClipboardManager.dll', 'ClipboardManager.Update.exe', 'ClipboardManager.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) { throw "Missing update file: $required" }
}
if (Test-Path -LiteralPath (Join-Path $source 'update-manifest.json')) { throw 'Refusing to replace an existing update manifest.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$files = [Collections.Generic.List[object]]::new()
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
    if ($relative -match '(^|/)(\.clipboard-update|unins[^/]*|history\.json|images|app\.log[^/]*)(/|$)' -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Protected or linked update file: $relative" }
    if ($file.Length -gt 512MB) { throw "Update file exceeds the maximum size: $relative" }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $blob = Join-Path $output "update-$hash.gz"
    if (-not (Test-Path -LiteralPath $blob)) {
        $inputStream = [IO.File]::OpenRead($file.FullName)
        try {
            $outputStream = [IO.File]::Open($blob, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            try {
                $gzip = [IO.Compression.GZipStream]::new($outputStream, [IO.Compression.CompressionLevel]::SmallestSize, $true)
                try { $inputStream.CopyTo($gzip) }
                finally { $gzip.Dispose() }
            }
            finally { $outputStream.Dispose() }
        }
        finally { $inputStream.Dispose() }
    }
    $files.Add([ordered]@{ path = $relative; sha256 = $hash; size = $file.Length; compressedSize = (Get-Item -LiteralPath $blob).Length })
}
$manifest = [ordered]@{ schema = 1; application = 'ClipboardManager'; version = $Version; platform = 'win-x64'; packaging = $release.packaging; files = $files.ToArray() }
$json = $manifest | ConvertTo-Json -Depth 8
$manifestName = "ClipboardManager-$Version-win-x64-update.json"
[IO.File]::WriteAllText((Join-Path $source 'update-manifest.json'), $json, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $output $manifestName), $json, [Text.UTF8Encoding]::new($false))
Write-Host "Incremental update: $($files.Count) files, $(@(Get-ChildItem -LiteralPath $output -Filter '*.gz').Count) unique blobs."
