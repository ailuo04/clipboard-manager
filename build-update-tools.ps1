[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.0.3',
    [switch]$SelfContainedAppHost
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The Windows .NET Framework compiler is required for the update helper.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$staging = Join-Path $output ('.tools-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    $metadata = Join-Path $staging 'Version.cs'
    [IO.File]::WriteAllText($metadata, ('[assembly: System.Reflection.AssemblyVersion("{0}.0")] [assembly: System.Reflection.AssemblyFileVersion("{0}.0")]' -f $Version), [Text.UTF8Encoding]::new($false))
    $shared = @((Join-Path $PSScriptRoot 'Updates\UpdateManifest.cs'), (Join-Path $PSScriptRoot 'Updates\UpdateFiles.cs'), $metadata)
    foreach ($target in @(@{ Source = 'Program.cs'; Name = 'ClipboardManager.Update.exe' }, @{ Source = 'Launcher.cs'; Name = 'ClipboardManager.exe' })) {
        $built = Join-Path $staging $target.Name
        & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /reference:System.Runtime.Serialization.dll /reference:System.Windows.Forms.dll "/win32icon:$(Join-Path $PSScriptRoot 'Assets\Clipboard.ico')" "/out:$built" @shared (Join-Path $PSScriptRoot "UpdateHelper\$($target.Source)")
        if ($LASTEXITCODE -ne 0) { throw "Update tool compilation failed: $($target.Name)" }
    }
    if ($SelfContainedAppHost) {
        [IO.File]::Move((Join-Path $output 'ClipboardManager.exe'), (Join-Path $output 'ClipboardManager.App.exe'), $true)
    }
    foreach ($name in @('ClipboardManager.exe', 'ClipboardManager.Update.exe')) {
        [IO.File]::Move((Join-Path $staging $name), (Join-Path $output $name), $true)
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    if ((Split-Path $resolved) -ne $output -or (Split-Path $resolved -Leaf) -notmatch '^\.tools-[0-9a-f]{32}$') { throw 'Unexpected update tool staging path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
