$ErrorActionPreference = 'Stop'
$directory = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows'
$assembly = Join-Path $directory 'ClipboardManager.dll'
if (-not (Test-Path -LiteralPath $assembly)) { & (Join-Path $PSScriptRoot 'build.ps1') }
$launcher = Join-Path $directory 'ClipboardManager.exe'
if (Test-Path -LiteralPath $launcher) {
    Start-Process -FilePath $launcher -WorkingDirectory $directory -WindowStyle Hidden
}
else {
    Start-Process -FilePath (Get-Command dotnet.exe).Source -ArgumentList ('"' + $assembly + '"') -WorkingDirectory $directory -WindowStyle Hidden
}
