$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$releaseNotes = Get-Content -LiteralPath (Join-Path $root 'RELEASE_NOTES.md') -Raw
$usage = Get-Content -LiteralPath (Join-Path $root 'docs\USAGE.md') -Raw
$installer = Get-Content -LiteralPath (Join-Path $root 'installer\ClipboardManager.iss') -Raw
$manifestScript = Get-Content -LiteralPath (Join-Path $root 'package.ps1') -Raw

if ($releaseNotes -notmatch '首次从 WPF 版升级' -or $releaseNotes -notmatch '完整安装器覆盖安装' -or
    $releaseNotes -notmatch '不要使用旧 WPF 应用内增量更新器') {
    throw 'Release notes must explicitly require the full installer for the first WPF-to-WinUI upgrade.'
}
if ($usage -notmatch '首个 WinUI 版本必须先退出旧程序，再运行完整安装器覆盖安装' -or
    $usage -notmatch '旧版应用内更新器不能升级 WinUI 包') {
    throw 'User documentation must not imply that the legacy WPF updater can install WinUI.'
}
if ($installer -notmatch '\[InstallDelete\]' -or $installer -notmatch 'ClipboardManager\.Update\.exe' -or
    $installer -notmatch 'ClipboardManager\.dll') {
    throw 'The full installer must remove the legacy WPF launcher/updater files without touching user data.'
}
if ($manifestScript -notmatch 'firstUpgrade\s*=\s*''full-installer''' -or
    $manifestScript -notmatch 'packageKind\s*=\s*''winui3''') {
    throw 'The release payload must identify WinUI and record its full-installer first-upgrade requirement.'
}

$portableCheck = Get-Content -LiteralPath (Join-Path $root 'tests\packaging\Test-PortablePackage.ps1') -Raw
$releaseWorkflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\release.yml') -Raw
$installerWorkflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\installer.yml') -Raw
if ($portableCheck -notmatch "GITHUB_ACTIONS -ne 'true'" -or
    $portableCheck -notmatch "RUNNER_ENVIRONMENT -ne 'github-hosted'" -or
    $releaseWorkflow -notmatch 'RUNNER_ENVIRONMENT:.*runner\.environment' -or
    $installerWorkflow -notmatch 'RUNNER_ENVIRONMENT:.*runner\.environment' -or
    $portableCheck -notmatch "ClipboardManager\.Storage\.Tests\\Fixtures" -or
    $portableCheck -notmatch "ClipboardManager\.UpdateWorker\.exe" -or
    $portableCheck -notmatch "'--apply'" -or
    $portableCheck -match 'api\.github\.com|GitHubReleaseClient' -or
    $releaseWorkflow -notmatch 'Test-PortablePackage\.ps1' -or
    $installerWorkflow -notmatch 'Test-PortablePackage\.ps1') {
    throw 'Portable synthetic launch/update verification must be runner-gated and wired into both release workflows; the portable test itself must not contact the GitHub Release API.'
}

Write-Host 'Legacy upgrade policy passed: release metadata requires a full installer; portable update verification is synthetic/offline and runner-gated. WPF migration itself remains unverified until hosted CI runs.'



