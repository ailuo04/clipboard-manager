# Exercises packaging and publication locally; GitHub calls are replaced with an in-memory release.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('clipboard-publication-' + [guid]::NewGuid().ToString('N'))
$previousToken = $env:GH_TOKEN
try {
    $source = Join-Path $testRoot 'source'
    $output = Join-Path $testRoot 'output'
    [IO.Directory]::CreateDirectory($source) | Out-Null
    foreach ($name in @('ClipboardManager.exe', 'ClipboardManager.dll', 'ClipboardManager.Update.exe', 'ClipboardManager.runtimeconfig.json')) {
        [IO.File]::WriteAllText((Join-Path $source $name), "fixture $name")
    }
    [IO.File]::WriteAllText((Join-Path $source 'release.json'), '{"version":"999.0.0","platform":"win-x64","packaging":"sdk-self-contained"}')
    & (Join-Path $repo 'package-updates.ps1') -SourceDirectory $source -OutputDirectory $output -Version '999.0.0'
    $fixed = Join-Path $output 'latest-win-x64.json'
    $versioned = Join-Path $output 'ClipboardManager-999.0.0-win-x64-update.json'
    if (-not (Test-Path -LiteralPath $fixed)) { throw 'Packaging did not emit the fixed manifest.' }
    if ((Get-FileHash -LiteralPath $fixed).Hash -ne (Get-FileHash -LiteralPath $versioned).Hash) { throw 'Fixed and versioned manifests differ.' }
    foreach ($extension in @('zip', 'exe')) {
        $base = if ($extension -eq 'exe') { 'ClipboardManager-999.0.0-win-x64-setup' } else { 'ClipboardManager-999.0.0-win-x64' }
        $path = Join-Path $output "$base.$extension"
        [IO.File]::WriteAllText($path, 'fixture package')
        [IO.File]::WriteAllText((Join-Path $output "$base.sha256"), "$( (Get-FileHash -LiteralPath $path).Hash )  $base.$extension")
    }
    $publicationState = @{ Release = $null; Published = $false }
    function Invoke-RestMethod {
        param($Uri, $Headers, $TimeoutSec)
        if ($publicationState.Release) { return $publicationState.Release }
        return @()
    }
    function gh {
        $global:LASTEXITCODE = 0
        switch ($args[1]) {
            'create' { $publicationState.Release = [pscustomobject]@{ tag_name = 'v999.0.0'; assets = @(); draft = $true; prerelease = $false } }
            'upload' {
                foreach ($path in $args[3..($args.Count - 1)]) {
                    if ($path -eq '--repo') { break }
                    $file = Get-Item -LiteralPath $path
                    $publicationState.Release.assets += [pscustomobject]@{ name = $file.Name; size = $file.Length; state = 'uploaded'; digest = 'sha256:' + (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() }
                }
            }
            'edit' {
                if ($args -contains '--latest') {
                    if (@($publicationState.Release.assets | Where-Object name -eq 'latest-win-x64.json').Count -ne 1) { throw 'Published without the fixed manifest.' }
                    $publicationState.Published = $true
                }
            }
            default { throw "Unexpected gh command: $args" }
        }
    }
    $env:GH_TOKEN = 'offline-fixture-token'
    & (Join-Path $repo 'publish-release.ps1') -Version '999.0.0' -Directory $output -Repository 'fixture/repository'
    if (-not $publicationState.Published) { throw 'Publication did not complete.' }
    $publicationState.Published = $false
    [IO.File]::WriteAllText($fixed, '{}')
    $rejected = $false
    try { & (Join-Path $repo 'publish-release.ps1') -Version '999.0.0' -Directory $output -Repository 'fixture/repository' }
    catch { if ($_.Exception.Message -notmatch 'manifest.*match') { throw }; $rejected = $true }
    if (-not $rejected -or $publicationState.Published) { throw 'Publication accepted a mismatched fixed manifest.' }
    Write-Host 'PASS: fixed manifest packaging, release upload, and mismatched manifest rejection.'
}
finally {
    $env:GH_TOKEN = $previousToken
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ((Split-Path $resolved -Parent) -ne ([IO.Path]::GetTempPath()).TrimEnd('\') -or (Split-Path $resolved -Leaf) -notmatch '^clipboard-publication-[0-9a-f]{32}$') { throw 'Unexpected test cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
