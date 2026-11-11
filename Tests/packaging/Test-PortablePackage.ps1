[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Portable package verification requires Windows PowerShell 7.' }
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Portable application launch is restricted to GitHub-hosted disposable runners.'
}

$archivePath = (Resolve-Path -LiteralPath $Archive -ErrorAction Stop).Path
$checksumPath = [IO.Path]::ChangeExtension($archivePath, '.sha256')
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw 'The portable package SHA256 sidecar is missing.' }
$sidecar = (Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+', 2
if ($sidecar.Count -ne 2 -or $sidecar[0] -notmatch '^[a-fA-F0-9]{64}$' -or
    $sidecar[1] -ne [IO.Path]::GetFileName($archivePath) -or
    (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $sidecar[0]) {
    throw 'The portable package does not match its SHA256 sidecar.'
}

$localAppData = [IO.Path]::GetFullPath([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))
$dataRoot = [IO.Path]::GetFullPath((Join-Path $localAppData 'ClipboardManager'))
if (-not $dataRoot.StartsWith($localAppData.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) { throw 'Synthetic data root escaped LocalAppData.' }
if (Test-Path -LiteralPath $dataRoot) { throw 'Refusing to run portable verification when ClipboardManager user data already exists on this runner.' }

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('cm-portable-verify-' + [guid]::NewGuid().ToString('N'))
$portableRoot = Join-Path $temporary 'portable-app'
$appProcess = $null
$dataRootCreated = $false
$legacyHistoryHash = $null
$legacyThemeHash = $null
$legacyImageHash = $null
[IO.Directory]::CreateDirectory($portableRoot) | Out-Null

function Get-TreeHash([string]$Root) {
    $fullRoot = [IO.Path]::GetFullPath($Root)
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) { throw "Expected directory is missing: $fullRoot" }
    return (Get-ChildItem -LiteralPath $fullRoot -Recurse -File | Where-Object { $_.Name -ne 'app.log' } | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($fullRoot, $_.FullName)
        "$relative`0$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
    }) -join "`n"
}

try {
    [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $portableRoot)
    $release = Get-Content -LiteralPath (Join-Path $portableRoot 'release.json') -Raw | ConvertFrom-Json
    $manifest = Get-Content -LiteralPath (Join-Path $portableRoot 'update-manifest.json') -Raw | ConvertFrom-Json
    if ($release.application -ne 'ClipboardManager' -or $release.version -ne $Version -or
        $release.platform -ne 'win-x64' -or $release.packageKind -ne 'winui3' -or $release.firstUpgrade -ne 'full-installer' -or
        $manifest.version -ne $Version -or $manifest.platform -ne 'win-x64' -or $manifest.packageKind -ne 'winui3') {
        throw 'The portable package metadata does not match the WinUI Release.'
    }
    foreach ($entry in $manifest.files) {
        $relative = $entry.path.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $filePath = [IO.Path]::GetFullPath((Join-Path $portableRoot $relative))
        $prefix = $portableRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $filePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $filePath -PathType Leaf) -or
            (Get-Item -LiteralPath $filePath).Length -ne $entry.size -or
            (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Portable manifest verification failed for $($entry.path)."
        }
    }

    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    $dataRootCreated = $true
    $legacyFixtures = Join-Path $PSScriptRoot '..\..\Tests\ClipboardManager.Storage.Tests\Fixtures'
    $legacyHistory = Get-Content -LiteralPath (Join-Path $legacyFixtures 'legacy-history.json') -Raw | ConvertFrom-Json
    $legacyHistory.settings.autoCheckUpdates = $false
    $legacyHistory.settings.minimizeToTray = $false
    [IO.File]::WriteAllText((Join-Path $dataRoot 'history.json'),
        ($legacyHistory | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
    [IO.Directory]::CreateDirectory((Join-Path $dataRoot 'images')) | Out-Null
    Copy-Item -LiteralPath (Join-Path $legacyFixtures 'legacy-image.png') -Destination (Join-Path $dataRoot 'images\00000000000000000000000000000012.png')
    [IO.File]::WriteAllText((Join-Path $dataRoot 'theme.json'), '{"theme":"Dark"}', [Text.UTF8Encoding]::new($false))
    $legacyHistoryHash = (Get-FileHash -LiteralPath (Join-Path $dataRoot 'history.json') -Algorithm SHA256).Hash
    $legacyThemeHash = (Get-FileHash -LiteralPath (Join-Path $dataRoot 'theme.json') -Algorithm SHA256).Hash
    $legacyImageHash = (Get-FileHash -LiteralPath (Join-Path $dataRoot 'images\00000000000000000000000000000012.png') -Algorithm SHA256).Hash

    $appPath = Join-Path $portableRoot 'ClipboardManager.App.exe'
    $start = [Diagnostics.ProcessStartInfo]::new($appPath)
    $start.WorkingDirectory = $portableRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $appProcess = [Diagnostics.Process]::Start($start)
    $windowOpened = $false
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw "Portable WinUI app exited during synthetic-data startup: $($appProcess.ExitCode)." }
        if ($appProcess.MainWindowHandle -ne [IntPtr]::Zero) { $windowOpened = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $windowOpened) { throw 'Portable WinUI app did not show its main window on the disposable runner.' }
    if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'history.db') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $dataRoot '.migration-complete.json') -PathType Leaf)) {
        throw 'Portable startup did not create the SQLite database and migration marker from synthetic legacy data.'
    }
    if (-not $appProcess.CloseMainWindow() -or -not $appProcess.WaitForExit(15000)) {
        throw 'Portable WinUI app did not close cleanly after the launch check.'
    }
    if ($appProcess.ExitCode -ne 0) { throw "Portable WinUI app exited with code $($appProcess.ExitCode)." }
    $appProcess = $null


    if ((Get-FileHash -LiteralPath (Join-Path $dataRoot 'history.json') -Algorithm SHA256).Hash -ne $legacyHistoryHash -or
        (Get-FileHash -LiteralPath (Join-Path $dataRoot 'theme.json') -Algorithm SHA256).Hash -ne $legacyThemeHash -or
        (Get-FileHash -LiteralPath (Join-Path $dataRoot 'images\00000000000000000000000000000012.png') -Algorithm SHA256).Hash -ne $legacyImageHash -or
        -not (Get-ChildItem -LiteralPath (Join-Path $dataRoot '.migration-backups') -Directory -ErrorAction SilentlyContinue)) {
        throw 'Portable migration did not preserve the original synthetic JSON/theme/image inputs and backup.'
    }

    $dataHashBeforeUpdate = Get-TreeHash $dataRoot
    $currentVersion = [version]$manifest.version
    if ($currentVersion.Build -ge [int]::MaxValue) { throw 'Cannot construct a higher synthetic update version.' }
    $nextVersion = '{0}.{1}.{2}' -f $currentVersion.Major, $currentVersion.Minor, ($currentVersion.Build + 1)
    $updateParent = Join-Path $portableRoot '.clipboard-update'
    $workspace = Join-Path $updateParent ([guid]::NewGuid().ToString('N'))
    $stagedRoot = Join-Path $workspace 'new'
    [IO.Directory]::CreateDirectory($stagedRoot) | Out-Null
    foreach ($entry in $manifest.files) {
        $relative = $entry.path.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $sourceFile = Join-Path $portableRoot $relative
        $destinationFile = Join-Path $stagedRoot $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationFile)) | Out-Null
        Copy-Item -LiteralPath $sourceFile -Destination $destinationFile
    }
    $stagedManifest = $manifest | ConvertTo-Json -Depth 16 | ConvertFrom-Json
    $stagedManifest.version = $nextVersion
    $releaseFile = Join-Path $stagedRoot 'release.json'
    $stagedRelease = Get-Content -LiteralPath $releaseFile -Raw | ConvertFrom-Json
    $stagedRelease.version = $nextVersion
    $releaseJson = $stagedRelease | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($releaseFile, $releaseJson, [Text.UTF8Encoding]::new($false))
    $releaseBytes = [Text.UTF8Encoding]::new($false).GetBytes($releaseJson)
    $releaseEntry = $stagedManifest.files | Where-Object path -eq 'release.json' | Select-Object -First 1
    if (-not $releaseEntry) { throw 'The portable manifest must manage its release.json metadata.' }
    $releaseEntry.size = $releaseBytes.Length
    $releaseEntry.sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($releaseBytes)).ToLowerInvariant()
    $compressedStream = [IO.MemoryStream]::new()
    try {
        $gzip = [IO.Compression.GZipStream]::new($compressedStream, [IO.Compression.CompressionLevel]::SmallestSize, $true)
        try { $gzip.Write($releaseBytes, 0, $releaseBytes.Length) }
        finally { $gzip.Dispose() }
        $releaseEntry.compressedSize = $compressedStream.Length
    }
    finally { $compressedStream.Dispose() }
    [IO.File]::WriteAllText((Join-Path $stagedRoot 'update-manifest.json'),
        ($stagedManifest | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
    $workerPath = Join-Path $portableRoot 'ClipboardManager.UpdateWorker.exe'
    $stagedWorker = Join-Path $workspace 'ClipboardManager.UpdateWorker.exe'
    Copy-Item -LiteralPath $workerPath -Destination $stagedWorker
    $workerStart = [Diagnostics.ProcessStartInfo]::new($stagedWorker)
    $workerStart.WorkingDirectory = $workspace
    $workerStart.UseShellExecute = $false
    $workerStart.CreateNoWindow = $true
    foreach ($argument in @('--apply', $workspace, $portableRoot, '0', '0', $dataRoot, $appPath)) {
        $workerStart.ArgumentList.Add($argument)
    }
    $workerProcess = [Diagnostics.Process]::Start($workerStart)
    if (-not $workerProcess.WaitForExit(120000)) {
        $workerProcess.Kill($true)
        if (-not $workerProcess.WaitForExit(15000)) {
            throw 'The synthetic portable update worker could not be stopped after timeout.'
        }
        throw 'The synthetic portable update worker did not finish within 120 seconds.'
    }
    if ($workerProcess.ExitCode -ne 0) { throw "The synthetic portable update worker failed with code $($workerProcess.ExitCode)." }

    $installedManifest = Get-Content -LiteralPath (Join-Path $portableRoot 'update-manifest.json') -Raw | ConvertFrom-Json
    if ($installedManifest.version -ne $nextVersion) { throw 'The synthetic portable update version was not committed.' }
    foreach ($entry in $installedManifest.files) {
        $relative = $entry.path.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $installedFile = Join-Path $portableRoot $relative
        if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf) -or
            (Get-Item -LiteralPath $installedFile).Length -ne $entry.size -or
            (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Updated portable file failed installed-manifest validation: $($entry.path)."
        }
    }
    $updatedApp = Get-Process -Name 'ClipboardManager.App' -ErrorAction SilentlyContinue |
        Where-Object { try { $_.Path -eq $appPath } catch { $false } } | Select-Object -First 1
    if (-not $updatedApp -or $updatedApp.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The update worker did not leave the verified WinUI app running after its health check.'
    }
    $updatedApp.Refresh()
    if (-not $updatedApp.CloseMainWindow() -or -not $updatedApp.WaitForExit(15000)) {
        throw 'The updated portable WinUI app did not close cleanly.'
    }
    if ((Get-TreeHash $dataRoot) -cne $dataHashBeforeUpdate) {
        throw 'The synthetic LocalAppData tree changed during the portable program-file update.'
    }
    if (Test-Path -LiteralPath $updateParent) { throw 'The healthy portable update did not clean its transaction workspace.' }
    $appProcess = $null
    Write-Host "Portable synthetic update passed: $Version -> $nextVersion; manifest files/hash and LocalAppData tree verified."
    $dataHashBeforePortableRemoval = Get-TreeHash $dataRoot
    $resolvedPortable = [IO.Path]::GetFullPath($portableRoot)
    $temporaryPrefix = [IO.Path]::GetFullPath($temporary).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPortable.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedPortable) -ne 'portable-app') { throw 'Refusing to remove an unexpected portable application directory.' }
    Remove-Item -LiteralPath $resolvedPortable -Recurse -Force
    if ((Get-TreeHash $dataRoot) -cne $dataHashBeforePortableRemoval) {
        throw 'Removing the portable program files changed synthetic LocalAppData data.'
    }

    Write-Host "Portable verification passed: v$Version manifest/hash validation, isolated launch, synthetic legacy import and data retention after removing app files."
}
finally {
    if ($workerProcess) {
        try {
            if (-not $workerProcess.HasExited) {
                $workerProcess.Kill($true)
                if (-not $workerProcess.WaitForExit(15000)) {
                    throw 'The update worker process did not exit after termination was requested.'
                }
            }
        }
        catch {
            throw 'Cannot safely remove portable verification files while the update worker may still be running.'
        }
        $workerProcess.Dispose()
        $workerProcess = $null
    }
    $portablePrefix = [IO.Path]::GetFullPath($portableRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($candidate in @(Get-Process -Name 'ClipboardManager.App' -ErrorAction SilentlyContinue)) {
        try {
            if ($candidate.Path.StartsWith($portablePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                if (-not $candidate.HasExited) { $candidate.Kill($true); $candidate.WaitForExit() }
            }
        }
        catch { }
    }
    if ($appProcess -and -not $appProcess.HasExited) {
        $appProcess.Kill($true)
        $appProcess.WaitForExit()
    }
    if ($dataRootCreated -and (Test-Path -LiteralPath $dataRoot)) {
        if ([IO.Path]::GetFullPath($dataRoot) -ne [IO.Path]::GetFullPath((Join-Path $localAppData 'ClipboardManager'))) {
            throw 'Refusing to clean an unexpected synthetic data root.'
        }
        Remove-Item -LiteralPath $dataRoot -Recurse -Force
    }
    $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
    $temporaryPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTemporary.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTemporary) -notmatch '^cm-portable-verify-[0-9a-f]{32}$') {
        throw "Refusing to clean an unexpected portable verification path: $resolvedTemporary"
    }
    if (Test-Path -LiteralPath $resolvedTemporary) { Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force }
}










