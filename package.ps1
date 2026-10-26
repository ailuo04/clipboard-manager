[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.0.1',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [switch]$RuntimeCompiler
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Packaging requires Windows and PowerShell 7.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$name = "ClipboardManager-$Version-win-x64"
$archive = Join-Path $output "$name.zip"
$checksum = Join-Path $output "$name.sha256"
foreach ($path in @($archive, $checksum)) {
    if (Test-Path -LiteralPath $path) { throw "Release file already exists; choose a new version or output directory: $path" }
}
$bin = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'bin'))
$staging = Join-Path $bin ('.package-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging $name
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source

try {
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    $sdks = @(& $dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot query .NET SDKs.' }
    if ($sdks.Count -gt 0 -and -not $RuntimeCompiler) {
        & $dotnet publish (Join-Path $PSScriptRoot 'ClipboardManager.csproj') --configuration Release `
            --runtime win-x64 --self-contained true --output $payload "-p:Version=$Version" `
            -p:PublishTrimmed=false -p:PublishReadyToRun=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
        $mode = 'sdk-self-contained'
        $runtimeVersion = (Get-Content -LiteralPath (Join-Path $payload 'ClipboardManager.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks |
            Where-Object name -eq 'Microsoft.WindowsDesktop.App' | Select-Object -ExpandProperty version
        if (-not $runtimeVersion) { throw 'Published application is missing its self-contained desktop runtime.' }
    }
    else {
        if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
            throw 'The offline packaging path requires x64 PowerShell and .NET.'
        }
        & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $payload -RuntimeCompiler -Version $Version
        $root = Split-Path $dotnet
        $desktop = Get-ChildItem -LiteralPath (Join-Path $root 'shared\Microsoft.WindowsDesktop.App') -Directory |
            Where-Object { $_.Name -match '^8\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
        if (-not $desktop) { throw 'An installed .NET 8 Windows Desktop Runtime is required for offline packaging.' }
        $runtimeVersion = $desktop.Name
        $runtime = Join-Path $payload 'runtime'
        foreach ($relative in @("shared\Microsoft.NETCore.App\$runtimeVersion", "shared\Microsoft.WindowsDesktop.App\$runtimeVersion", "host\fxr\$runtimeVersion")) {
            $source = Join-Path $root $relative
            if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Matching runtime component is missing: $source" }
            $target = Join-Path $runtime $relative
            [IO.Directory]::CreateDirectory((Split-Path $target)) | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Recurse
        }
        foreach ($file in @('dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt')) {
            Copy-Item -LiteralPath (Join-Path $root $file) -Destination $runtime
        }
        & (Join-Path $runtime 'dotnet.exe') --list-runtimes
        if ($LASTEXITCODE -ne 0) { throw 'The bundled .NET host failed verification.' }
        $mode = 'offline-bundled-runtime'
    }

    foreach ($file in @('ClipboardManager.exe', 'ClipboardManager.dll', 'ClipboardManager.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) { throw "Missing application file: $file" }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\USAGE.md') -Destination (Join-Path $payload 'USAGE.md')
    $manifest = [ordered]@{
        application = 'ClipboardManager'
        version = $Version
        platform = 'win-x64'
        packaging = $mode
        desktopRuntime = $runtimeVersion
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        dataDirectory = '%LOCALAPPDATA%\ClipboardManager'
    }
    [IO.File]::WriteAllText((Join-Path $payload 'release.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [IO.Directory]::CreateDirectory($output) | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksum, "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Package: $archive"
    Write-Host "SHA256:  $hash"
    Write-Host "Runtime: $runtimeVersion ($mode)"
}
finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    if ((Split-Path $resolved) -ne $bin -or (Split-Path $resolved -Leaf) -notmatch '^\.package-[0-9a-f]{32}$') {
        throw "Refusing to clean an unexpected staging path: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
