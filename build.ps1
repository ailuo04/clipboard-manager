param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows'),
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.0.8'
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$sdks = @(& $dotnet --list-sdks)
if ($sdks.Count -eq 0) { throw 'The .NET 8 SDK is required to build ClipboardManager.' }

& $dotnet build (Join-Path $PSScriptRoot 'ClipboardManager.csproj') --configuration Release --output $output "-p:Version=$Version" -p:UseAppHost=false
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $LASTEXITCODE" }
& (Join-Path $PSScriptRoot 'build-update-tools.ps1') -OutputDirectory $output -Version $Version
