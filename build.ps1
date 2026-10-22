param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows'),
    [switch]$RuntimeCompiler
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$sdks = @(& $dotnet --list-sdks)
if ($sdks.Count -gt 0 -and -not $RuntimeCompiler) {
    & $dotnet build (Join-Path $PSScriptRoot 'ClipboardManager.csproj') --configuration Release --output $output
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed: $LASTEXITCODE" }
    return
}

# Use only the already installed compiler and Windows Desktop runtime.
$compiler = Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Install the .NET 8 SDK, or run with a PowerShell 7 distribution that already includes Roslyn.' }
$dotnetRoot = Split-Path $dotnet
$desktop = Get-ChildItem -LiteralPath (Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App') -Directory |
    Where-Object { $_.Name -match '^8\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $desktop) { throw '.NET 8 Windows Desktop runtime is required.' }
$core = Join-Path $dotnetRoot ('shared\Microsoft.NETCore.App\' + $desktop.Name)
if (-not (Test-Path -LiteralPath $core)) { throw "Matching .NET runtime was not found: $core" }
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path $compiler

$referencePaths = @{}
foreach ($directory in @($core, $desktop.FullName)) {
    foreach ($file in Get-ChildItem -LiteralPath $directory -Filter '*.dll') {
        try {
            $null = [Reflection.AssemblyName]::GetAssemblyName($file.FullName)
            $referencePaths[$file.Name] = $file.FullName
        }
        catch [BadImageFormatException] { }
    }
}
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
foreach ($path in $referencePaths.Values) { $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($path)) }
$parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion([Microsoft.CodeAnalysis.CSharp.LanguageVersion]::CSharp12)
$trees = [Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
$implicitUsings = 'global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;'
$trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($implicitUsings, $parseOptions))
$sourceFiles = Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.verification)[\\/]' }
foreach ($file in $sourceFiles) {
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file.FullName), $parseOptions, $file.FullName))
}

function New-ResourceProvider([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    return [Func[IO.Stream]] { [IO.MemoryStream]::new($bytes, $false) }.GetNewClosure()
}
$resources = [Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
$resourceFiles = @((Get-Item -LiteralPath (Join-Path $PSScriptRoot 'App.xaml'))) + @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Views') -Filter '*.xaml')
foreach ($file in $resourceFiles) {
    $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName).Replace('\', '.').Replace('/', '.')
    $resources.Add([Microsoft.CodeAnalysis.ResourceDescription]::new("ClipboardManager.$relative", (New-ResourceProvider $file.FullName), $true))
}
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::WindowsApplication).
    WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).
    WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable).
    WithConcurrentBuild($false)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('ClipboardManager', $trees, $references, $options)
[IO.Directory]::CreateDirectory($output) | Out-Null
$assemblyPath = Join-Path $output 'ClipboardManager.dll'
$stream = [IO.File]::Create($assemblyPath)
try { $result = $compilation.Emit($stream, $null, $null, $null, $resources, $null, [Threading.CancellationToken]::None) }
finally { $stream.Dispose() }
foreach ($diagnostic in $result.Diagnostics) {
    if ($diagnostic.Severity -in @('Warning', 'Error')) { Write-Host $diagnostic.ToString() }
}
if (-not $result.Success) { throw 'C# compilation failed.' }

$runtimeConfig = @{
    runtimeOptions = @{
        tfm = 'net8.0'
        frameworks = @(
            @{ name = 'Microsoft.NETCore.App'; version = '8.0.0' },
            @{ name = 'Microsoft.WindowsDesktop.App'; version = '8.0.0' }
        )
    }
} | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $output 'ClipboardManager.runtimeconfig.json'), $runtimeConfig, [Text.UTF8Encoding]::new($false))

# A small Windows subsystem launcher avoids opening a console when double-clicked.
$frameworkCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (Test-Path -LiteralPath $frameworkCompiler) {
    $launcherSource = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
internal static class DesktopLauncher {
    [STAThread] private static int Main() {
        try {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string host = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            var info = new ProcessStartInfo(host, "\"" + Path.Combine(directory, "ClipboardManager.dll") + "\"");
            info.WorkingDirectory = directory;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            using (var process = Process.Start(info)) {
                process.WaitForExit();
                if (process.ExitCode != 0) MessageBox.Show("应用已异常退出。请检查 %LOCALAPPDATA%\\ClipboardManager\\app.log，并确认已安装 .NET 8 Desktop Runtime。", "剪贴板");
                return process.ExitCode;
            }
        } catch (Exception error) { MessageBox.Show(error.Message, "剪贴板"); return 1; }
    }
}
'@
    $launcherPath = Join-Path $output 'Launcher.generated.cs'
    [IO.File]::WriteAllText($launcherPath, $launcherSource, [Text.UTF8Encoding]::new($false))
    try {
        & $frameworkCompiler /nologo /target:winexe /optimize+ /codepage:65001 /reference:System.Windows.Forms.dll "/out:$(Join-Path $output 'ClipboardManager.exe')" $launcherPath
        if ($LASTEXITCODE -ne 0) { throw 'Desktop launcher compilation failed.' }
    }
    finally { [IO.File]::Delete($launcherPath) }
}
Write-Host "Build succeeded: $assemblyPath"
