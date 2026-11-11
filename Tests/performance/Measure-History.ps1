[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '..\..\docs\performance\history-5000-baseline.json'))

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The history baseline script requires Windows PowerShell 7.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$harness = Join-Path $PSScriptRoot 'ClipboardManager.PerformanceHarness.csproj'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('cm-history-measure-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$metricsPath = Join-Path $temporary 'measurements.json'

try {
    & dotnet run --project $harness --configuration Release --no-launch-profile -- --output $metricsPath
    if ($LASTEXITCODE -ne 0) { throw 'The synthetic history benchmark failed.' }
    $metrics = Get-Content -LiteralPath $metricsPath -Raw | ConvertFrom-Json
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $system = Get-CimInstance -ClassName Win32_ComputerSystem
    $processor = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
    $physicalDisks = @(Get-PhysicalDisk -ErrorAction SilentlyContinue | ForEach-Object {
        [ordered]@{ mediaType = [string]$_.MediaType; busType = [string]$_.BusType; sizeBytes = [long]$_.Size }
    })
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the active .NET SDK version.' }
    $report = [ordered]@{
        schema = 1
        generatedAtLocal = [DateTimeOffset]::Now.ToString('o')
        environment = [ordered]@{
            machineModel = [string]$system.Model
            operatingSystem = [string]$os.Caption
            operatingSystemVersion = [string]$os.Version
            operatingSystemBuild = [string]$os.BuildNumber
            processor = [string]$processor.Name
            logicalProcessorCount = [int]$processor.NumberOfLogicalProcessors
            memoryGiB = [Math]::Round([double]$system.TotalPhysicalMemory / 1GB)
            diskMediaTypes = @($physicalDisks | ForEach-Object mediaType | Sort-Object -Unique)
            diskBusTypes = @($physicalDisks | ForEach-Object busType | Sort-Object -Unique)
            dotnetSdk = $sdk
            note = 'Machine name, user paths, clipboard contents and user history are intentionally excluded.'
        }
        benchmark = $metrics
    }
    $fullOutput = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullOutput)) | Out-Null
    $json = $report | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($fullOutput, $json + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Synthetic 5,000-record baseline: $fullOutput"
    Write-Host "Search first: $($metrics.measurements.firstLongSearchMilliseconds) ms; repeated median: $($metrics.measurements.repeatLongSearchMilliseconds.medianMilliseconds) ms; p95: $($metrics.measurements.repeatLongSearchMilliseconds.p95Milliseconds) ms."
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $expectedPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^cm-history-measure-[0-9a-f]{32}$') {
        throw "Refusing to clean an unexpected benchmark directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}



