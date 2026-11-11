[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.0.0'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The WinUI build requires Windows and PowerShell 7.' }
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$sdk = (& $dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.0\.') { throw ".NET 10 SDK is required; resolved SDK was '$sdk'." }
& $dotnet restore (Join-Path $PSScriptRoot 'ClipboardManager.sln') --nologo
if ($LASTEXITCODE -ne 0) { throw "Solution restore failed: $LASTEXITCODE" }
& $dotnet build (Join-Path $PSScriptRoot 'ClipboardManager.sln') --configuration Release --no-restore -p:Platform=x64 "-p:Version=$Version" --nologo
if ($LASTEXITCODE -ne 0) { throw "Solution build failed: $LASTEXITCODE" }
