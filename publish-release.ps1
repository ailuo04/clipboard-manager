[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Directory = (Join-Path $PSScriptRoot 'dist'),
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = $env:GITHUB_REPOSITORY,
    [switch]$RefreshExisting
)

$ErrorActionPreference = 'Stop'
if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is required for release publication.' }
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; 'User-Agent' = 'ClipboardManager-release'; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
$api = "https://api.github.com/repos/$Repository"
$tag = "v$Version"
$updateTag = "updates-$tag"
$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the release source commit.' }

function Get-Release([string]$ReleaseTag) {
    try { return Invoke-RestMethod -Uri "$api/releases/tags/$ReleaseTag" -Headers $headers -TimeoutSec 60 }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { return $null }
        throw
    }
}

function Invoke-GitHub([string[]]$Arguments) {
    & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($Arguments[0]) $($Arguments[1])" }
}

function Assert-Assets($Release, [IO.FileInfo[]]$Files) {
    if ($Release.assets.Count -ne $Files.Count) { throw "Wrong asset count in $($Release.tag_name)." }
    foreach ($file in $Files) {
        $asset = @($Release.assets | Where-Object name -eq $file.Name)
        if ($asset.Count -ne 1 -or $asset[0].size -ne $file.Length -or $asset[0].state -ne 'uploaded') { throw "Missing or incorrect release asset: $($file.Name)" }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($asset[0].digest -and $asset[0].digest -ne "sha256:$hash") { throw "Release asset checksum mismatch: $($file.Name)" }
    }
}

function Remove-Assets([object[]]$Assets) {
    # Each worker deletes a disjoint asset ID; verify the final release before publishing it.
    $Assets | ForEach-Object -Parallel {
        $ErrorActionPreference = 'Stop'
        $asset = $_
        for ($attempt = 0; ; $attempt++) {
            try {
                Invoke-RestMethod -Method Delete -Uri "$using:api/releases/assets/$($asset.id)" -Headers $using:headers -TimeoutSec 60 | Out-Null
                break
            }
            catch {
                $response = $_.Exception.Response
                if ($response -and [int]$response.StatusCode -eq 404) { break }
                if ($response -and [int]$response.StatusCode -in @(403,429,500,502,503) -and $attempt -lt 2) { Start-Sleep -Seconds 30; continue }
                throw
            }
        }
    } -ThrottleLimit 4
}

function Upload-Assets([string]$ReleaseTag, [IO.FileInfo[]]$Files, [bool]$Replace) {
    for ($index = 0; $index -lt $Files.Count; $index += 20) {
        $batch = @($Files[$index..([Math]::Min($index + 19, $Files.Count - 1))].FullName)
        $arguments = @('release','upload',$ReleaseTag) + $batch + @('--repo',$Repository)
        if ($Replace) { $arguments += '--clobber' }
        Invoke-GitHub $arguments
    }
}

$directoryPath = (Resolve-Path -LiteralPath $Directory).Path
$name = "ClipboardManager-$Version-win-x64"
$downloadNames = @("$name.zip", "$name.sha256", "$name-setup.exe", "$name-setup.sha256")
$downloads = @($downloadNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $directoryPath $_) -ErrorAction Stop })
$manifestFile = Get-Item -LiteralPath (Join-Path $directoryPath "$name-update.json")
$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.version -ne $Version -or $manifest.platform -ne 'win-x64') { throw 'Invalid publication manifest.' }
$blobNames = @($manifest.files.sha256 | Sort-Object -Unique | ForEach-Object { "update-$_.gz" })
$updateFiles = @($manifestFile) + @($blobNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $directoryPath $_) -ErrorAction Stop })
if (@(Get-ChildItem -LiteralPath $directoryPath -File).Count -ne $downloads.Count + $updateFiles.Count) { throw 'Unexpected publication files.' }
foreach ($file in $manifest.files) {
    if ((Get-Item -LiteralPath (Join-Path $directoryPath "update-$($file.sha256).gz")).Length -ne $file.compressedSize) { throw "Wrong compressed file size: $($file.path)" }
}
foreach ($file in @($downloads | Where-Object Extension -in @('.exe','.zip'))) {
    $parts = (Get-Content -LiteralPath ([IO.Path]::ChangeExtension($file.FullName,'.sha256')) -Raw).Trim() -split '\s+',2
    if ($parts.Count -ne 2 -or $parts[1] -ne $file.Name -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $parts[0]) { throw "Release checksum failed: $($file.Name)" }
}

$main = Get-Release $tag
$updates = Get-Release $updateTag
if (($main -or $updates) -and -not $RefreshExisting) { throw 'A release already exists. Refresh requires explicit approval.' }
if (($main -and $main.immutable) -or ($updates -and $updates.immutable)) { throw 'Cannot refresh an immutable release.' }
if ($main -and $main.prerelease) { throw 'The application release must not be a prerelease.' }
if ($updates -and -not $updates.prerelease) { throw 'Update data must be a prerelease, never the latest application.' }

# Publish and verify the independent update data before changing the user-facing downloads.
if (-not $updates) {
    Invoke-GitHub -Arguments @('release','create',$updateTag,'--repo',$Repository,'--target',$commit,'--title',"自动更新数据 $tag（非安装包）",'--notes',"仅供应用内增量更新使用；请从 $tag 正式发行页下载安装包。",'--draft','--prerelease','--latest=false')
}
elseif (-not $updates.draft) { Invoke-GitHub -Arguments @('release','edit',$updateTag,'--repo',$Repository,'--draft=true','--latest=false') }
Upload-Assets $updateTag $updateFiles ([bool]$updates)
$updates = Get-Release $updateTag
$updateNames = @($updateFiles.Name)
$obsoleteUpdates = @($updates.assets | Where-Object name -NotIn $updateNames)
if (@($obsoleteUpdates | Where-Object { $_.name -notmatch '^update-[0-9a-f]{64}\.gz$' }).Count) { throw 'Refusing to delete unknown update-data assets.' }
Remove-Assets $obsoleteUpdates
Assert-Assets (Get-Release $updateTag) $updateFiles
Invoke-GitHub -Arguments @('release','edit',$updateTag,'--repo',$Repository,'--draft=false','--prerelease','--latest=false')

if (-not $main) {
    Invoke-GitHub -Arguments @('release','create',$tag,'--repo',$Repository,'--target',$commit,'--title',"剪贴板 $tag",'--notes-file',(Join-Path $PSScriptRoot 'RELEASE_NOTES.md'),'--draft')
}
else {
    $unknown = @($main.assets | Where-Object { $_.name -notin $downloadNames -and $_.name -ne "$name-update.json" -and $_.name -notmatch '^update-[0-9a-f]{64}\.gz$' })
    if ($unknown.Count) { throw 'Refusing to delete unknown application release assets.' }
    Invoke-GitHub -Arguments @('release','edit',$tag,'--repo',$Repository,'--draft=true')
}
Upload-Assets $tag $downloads ([bool]$main)
$main = Get-Release $tag
$obsoleteDownloads = @($main.assets | Where-Object name -NotIn $downloadNames)
if (@($obsoleteDownloads | Where-Object { $_.name -ne "$name-update.json" -and $_.name -notmatch '^update-[0-9a-f]{64}\.gz$' }).Count) { throw 'Refusing to delete an unrecognized asset added during publication.' }
Remove-Assets $obsoleteDownloads
Assert-Assets (Get-Release $tag) $downloads

if ($RefreshExisting) {
    $oldTag = (git rev-parse "refs/tags/$tag").Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Existing release tag is missing.' }
    $oldCommit = (git rev-parse "refs/tags/$tag^{commit}").Trim()
    if ($oldCommit -ne $commit) {
        git -c 'user.name=github-actions[bot]' -c 'user.email=41898282+github-actions[bot]@users.noreply.github.com' tag -f -a $tag -m "Refresh $tag with independent update data"
        if ($LASTEXITCODE -ne 0) { throw 'Could not refresh the approved source tag.' }
        $lease = '--force-with-lease=refs/tags/{0}:{1}' -f $tag,$oldTag
        git push $lease origin "refs/tags/$tag"
        if ($LASTEXITCODE -ne 0) { throw 'Source tag changed concurrently; release remains a draft.' }
    }
}
Invoke-GitHub -Arguments @('release','edit',$tag,'--repo',$Repository,'--title',"剪贴板 $tag",'--notes-file',(Join-Path $PSScriptRoot 'RELEASE_NOTES.md'),'--draft=false','--latest')
Write-Host "Published $tag with exactly four downloadable assets; update data is isolated at $updateTag."
