[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'),
    [string]$CompilerPath,
    [switch]$VerifyInstall
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Installer packaging requires Windows and PowerShell 7.' }
if ($VerifyInstall -and $env:GITHUB_ACTIONS -ne 'true') { throw 'Installation verification is restricted to disposable GitHub Actions runners.' }
$archivePath = (Resolve-Path -LiteralPath $Archive -ErrorAction Stop).Path
$checksumPath = [IO.Path]::ChangeExtension($archivePath, '.sha256')
$checksumParts = (Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+', 2
if ($checksumParts.Count -ne 2 -or $checksumParts[0] -notmatch '^[0-9a-fA-F]{64}$' -or $checksumParts[1] -ne [IO.Path]::GetFileName($archivePath)) {
    throw 'The input ZIP checksum file is invalid.'
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $checksumParts[0]) { throw 'The input ZIP failed SHA256 verification.' }
if (-not $CompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $CompilerPath = $command.Source }
    else { $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
}
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Inno Setup is not installed. Use the GitHub installer workflow, which uses its preinstalled compiler.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$name = "ClipboardManager-$Version-win-x64-setup"
$installerPath = Join-Path $output "$name.exe"
$checksumOutput = Join-Path $output "$name.sha256"
foreach ($path in @($installerPath, $checksumOutput)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite an existing release file: $path" }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$staging = Join-Path $output ('.installer-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'payload'

try {
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $payload)
    $release = Get-Content -LiteralPath (Join-Path $payload 'release.json') -Raw | ConvertFrom-Json
    if ($release.version -ne $Version -or $release.platform -ne 'win-x64' -or
        $release.packageKind -ne 'winui3' -or $release.firstUpgrade -ne 'full-installer') {
        throw 'The ZIP identity, package kind, or WPF migration policy does not match this installer.'
    }
    foreach ($file in @('ClipboardManager.App.exe', 'ClipboardManager.UpdateWorker.exe', 'update-manifest.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) { throw "Missing WinUI release file: $file" }
    }
    $updateManifest = Get-Content -LiteralPath (Join-Path $payload 'update-manifest.json') -Raw | ConvertFrom-Json
    if ($updateManifest.version -ne $Version -or $updateManifest.platform -ne 'win-x64' -or $updateManifest.packageKind -ne 'winui3') {
        throw 'The installed update manifest does not match the WinUI package.'
    }

    & $CompilerPath /Qp "/DAppVersion=$Version" "/DSourceDir=$payload" "/DOutputDir=$staging" (Join-Path $PSScriptRoot 'installer\ClipboardManager.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed: $LASTEXITCODE" }
    $builtInstaller = Join-Path $staging "$name.exe"
    if (-not (Test-Path -LiteralPath $builtInstaller -PathType Leaf)) { throw 'The compiler did not produce the installer.' }

    if ($VerifyInstall) {
        $installDirectory = Join-Path $staging 'installed-app'
        $dataRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ClipboardManager'))
        $localAppData = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $dataRoot.StartsWith($localAppData, [StringComparison]::OrdinalIgnoreCase)) { throw 'The installer test data root escaped LocalAppData.' }
        $dataRootExisted = Test-Path -LiteralPath $dataRoot
        [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
        $sentinel = Join-Path $dataRoot ('installer-check-' + [guid]::NewGuid().ToString('N') + '.txt')
        [IO.File]::WriteAllText($sentinel, 'Preserve synthetic clipboard user data during install and uninstall.')
        $registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8BCDA40E-D12B-4A21-BBD5-EF1653BD6F7B}_is1'
        if (Test-Path -LiteralPath $registryPath) { throw 'Refusing to overwrite an existing installation in the verification runner.' }
        $appProcess = $null
        function Invoke-Setup([string]$Path, [string[]]$SetupArguments) {
            $start = [Diagnostics.ProcessStartInfo]::new($Path)
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            foreach ($argument in $SetupArguments) { $start.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::Start($start)
            if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Installer operation timed out.' }
            if ($process.ExitCode -ne 0) { throw "Installer operation failed: $($process.ExitCode)" }
        }
        if (-not ('ClipboardInstallerPaths' -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
public static class ClipboardInstallerPaths {
    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint Expand(string path, StringBuilder buffer, uint capacity);
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUnicodeShellLink { void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags); }
    public static string ReadShortcut(string path) {
        object link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
        try { ((IPersistFile)link).Load(path, 0); var target = new StringBuilder(32768); ((IUnicodeShellLink)link).GetPath(target, target.Capacity, IntPtr.Zero, 4); return target.ToString(); }
        finally { Marshal.FinalReleaseComObject(link); }
    }
}
"@
        }
        try {
            $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$installDirectory", "/LOG=$(Join-Path $staging 'install.log')")
            Invoke-Setup $builtInstaller $arguments
            $registration = Get-ItemProperty -LiteralPath $registryPath
            if ($registration.DisplayVersion -ne $Version -or $registration.InstallLocation.TrimEnd('\') -ne $installDirectory) { throw 'The per-user uninstall registration is incorrect.' }
            $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) '剪贴板\剪贴板.lnk'
            $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) '剪贴板.lnk'
            foreach ($path in @($shortcut, $desktopShortcut)) {
                if (-not (Test-Path -LiteralPath $path)) { throw "Missing installed shortcut: $path" }
                $actualTarget = [ClipboardInstallerPaths]::ReadShortcut($path)
                $expectedTarget = Join-Path $installDirectory 'ClipboardManager.App.exe'
                $longPath = [Text.StringBuilder]::new(32768)
                $length = [ClipboardInstallerPaths]::Expand($actualTarget, $longPath, $longPath.Capacity)
                if ($length -eq 0 -or $length -ge $longPath.Capacity -or $longPath.ToString() -ne $expectedTarget) { throw "A shortcut targets the wrong executable: $actualTarget" }
            }
            foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
                $relative = [IO.Path]::GetRelativePath($payload, $file.FullName)
                $installedFile = Join-Path $installDirectory $relative
                if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf) -or
                    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash) {
                    throw "Installed file changed or is missing: $relative"
                }
            }
            foreach ($legacyFile in @('ClipboardManager.exe', 'ClipboardManager.dll', 'ClipboardManager.Update.exe', 'ClipboardManager.runtimeconfig.json', 'ClipboardManager.deps.json')) {
                if (Test-Path -LiteralPath (Join-Path $installDirectory $legacyFile)) { throw "Legacy WPF updater file remains after full-installer migration: $legacyFile" }
            }
            $appStart = [Diagnostics.ProcessStartInfo]::new((Join-Path $installDirectory 'ClipboardManager.App.exe'))
            $appStart.WorkingDirectory = $installDirectory
            $appStart.UseShellExecute = $false
            $appStart.CreateNoWindow = $true
            $appProcess = [Diagnostics.Process]::Start($appStart)
            for ($attempt = 0; $attempt -lt 60; $attempt++) {
                $appProcess.Refresh()
                if (-not $appProcess.HasExited -and $appProcess.MainWindowHandle -ne [IntPtr]::Zero) { break }
                if ($appProcess.HasExited) { throw "The installed WinUI app exited before opening its main window: $($appProcess.ExitCode)" }
                Start-Sleep -Milliseconds 250
            }
            if ($appProcess.MainWindowHandle -eq [IntPtr]::Zero) { throw 'The installed WinUI app did not open its main window.' }
            if (-not $appProcess.CloseMainWindow()) { throw 'The installed WinUI app did not accept a close request.' }
            if (-not $appProcess.WaitForExit(15000)) { throw 'The installed WinUI app did not exit within 15 seconds.' }
            if ($appProcess.ExitCode -ne 0) { throw "The installed WinUI app exited with code $($appProcess.ExitCode)." }
            $appProcess = $null
            Invoke-Setup $builtInstaller $arguments
            if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Reinstallation removed synthetic user data.' }
            Invoke-Setup (Join-Path $installDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $staging 'uninstall.log')")
            if ((Test-Path -LiteralPath $registryPath) -or (Test-Path -LiteralPath $shortcut) -or
                (Test-Path -LiteralPath $desktopShortcut) -or (Test-Path -LiteralPath (Join-Path $installDirectory 'ClipboardManager.App.exe'))) {
                throw 'Uninstall left the registered WinUI app or shortcuts.'
            }
            if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed synthetic user data.' }
            Write-Host 'Installer verification passed: WinUI startup, per-user registration, file hashes, shortcuts, reinstall, uninstall and data preservation.'
        }
        finally {
            if ($appProcess -and -not $appProcess.HasExited) { $appProcess.Kill($true); $appProcess.WaitForExit() }
            if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel -Force }
            if (-not $dataRootExisted -and (Test-Path -LiteralPath $dataRoot)) {
                $resolvedDataRoot = [IO.Path]::GetFullPath($dataRoot)
                if (-not $resolvedDataRoot.Equals($dataRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to clean an unexpected verification data root.' }
                Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force
            }
        }
    }

    $hash = (Get-FileHash -LiteralPath $builtInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $staging "$name.sha256"), "$hash  $name.exe`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($builtInstaller, $installerPath, $false)
    [IO.File]::Move((Join-Path $staging "$name.sha256"), $checksumOutput, $false)
    Write-Host "Installer: $installerPath"
    Write-Host "SHA256: $hash"
}
finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    $outputRoot = [IO.Path]::GetFullPath($output).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($outputRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^\.installer-[0-9a-f]{32}$') { throw 'Unexpected installer staging path.' }
    for ($attempt = 0; $attempt -lt 20 -and (Test-Path -LiteralPath $resolved); $attempt++) {
        try { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop }
        catch {
            $code = $_.Exception.HResult -band 0xFFFF
            if ($code -notin @(32, 33, 145) -or $attempt -eq 19) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}
