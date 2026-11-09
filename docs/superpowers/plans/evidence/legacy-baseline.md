# 旧版 WPF 基线证据（Task 1）

记录日期：2026-11-10
分支：`codex/winui3-redesign-implementation`
基线提交：`0694fd0`（本任务开始前的 HEAD）
范围：仅记录旧版 WPF 应用的测试基线、文件布局与首发切换策略。本任务不创建 .NET 10 解决方案、不改动源码、不改动计划或设计文档。

> 本文所有路径均为仓库相对路径或 Windows 环境变量形式；不记录任何开发者本机绝对路径、真实剪贴板内容、凭证或私钥。

## 1. 基线测试结果

命令（与任务简报一致）：

```
dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj -c Release --nologo
```

环境：Windows x64；工作树干净（`git status --short` 无输出）；可用的 .NET SDK 为 `10.0.403`，被测项目自身目标框架为 `net8.0-windows`（`ClipboardManager.csproj`：`TargetFramework` = `net8.0-windows`，`UseWPF` = `true`，`OutputType` = `WinExe`，`AssemblyName` = `ClipboardManager`）。

实际输出（原样摘录）：

```
已通过! - 失败:     0，通过:    72，已跳过:     0，总计:    72，持续时间: 16 s - ClipboardManager.Tests.dll (net8.0)
```

记录值：

| 项目 | 值 |
|---|---|
| 总计 | 72 |
| 通过 | 72 |
| 失败 | 0 |
| 跳过 | 0 |
| 测试持续时间 | 16 s |
| 命令整体耗时（含还原/构建） | 本次实测约 21 s |
| 退出码 | 0 |

说明：

- 该结果来自真实命令输出，不是推算值；同一组数字（72/72，16 s）也记录在 SDD 进度账本中。
- 这是**旧版 WPF 测试工程**的结果，只覆盖旧实现。它不代表新解决方案、SQLite 迁移、打包或安装器迁移已经通过；这些在 Task 1 中**均未执行**。

## 2. 旧版数据布局

数据根目录与文件名依据 `Utilities/AppPaths.cs` 逐项核对：

| 项 | 值 | 来源 |
|---|---|---|
| 数据根目录 | `%LOCALAPPDATA%\ClipboardManager` | `AppPaths.RootDirectory`（`LocalApplicationData` + `ClipboardManager`） |
| 图片目录 | `<数据根目录>\images` | `AppPaths.ImagesDirectory` |
| 历史文件 | `<数据根目录>\history.json` | `AppPaths.HistoryFilePath` |
| 主题文件 | `<数据根目录>\theme.json` | `AppPaths.ThemeFilePath` |
| 日志文件 | `<数据根目录>\app.log` | `AppPaths.LogFilePath` |
| 目录创建 | 首次写日志时创建数据根目录与 `images` 子目录 | `AppPaths.EnsureDirectories()`，由 `AppLogger` 调用 |

补充事实（同样来自 `AppPaths.cs` 之外的既有文件，用于说明“程序文件”与“用户数据”的边界）：

- 更新清单 `update-manifest.json` 与更新工作区 `.clipboard-update` 位于**安装目录**，不在数据根目录（`Updates/UpdateFiles.cs`：`ManifestName`、`WorkspaceName`）。
- `UpdateFiles.ResolvePath` 显式拒绝解析到 `%LOCALAPPDATA%\ClipboardManager` 及其子目录的任何更新路径，即更新器在设计上不得改写历史/图片/日志（`Updates/UpdateFiles.cs`）。
- `UpdateFiles.ValidateManifest` 把 `.clipboard-update`、`update-manifest.json`、`unins*`、`history.json`、`images`、`app.log*` 视为受保护名称，不允许出现在增量清单里。
- 日志轮转：`app.log` 超过 2 MB 时改名为 `app.log.old`（覆盖式），再追加写入（`Utilities/AppLogger.cs`）。
- 数据文件结构（仅用于迁移字段核对）：`history.json` 为 `Version`=1 的文档，含 `records`、`settings`、`pendingImageDeletes`；记录字段为 `id`、`kind`（`Text`/`Image`）、`text`、`imageFileName`、`byteLength`、`imageWidth`、`imageHeight`、`contentHash`、`copiedAtUtc`、`isPinned`；图片文件名约定为 32 位十六进制 GUID + `.png`。`theme.json` 为 `{ "theme": "System" | "Light" | "Dark" }`。

## 3. 发布资产命名

依据 `.github/workflows/release.yml`、`installer.ps1`，并以 `package.ps1`、`package-updates.ps1`、`publish-release.ps1` 交叉核对（版本号统一为 `^\d+\.\d+\.\d+$`）：

| 资产 | 命名 | 来源 |
|---|---|---|
| 便携 ZIP | `ClipboardManager-<version>-win-x64.zip` | `release.yml`（`dist/ClipboardManager-<version>-win-x64.zip`）、`package.ps1` |
| ZIP 校验 | `ClipboardManager-<version>-win-x64.sha256` | `package.ps1`、`publish-release.ps1` |
| 安装器 | `ClipboardManager-<version>-win-x64-setup.exe` | `installer.ps1`（`$name = "ClipboardManager-$Version-win-x64-setup"`） |
| 安装器校验 | `ClipboardManager-<version>-win-x64-setup.sha256` | `installer.ps1` |
| 增量更新清单 | `ClipboardManager-<version>-win-x64-update.json` | `package-updates.ps1`、`publish-release.ps1` |
| 增量内容块 | `update-<sha256>.gz`（按文件哈希去重） | `package-updates.ps1`、`publish-release.ps1` |

- 发布工作流把 `dist/*` 整体上传为构建产物，发布脚本要求上述文件集合完全匹配，否则拒绝发布。
- 安装器构建要求输入 ZIP 的 `release.json` 中 `version` 与 `platform`（`win-x64`）一致，并校验 ZIP 的 SHA-256。

## 4. 安装默认路径与安装形态

依据 `installer.ps1`，并以 `installer/ClipboardManager.iss` 交叉核对：

| 项 | 值 |
|---|---|
| 默认安装目录 | `{localappdata}\Programs\ClipboardManager`（即 `%LOCALAPPDATA%\Programs\ClipboardManager`） |
| 权限 | 用户级安装，`PrivilegesRequired=lowest`，不要求管理员 |
| 安装标识 | `AppId={{8BCDA40E-D12B-4A21-BBD5-EF1653BD6F7B}` |
| 卸载注册表项 | `HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8BCDA40E-D12B-4A21-BBD5-EF1653BD6F7B}_is1` |
| 开始菜单快捷方式 | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\剪贴板\剪贴板.lnk` → `<安装目录>\ClipboardManager.exe` |
| 桌面快捷方式 | `%USERPROFILE%\Desktop\剪贴板.lnk` → `<安装目录>\ClipboardManager.exe` |
| 静默安装参数 | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=<目录> /LOG=<日志>` |
| 卸载入口 | `<安装目录>\unins000.exe` |
| 卸载时清理增量文件 | `<安装目录>\ClipboardManager.Update.exe --uninstall-managed` |

- `installer.ps1 -VerifyInstall` 会校验：注册表版本与安装位置、两个快捷方式目标、安装文件逐字节哈希、应用能打开主窗口并正常退出、覆盖安装后数据保留、卸载后程序文件与快捷方式被移除但用户数据仍保留。
- 该验证开关被硬性限制为 `GITHUB_ACTIONS=true` 的一次性 CI runner，本机运行会被拒绝（`installer.ps1` 顶部检查）。因此“安装器/迁移已通过”不能由本任务声称。

## 5. 互斥体名与旧应用入口

| 项 | 值 | 来源 |
|---|---|---|
| 应用单实例互斥体 | `Local\ClipboardManager.Desktop.v1` | 见下方说明 |
| 更新互斥体 | `Local\ClipboardManager.Update.<SHA256(安装根目录全路径，去尾部分隔符并转大写)>`（64 位**大写**十六进制） | `Updates/UpdateFiles.cs` `LockName(string root)` |
| 安装器互斥体声明 | `AppMutex=Local\ClipboardManager.Desktop.v1` | 补充核对：`installer/ClipboardManager.iss` |

**核对来源（已按控制器裁定放宽）。** 数据布局与发布资产仍只依据 `Utilities/AppPaths.cs`、`installer.ps1`、`Updates/UpdateFiles.cs` 与 `.github/workflows/release.yml`。互斥体与入口链补充依据以下文件，且**仅用于这两项**，不用于推导数据布局或发布资产：

- `App.xaml.cs`：`Local\ClipboardManager.Desktop.v1` 的创建与释放。
- `installer/ClipboardManager.iss`：`AppMutex=Local\ClipboardManager.Desktop.v1` 声明。
- `UpdateHelper/Launcher.cs`、`UpdateHelper/Program.cs`：启动器与 worker 的入口、参数（`--apply`/`--recover`/`--uninstall-managed`/`--verify-update`）与健康标记等待。
- `build-update-tools.ps1`：两个 helper 的编译方式与产物文件名（含自包含发布时把应用主机改名为 `ClipboardManager.App.exe`）。

上述放宽的原因：仅凭原四个来源无法核实应用单实例互斥体与旧应用入口链——原四个来源只包含**更新**互斥体（`UpdateFiles.LockName`）。范围仍严格限定为文档证据，不改动任何源码。

旧应用入口链（依据 `build-update-tools.ps1`、`UpdateHelper/Launcher.cs`、`UpdateHelper/Program.cs`、`ClipboardManager.csproj`）：

1. 用户启动 `<安装目录>\ClipboardManager.exe`。该文件不是 .NET 应用主机，而是由 .NET Framework `csc.exe` 编译的启动器（`UpdateHelper/Launcher.cs`）。
2. 启动器先检查 `.clipboard-update\journal.json`：存在则调用 `worker.exe --recover` 恢复未完成更新；存在 `complete` 则清理工作区。
3. 启动器优先启动 `<安装目录>\ClipboardManager.App.exe`（自包含发布时，原应用主机被改名为该文件）；否则回退到 `runtime\dotnet.exe ClipboardManager.dll`。
4. 真正的 WPF 应用为 `ClipboardManager.dll`（`net8.0-windows`、`UseWPF`）；`ClipboardManager.Update.exe` 是独立更新 worker，支持 `--apply`、`--recover`、`--uninstall-managed`。
5. 启动器支持 `--verify-update`：等待新版写出 `.clipboard-update\ready` 健康标记，失败即返回非零。

以上入口链是“旧 helper 直接增量替换新架构”风险评估的对象：启动器按固定文件名寻找并等待子进程，新 WinUI 应用必须满足同一文件名/健康标记契约，否则不能走增量路径。

## 6. 隔离升级验证协议（未来执行，当前尚未执行）

**状态：未执行。** 本节是可重演的协议与判定标准，不是结果记录。任何“升级已通过”的结论都必须来自实际执行并保存输出；在此之前一律按“未证明”处理。

本节分两条**互不替代**的轨道：

| 轨道 | 目的 | 在哪里跑 | 是否覆盖安装/卸载 |
|---|---|---|---|
| **A. 安装/升级/卸载协议**（§6.1–§6.10） | 证明 WPF → WinUI 首发切换与数据迁移 | 一次性 Windows VM / CI runner 的**一次性 Windows 账户**，使用该账户**正常的**已知文件夹 | 是 |
| **B. 环境重定向冒烟**（§6.11） | 只证明“环境变量重定向后的数据根目录确实落在隔离根内” | 本机临时目录、子进程 | **否**（明确不是安装/迁移测试） |

轨道 A 中**禁止**覆盖 `USERPROFILE`/`APPDATA`：安装器的快捷方式与卸载注册表项都按真实账户解析，覆盖后既无法验证也无法清理。轨道 B 只验证文件路径重定向，不能用来声称安装或迁移通过。

### 6.0 隔离边界与前提

- 轨道 A 必须在一台**可丢弃**的 Windows VM 或一次性 CI runner 上执行，并使用该环境里**新建的一次性 Windows 账户**（或 CI runner 的临时账户）。不得在开发者日常桌面或真实用户数据目录上执行。
- 环境变量**不能**隔离以下副作用，因此轨道 A 依赖“整机/整个账户可丢弃”，而不是依赖环境变量：
  1. `HKCU` 卸载注册表项 `{8BCDA40E-D12B-4A21-BBD5-EF1653BD6F7B}_is1`；
  2. 开始菜单与桌面快捷方式（由 Shell 已知文件夹解析）；
  3. 应用单实例互斥体 `Local\ClipboardManager.Desktop.v1`（会话/机器级内核对象）。
- **禁止**在普通开发桌面上执行 §6.1–§6.10：卸载步骤会删除 `HKCU` 卸载项与快捷方式，可能命中你日常使用的那套安装。
- 若本次运行环境不是一次性账户（例如本机已有同 AppId 的安装），**立即停止**，改到一次性 VM/runner 重跑。

#### 6.0.1 为什么安装/迁移测试不能靠环境变量重定向

旧应用的数据根目录来自 `Environment.GetFolderPath(LocalApplicationData)`，而不是直接读 `%LOCALAPPDATA%`。实测（Windows，PowerShell 7.6.9，全新子进程）表明：

- 只覆盖 `LOCALAPPDATA` 时，`GetFolderPath(LocalApplicationData)` 仍解析到 `<USERPROFILE>\AppData\Local`；
- 覆盖 `USERPROFILE` 后，它解析为 `<USERPROFILE>\AppData\Local`，即**跟随 `USERPROFILE`**；
- `APPDATA` 与 `TEMP` 分别直接对应 `GetFolderPath(ApplicationData)` 与 `GetTempPath()`；
- `GetFolderPath` 在**同一进程内首次调用后会被缓存**，之后再改变量不会生效；
- 覆盖 `USERPROFILE`/`APPDATA` 后，`GetFolderPath('Desktop')`/`('Programs')` 返回**空值**——快捷方式既不会被重定向，也无法再被验证。

结论：环境变量重定向只适合做“数据根目录是否落在隔离根内”的冒烟（§6.11）。安装/升级/卸载必须在**真实一次性账户的正常已知文件夹**下执行。

#### 6.0.2 前置条件（轨道 A）

- 一次性账户从未安装过本应用：`HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8BCDA40E-D12B-4A21-BBD5-EF1653BD6F7B}_is1` 必须不存在；存在则说明环境不可用，停止并换环境。
- 磁盘剩余空间足够容纳旧版 + 新版两份程序与合成数据（建议 ≥ 2 GB）。
- 全程只用**合成数据**：虚构文本（例如 `sample-text-001`）、程序生成的小 PNG、合成设置值；不得复制任何真实剪贴板内容。
- 需要能取得旧版与新版的安装器与其 `.sha256` sidecar（来自已发布资产或本地产物）。

### 6.1 确认一次性账户的已知文件夹可用

在**一次性账户**里新开一个 PowerShell 7 会话；后续 §6.2–§6.10 全部在同一会话内继续。

```powershell
$ErrorActionPreference = 'Stop'

# 本协议不覆盖任何环境变量：安装器与快捷方式都按这个账户的真实已知文件夹解析。
$known = [ordered]@{
  LocalApplicationData = [Environment]::GetFolderPath('LocalApplicationData')
  ApplicationData      = [Environment]::GetFolderPath('ApplicationData')
  UserProfile          = [Environment]::GetFolderPath('UserProfile')
  Programs             = [Environment]::GetFolderPath('Programs')
  Desktop              = [Environment]::GetFolderPath('Desktop')
}

$unresolved = @($known.GetEnumerator() | Where-Object { [string]::IsNullOrWhiteSpace($_.Value) } | ForEach-Object { $_.Key })
if ($unresolved.Count -gt 0) {
  # 不计为通过：runner 账户本身不健康，必须先修好再跑。
  throw ('INCONCLUSIVE: this disposable profile cannot resolve: ' + ($unresolved -join ', ') + '. Fix the runner profile instead of marking the run as passed.')
}
foreach ($k in $known.Keys) { Write-Host ('{0,-20} {1}' -f $k, $known[$k]) }

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8BCDA40E-D12B-4A21-BBD5-EF1653BD6F7B}_is1'
if (Test-Path -LiteralPath $uninstallKey) {
  throw 'INCONCLUSIVE: this profile already has an installation of the app. Use a clean disposable account.'
}

$dataRoot   = Join-Path $known.LocalApplicationData 'ClipboardManager'
$workRoot   = Join-Path $env:TEMP ('cm-mig-' + [guid]::NewGuid().ToString('N'))
$installDir = Join-Path $workRoot 'app'          # 旧版与新覆盖安装共用同一目录
$logsDir    = Join-Path $workRoot 'logs'
New-Item -ItemType Directory -Force -Path $installDir, $logsDir | Out-Null
Write-Host "dataRoot=$dataRoot"
Write-Host "installDir=$installDir"
```

> `$dataRoot` 是**真实**的 `%LOCALAPPDATA%\ClipboardManager`，不是重定向目录。这正是要验证的目标路径。

### 6.2 安装旧版样本（安装到本次演练目录）

```powershell
# 旧版安装器与校验文件来自已发布资产（示例版本号，按实际替换）。
$oldSetup     = Join-Path $workRoot 'ClipboardManager-0.0.7-win-x64-setup.exe'
$oldSetupHash = Join-Path $workRoot 'ClipboardManager-0.0.7-win-x64-setup.sha256'

# 归一化两侧再比较：sidecar 可能带 BOM、大小写差异或前后空白；
# 任何解析失败或长度不符都直接报错，不做“尽力而为”的比较。
function Read-ExpectedSha256([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing checksum file: $Path" }
  # 去掉 BOM 与首尾空白，再取第一个空白分隔字段作为哈希。
  $raw = (Get-Content -LiteralPath $Path -Raw).TrimStart([char]0xFEFF).Trim()
  $parts = $raw -split '\s+', 2
  $hash = $parts[0]
  if ($hash -notmatch '^[0-9a-fA-F]{64}$') { throw "Malformed SHA256 in $Path : '$hash'" }
  if ($parts.Count -eq 2) {
    # 文件名可省略；给出时按发布脚本约定（sidecar 基名 + .exe 或 .zip）比对，不一致只告警。
    $listed = $parts[1].TrimStart('*')
    $stem = [IO.Path]::GetFileNameWithoutExtension($Path)
    $matchesStem = @("$stem.exe", "$stem.zip") | Where-Object { [string]::Equals($_, $listed, [StringComparison]::OrdinalIgnoreCase) }
    if (-not $matchesStem) { Write-Warning "Checksum file lists '$listed', which does not match sidecar base name '$stem'." }
  }
  return $hash.ToUpperInvariant()
}

$expected = Read-ExpectedSha256 $oldSetupHash
$actual   = (Get-FileHash -LiteralPath $oldSetup -Algorithm SHA256).Hash.ToUpperInvariant()
if (-not [string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Old installer SHA256 mismatch. expected=$expected actual=$actual"
}
Write-Host "Old installer SHA256 OK: $actual"

$oldInstall = Start-Process -FilePath $oldSetup -Wait -PassThru -ArgumentList @(
  '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
  "/DIR=$installDir", "/LOG=$(Join-Path $logsDir 'install-old.log')")
if ($oldInstall.ExitCode -ne 0) { throw "Old installer failed: $($oldInstall.ExitCode)" }
```

判定：

- `$installDir` 下存在 `ClipboardManager.exe`、`ClipboardManager.dll`、`ClipboardManager.runtimeconfig.json`、`ClipboardManager.Update.exe`、`update-manifest.json`；
- 卸载注册表项存在且 `InstallLocation` 指向 `$installDir`；
- 开始菜单快捷方式 `$known.Programs\剪贴板\剪贴板.lnk` 与桌面快捷方式 `$known.Desktop\剪贴板.lnk` 都存在（安装器会创建它们，§6.8 必须验证它们被移除）。

### 6.3 启动实际 UI 宿主、写入合成数据

**重要：`ClipboardManager.exe` 不是 UI 进程。** 它是 .NET Framework 启动器（见第 5 节），会再拉起真正的 UI 宿主并等待其退出。因此**不要**用启动器进程的句柄判断“应用是否在运行/是否显示窗口”。

真正的 UI 宿主按第 5 节入口链选择：

1. 存在 `<安装目录>\ClipboardManager.App.exe` → 直接启动它；
2. 否则 → `<安装目录>\runtime\dotnet.exe <安装目录>\ClipboardManager.dll`。

下面六个辅助函数在**同一会话**内定义一次，§6.3–§6.8 共用（只探测互斥体与精确进程路径，从不按名字终止进程）：

```powershell
function Get-AppMutex {
  # 只打开/创建同名的本地互斥体句柄；不终止、不影响任何进程。
  return [Threading.Mutex]::new($false, 'Local\ClipboardManager.Desktop.v1')
}

function Test-AppMutexHeld {
  # $true = 已被某个实例持有（有应用在运行）。
  # 只有在本次调用确实取得所有权时才释放；否则立即 Dispose 句柄。
  $m = Get-AppMutex
  $acquired = $false
  try {
    try { $acquired = $m.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }   # 前一个持有者异常退出：所有权已转给本调用
    if ($acquired) { return $false }
    return $true
  }
  finally {
    if ($acquired) { try { $m.ReleaseMutex() } catch [ApplicationException] { } }
    $m.Dispose()
  }
}

function Get-IsolatedAppProcesses {
  param([Parameter(Mandatory)][string]$InstallDir)
  $root = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
  $hits = @()
  foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
    $path = $null
    try { $path = $p.Path } catch { }            # 无权限/已退出的进程跳过
    if ($path -and [IO.Path]::GetFullPath($path).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
      # 只匹配本演练目录内的可执行文件，不匹配其他安装的同名进程。
      $hits += [pscustomobject]@{ Id = $p.Id; Path = $path }
    }
  }
  return $hits
}

function Start-IsolatedHost {
  param([Parameter(Mandatory)][string]$InstallDir)
  $appHost = Join-Path $InstallDir 'ClipboardManager.App.exe'
  if (Test-Path -LiteralPath $appHost -PathType Leaf) {
    Write-Host "UI host: $appHost"
    return Start-Process -FilePath $appHost -PassThru -WorkingDirectory $InstallDir
  }
  $runtime = Join-Path $InstallDir 'runtime\dotnet.exe'
  if (-not (Test-Path -LiteralPath $runtime -PathType Leaf)) {
    throw "No UI host found: neither ClipboardManager.App.exe nor runtime\dotnet.exe exists in $InstallDir"
  }
  $dll = Join-Path $InstallDir 'ClipboardManager.dll'
  if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Missing ClipboardManager.dll in $InstallDir" }
  Write-Host "UI host: $runtime $dll"
  return Start-Process -FilePath $runtime -ArgumentList @($dll) -PassThru -WorkingDirectory $InstallDir
}

function Wait-AppWindow {
  param([Parameter(Mandatory)][System.Diagnostics.Process]$Process, [int]$TimeoutSeconds = 60)
  $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
  while ([DateTime]::UtcNow -lt $deadline) {
    $Process.Refresh()
    if ($Process.HasExited) { throw "UI host exited before showing a window: $($Process.ExitCode)" }
    if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { return }
    Start-Sleep -Milliseconds 250
  }
  throw "UI host did not show a window within $TimeoutSeconds seconds."
}

function Stop-IsolatedHost {
  param([Parameter(Mandatory)][System.Diagnostics.Process]$Process, [int]$TimeoutSeconds = 30)
  # 只处理本脚本亲自启动的那个进程对象；不按名字查找、不终止其他进程。
  $Process.Refresh()
  if ($Process.HasExited) { return }
  if (-not $Process.CloseMainWindow()) { throw 'UI host did not accept a close request.' }
  if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) { throw "UI host did not exit within $TimeoutSeconds seconds." }
}

function Get-LegacyInputFiles {
  param([Parameter(Mandatory)][string]$DataRoot)
  # 只取迁移前**不可变**的旧版输入：history.json、theme.json、被 history.json 引用的 PNG。
  # 刻意排除 app.log（及轮转文件）：正常启动就会改写它，不能作为完整性判据。
  $files = @()
  $history = Join-Path $DataRoot 'history.json'
  if (Test-Path -LiteralPath $history -PathType Leaf) { $files += $history }
  $theme = Join-Path $DataRoot 'theme.json'
  if (Test-Path -LiteralPath $theme -PathType Leaf) { $files += $theme }
  if (Test-Path -LiteralPath $history -PathType Leaf) {
    $doc = Get-Content -LiteralPath $history -Raw | ConvertFrom-Json
    foreach ($record in @($doc.records)) {
      if ($record -and $record.imageFileName) {
        $files += (Join-Path (Join-Path $DataRoot 'images') $record.imageFileName)
      }
    }
  }
  return @($files | Sort-Object -Unique)
}
```

用**实际 UI 宿主**启动旧版一次并正常关闭，让旧版自行创建数据目录与默认文件。判定依据是**互斥体契约**，不是启动器生命周期：

```powershell
$oldHost = Start-IsolatedHost -InstallDir $installDir
try {
  Wait-AppWindow -Process $oldHost -TimeoutSeconds 60
  if (-not (Test-AppMutexHeld)) { throw 'App mutex is not held while the old UI host is running.' }
  Write-Host "Old UI host ready: pid=$($oldHost.Id) hwnd=$($oldHost.MainWindowHandle)"
}
finally {
  Stop-IsolatedHost -Process $oldHost
}
if (Test-AppMutexHeld) { throw 'App mutex is still held after the old UI host exited.' }
```

旧版关闭后再写入合成数据：

```powershell
New-Item -ItemType Directory -Force -Path (Join-Path $dataRoot 'images') | Out-Null
$imageName = ('{0}.png' -f [guid]::NewGuid().ToString('N'))
# 2x2 不透明 PNG（红/绿/蓝/白，126 字节；固定字节便于哈希与像素双重比对）
$png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAATSURBVBhXY/jPwPAfDBkY/oMBAEnICfeW3k0uAAAAAElFTkSuQmCC')
[IO.File]::WriteAllBytes((Join-Path $dataRoot "images\$imageName"), $png)
$doc = [ordered]@{
  version = 1
  records = @(
    [ordered]@{ id=[guid]::NewGuid(); kind='Text'; text='sample-text-001'; imageFileName=$null; byteLength=15; imageWidth=0; imageHeight=0; contentHash='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; copiedAtUtc='2026-01-01T00:00:00Z'; isPinned=$true },
    [ordered]@{ id=[guid]::NewGuid(); kind='Text'; text='sample-text-002'; imageFileName=$null; byteLength=15; imageWidth=0; imageHeight=0; contentHash='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'; copiedAtUtc='2026-01-01T00:00:01Z'; isPinned=$false },
    [ordered]@{ id=[guid]::NewGuid(); kind='Image'; text=$null; imageFileName=$imageName; byteLength=$png.Length; imageWidth=2; imageHeight=2; contentHash='cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'; copiedAtUtc='2026-01-01T00:00:02Z'; isPinned=$false }
  )
  settings = [ordered]@{ maxRecords=500; maxStorageMb=512; alwaysOnTop=$false; autoCheckUpdates=$true; minimizeToTray=$false; hideAfterCopy=$true }
  pendingImageDeletes = @()
}
[IO.File]::WriteAllText((Join-Path $dataRoot 'history.json'), ($doc | ConvertTo-Json -Depth 8))
[IO.File]::WriteAllText((Join-Path $dataRoot 'theme.json'), '{"theme":"Dark"}')

# 说明：上面的 contentHash 是 64 位十六进制占位值，仅用于满足旧版字段形状与去重校验，
# 不是真实内容的 SHA-256；核对以文本/图片本身为准，不要断言这些占位哈希。

# 迁移前快照：只含不可变旧版输入（history.json / theme.json / 被引用的 PNG）。
$legacyBaseline = @{}
foreach ($file in Get-LegacyInputFiles -DataRoot $dataRoot) {
  if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing legacy input: $file" }
  $legacyBaseline[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
}
if ($legacyBaseline.Count -lt 3) { throw "Expected at least history.json, theme.json and one PNG; got $($legacyBaseline.Count)." }
Write-Host "Legacy baseline: $($legacyBaseline.Count) immutable file(s) (app.log excluded)."
```

> `$legacyBaseline` **不含** `app.log`。旧版与新版的正常启动都会追加/轮转日志，把日志纳入哈希会把正常运行误判成数据损坏。

### 6.4 覆盖安装新版（同目录）

首发路径是**同目录覆盖安装**：新安装器使用与 §6.2 完全相同的 `$installDir`，不另建安装目录。

安装前必须确认演练目录中的旧版**确实已退出**。判定以互斥体与**精确进程路径**为准，不以进程名称为准：

```powershell
$leftover = @(Get-IsolatedAppProcesses -InstallDir $installDir)
if ($leftover.Count -gt 0) {
  throw ('Isolated install still has running processes: ' + (($leftover | ForEach-Object { "$($_.Id)=$($_.Path)" }) -join '; '))
}
if (Test-AppMutexHeld) {
  # 互斥体是会话级对象，无法区分持有者是否来自演练目录；这里只报告，不终止任何进程。
  throw 'Local\ClipboardManager.Desktop.v1 is held by some instance. Close it yourself; this script will not terminate other processes.'
}

$newSetup = Join-Path $workRoot 'ClipboardManager-<new-version>-win-x64-setup.exe'
$newInstall = Start-Process -FilePath $newSetup -Wait -PassThru -ArgumentList @(
  '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
  "/DIR=$installDir", "/LOG=$(Join-Path $logsDir 'install-new.log')")
if ($newInstall.ExitCode -ne 0) { throw "New installer failed: $($newInstall.ExitCode)" }
```

> 说明：`Test-AppMutexHeld` 只会短暂申请并立即释放同名互斥体；脚本**从不**终止进程，也**从不**通过名字杀进程。若互斥体被演练目录之外的实例占用，演练必须停下来由操作者处理。
>
> 该探测只在**合成互斥体**（另起进程持有同名互斥体）上验证过“不窃取、不终止、异常退出不挂起”；**没有**在真实安装的剪贴板应用上验证过，真实应用的互斥体行为必须在一次性 VM/runner 里观察。

判定：

- `$installDir` 中的程序文件已更新为新版（例如 `update-manifest.json` 的 `version` 为新版号），旧版文件没有残留在清单之外；
- `$dataRoot` 未被安装器改写：`$legacyBaseline` 中每个文件仍存在且 SHA-256 一致；
- 注册表项 `DisplayVersion` 与 `InstallLocation` 已更新为 `$installDir` 与新版本。

> 备选（非首发路径）：若需对比“全新目录安装”，另设 `$installFresh = Join-Path $workRoot 'app-fresh'` 并单独记录。该用例**不能**替代首发覆盖安装的判定，且必须使用**独立的** `$dataRoot`，否则两份程序会争用同一用户数据。

### 6.5 启动新版并**保持运行**

启动后**不要**在这里关闭宿主：§6.6 的数据库与数据核对必须在应用运行期间完成，迁移完成标记也依赖正常关闭。

```powershell
$newHost = $null
$startupSucceeded = $false
try {
  $newHost = Start-IsolatedHost -InstallDir $installDir
  Wait-AppWindow -Process $newHost -TimeoutSeconds 120     # 首次迁移可能较慢，按实际日志调整
  if (-not (Test-AppMutexHeld)) { throw 'App mutex is not held while the new UI host is running.' }
  Write-Host "New UI host ready and kept alive: pid=$($newHost.Id) hwnd=$($newHost.MainWindowHandle)"
  $startupSucceeded = $true
}
finally {
  # 若启动后任一步失败（包括窗口超时或互斥体检查失败），正常关闭自己启动的宿主。
  # 成功路径不关闭；宿主保持运行直到 §6.7 明确关闭。
  if (-not $startupSucceeded -and $null -ne $newHost) {
    Stop-IsolatedHost -Process $newHost
    if (Test-AppMutexHeld) { throw 'App mutex is still held after failed startup cleanup.' }
  }
}
```

> 若正常关闭本身失败或超时，本次演练不得继续或记为通过；保留错误并销毁一次性 VM/runner。不要改用 `Stop-Process` 或 `.Kill()`。

### 6.6 核对清单（宿主运行期间执行）

在 `$newHost` 仍运行时执行下列核对。任一项失败都先关闭宿主再中止。

1. **数据库**：`$dataRoot\clipboard.db` 存在；记录数 = 3；`sample-text-001` 文本逐字一致；图片记录 `imageFileName`、宽高一致；收藏数 = 1（`isPinned=true` 映射为收藏）；时间按 UTC 保存且顺序一致。
2. **图片**：`$dataRoot\images\<imageName>` 仍存在，SHA-256 与 `$legacyBaseline` 记录值比对。字节一致是首选期望；若新管线按设计“规范化”PNG 而重新编码，必须在证据中显式记录该决定，并改为核对解码后的像素尺寸与像素内容一致，不得静默接受哈希变化。
3. **主题与设置**：主题 = `Dark`；`maxRecords`=500、`maxStorageMb`=512 等设置值与旧 `history.json` 一致。
4. **旧文件留存与迁移语义**：`history.json` 与 `theme.json` 仍存在，哈希与 `$legacyBaseline` 一致；按 Storage 迁移契约核对备份文件、完成标记的**确切相对路径及已完成状态/内容**。若路径或标记语义尚未由该契约定义，则本次为 `INCONCLUSIVE`，不得猜文件名或记为通过。
5. **数据与程序分离**：安装目录中不存在 `clipboard.db`、`history.json`、`images\`；更新清单不得包含数据文件。
6. **健康标记（如启动契约使用）**：若启动契约要求 `.clipboard-update\ready`，每次启动后均须按该契约断言该精确路径及其 ready 语义；它位于 `$installDir`，不属于 `$dataRoot` 快照，不能因此排除数据目录内的其他标记。
7. **故障注入（可选但建议）**：在数据库创建、导入事务、完成标记各阶段终止进程或制造磁盘错误，重启后旧 JSON/图片仍可恢复且可安全重试。

### 6.7 正常关闭、记录迁移后快照、重启验证幂等

```powershell
function Get-MigratedStateSnapshot {
  param([Parameter(Mandatory)][string]$DataRoot)
  $root = [IO.Path]::GetFullPath($DataRoot)
  # Windows 相对路径统一用 '/' 且按 Windows 大小写规则比较。
  $snapshot = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
  # 唯一排除项：数据根目录当前日志及其单份轮转文件（AppLogger 约定）。不使用 app.log* 通配。
  $excludedMutableLogs = @('app.log', 'app.log.old')
  foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction Stop) {
    $relativePath = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
    if ($excludedMutableLogs -contains $relativePath) { continue }
    $snapshot.Add($relativePath, (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant())
  }
  return ,$snapshot
}

function Assert-MigratedStateSnapshotEqual {
  param($Expected, $Actual)
  # Compare the complete relative-path sets in both directions, then every shared hash.
  $missing = @($Expected.Keys | Where-Object { -not $Actual.ContainsKey($_) })
  $added = @($Actual.Keys | Where-Object { -not $Expected.ContainsKey($_) })
  $changed = @($Expected.Keys | Where-Object {
    $Actual.ContainsKey($_) -and -not [string]::Equals($Expected[$_], $Actual[$_], [StringComparison]::OrdinalIgnoreCase)
  })
  if ($missing.Count -gt 0 -or $changed.Count -gt 0 -or $added.Count -gt 0) {
    throw ('Idempotency mismatch: missing=[' + ($missing -join ', ') + '] changed=[' + ($changed -join ', ') + '] added=[' + ($added -join ', ') + ']')
  }
}

# 1) 首次宿主已在 §6.5–§6.6 正常核对后关闭；先确认互斥体释放。
Stop-IsolatedHost -Process $newHost
if (Test-AppMutexHeld) { throw 'App mutex is still held after the new UI host exited.' }

# 2) 在宿主完全退出后记录迁移后稳定快照；隐藏文件也纳入，只有上面精确列出的两种日志排除。
$migratedBaseline = Get-MigratedStateSnapshot -DataRoot $dataRoot
if (-not $migratedBaseline.ContainsKey('clipboard.db')) { throw 'Migrated snapshot is missing clipboard.db.' }
Write-Host "Migrated baseline: $($migratedBaseline.Count) file(s); excluded only root app.log and app.log.old."

# 3) 重启一次，宿主运行时重做 §6.6 的语义核对（包括记录/设置/旧输入与迁移完成标记）。
$secondHost = $null
try {
  $secondHost = Start-IsolatedHost -InstallDir $installDir
  Wait-AppWindow -Process $secondHost -TimeoutSeconds 120
  if (-not (Test-AppMutexHeld)) { throw 'App mutex is not held during the idempotency run.' }

  # 在此按 §6.6 的 1–6 项重复语义断言；不要在宿主运行时计算快照哈希。
}
finally {
  # 成功或核对失败都正常关闭第二个宿主；不按名称查找或强制终止进程。
  if ($null -ne $secondHost) { Stop-IsolatedHost -Process $secondHost }
}
if (Test-AppMutexHeld) { throw 'App mutex is still held after the idempotency run exited.' }

# 4) 仅在第二个宿主正常退出且互斥体释放后，取新快照并比较完整路径集与所有哈希。
$migratedAgain = Get-MigratedStateSnapshot -DataRoot $dataRoot
Assert-MigratedStateSnapshotEqual -Expected $migratedBaseline -Actual $migratedAgain
Write-Host 'Idempotency OK: complete migrated relative-path set and hashes are unchanged after restart.'
```

> 快照键是 `$dataRoot` 下规范化的相对文件路径，路径分隔符统一为 `/`，按 Windows 大小写不敏感规则比较；比较同时捕获缺失、内容变化和新增文件。只排除根目录的 `app.log` 与 `app.log.old`。备份/迁移标记不会被忽略：它们必须包含在完整快照中并保持哈希稳定；若实现有意在重启时改写某个标记，必须先在协议中列出该**确切相对路径**并单独断言其允许语义，不能使用通配符。安装目录的 `.clipboard-update\ready` 健康标记不在数据快照中，按 §6.6 独立语义核对。

> 说明：`$migratedBaseline`（相对路径键）与 `$legacyBaseline`（绝对路径键）是两份独立快照。前者含数据库/备份/标记/图片，用于证明“卸载不删数据”；后者只含迁移前的不可变输入，用于证明“迁移没有篡改原始 JSON/PNG”。两者都排除日志；迁移后快照仅排除明确列出的两项日志。

### 6.8 收尾与清理（卸载、数据留存、丢弃 VM）

顺序固定：**关闭实际应用 → 卸载 → 核对注册表与快捷方式 → 核对两份快照仍完好 → 丢弃 VM/账户**。

```powershell
# 1) 确认演练目录中的 UI 宿主已退出（脚本亲自关闭过；这里再按精确路径复查一次）。
$leftover = @(Get-IsolatedAppProcesses -InstallDir $installDir)
if ($leftover.Count -gt 0) {
  throw ('Close the application before uninstalling: ' + (($leftover | ForEach-Object { "$($_.Id)=$($_.Path)" }) -join '; '))
}

# 2) 运行安装器产生的卸载程序（用户级安装，静默）。
$uninstaller = Join-Path $installDir 'unins000.exe'
if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw "Missing uninstaller: $uninstaller" }
$uninstall = Start-Process -FilePath $uninstaller -Wait -PassThru -ArgumentList @(
  '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=$(Join-Path $logsDir 'uninstall.log')")
if ($uninstall.ExitCode -ne 0) { throw "Uninstaller failed: $($uninstall.ExitCode)" }
```

随后逐项核对（每项都必须显式通过）：

```powershell
# 3) 卸载注册表项必须消失。
if (Test-Path -LiteralPath $uninstallKey) { throw "Uninstall left the HKCU registration: $uninstallKey" }

# 4) 安装器创建的快捷方式必须消失。
#    本协议在一次性账户的正常已知文件夹下运行，因此这两个路径必须能解析；
#    解析不出来属于 runner 环境问题，必须中止为 INCONCLUSIVE，而不是当作通过。
$shortcutPaths = [ordered]@{
  StartMenu = Join-Path $known.Programs '剪贴板\剪贴板.lnk'
  Desktop   = Join-Path $known.Desktop  '剪贴板.lnk'
}
$unresolvedShortcuts = @($shortcutPaths.GetEnumerator() | Where-Object { [string]::IsNullOrWhiteSpace($_.Value) } | ForEach-Object { $_.Key })
if ($unresolvedShortcuts.Count -gt 0) {
  throw ('INCONCLUSIVE: shortcut paths could not be resolved for: ' + ($unresolvedShortcuts -join ', ') + '. Fix the runner profile; do not record this run as passed.')
}
foreach ($name in $shortcutPaths.Keys) {
  $lnk = $shortcutPaths[$name]
  if (Test-Path -LiteralPath $lnk) { throw "Uninstall left a shortcut: $lnk" }
}
Write-Host 'Uninstall registration and shortcuts are gone.'

# 5) 两份快照都必须原样保留（卸载不得删除或改写用户数据）。
$missing = @()
foreach ($path in $legacyBaseline.Keys) {
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $missing += $path }
}
foreach ($relativePath in $migratedBaseline.Keys) {
  $path = Join-Path $dataRoot ($relativePath.Replace('/', '\'))
  if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $missing += $path }
}
if ($missing.Count -gt 0) { throw ('Uninstall removed user data: ' + (($missing | Sort-Object -Unique) -join '; ')) }
foreach ($path in $legacyBaseline.Keys) {
  $now = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
  if (-not [string]::Equals($now, $legacyBaseline[$path], [StringComparison]::OrdinalIgnoreCase)) {
    throw "User data changed by uninstall: $path"
  }
}
foreach ($relativePath in $migratedBaseline.Keys) {
  $path = Join-Path $dataRoot ($relativePath.Replace('/', '\'))
  $now = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
  if (-not [string]::Equals($now, $migratedBaseline[$relativePath], [StringComparison]::OrdinalIgnoreCase)) {
    throw "User data changed by uninstall: $path"
  }
}
Write-Host ("User data preserved: legacy={0} migrated={1} file(s) with matching SHA256." -f $legacyBaseline.Count, $migratedBaseline.Count)
# 6) 安装目录应已移除（Inno 的 unins000.exe 会自删；若仍在只报告，不递归删除）。
if (Test-Path -LiteralPath $installDir) {
  Write-Warning "Install directory still exists after uninstall: $installDir (inspect before deleting)."
}
```

最后丢弃环境：

- **一次性 VM / CI runner**：核对输出已留存后，直接丢弃整台机器/容器。VM 丢弃**不能**替代上面第 3–5 项——它们必须在丢弃前实际通过。
- 仅清理本会话临时目录 `$workRoot`（安装器日志与程序副本）；**不要**删除 `$dataRoot`，它的留存正是第 5 项的证明。删除 `$workRoot` 前用 `[IO.Path]::GetFullPath($workRoot)` 校验它位于 `$env:TEMP` 下且以 `cm-mig-` 开头。

### 6.9 判定规则

- **通过**：§6.6 的 1–6、§6.7 的幂等核对、§6.8 的注册表/快捷方式/双快照核对全部实际通过，并留存查询结果与哈希值。此时才可讨论保留在地增量更新。
- **INCONCLUSIVE**：已知文件夹无法解析、环境已存在同 AppId 安装、或 Storage 契约尚未定义迁移标记路径/语义等问题。必须修好环境重跑，不得记为通过。
- **未通过或未执行**：按“WPF → WinUI 在地更新安全性未证明”处理。

### 6.10 首发切换策略（硬性约束）

在 §6.6–§6.8 的隔离验证**实际通过并留存证据**之前：

- 首发必须要求用户运行**完整安装器**覆盖安装，不允许旧 `ClipboardManager.Update.exe` 直接以增量方式替换为新架构程序文件。
- 首发说明/发行说明必须明确写出“请下载并运行完整安装器升级”，不得暗示可无缝自动增量升级。
- 安装器只替换安装目录内的程序文件；`%LOCALAPPDATA%\ClipboardManager` 内的历史、图片、主题与日志不得被安装器或更新器改写或删除。
- 即便将来启用增量路径，也必须同时满足：旧清单与新清单 `packaging` 值相同（`UpdateFiles.Plan` 对不一致直接拒绝）、新文件清单包含 `ClipboardManager.exe`/`ClipboardManager.dll`/`ClipboardManager.runtimeconfig.json`/`ClipboardManager.Update.exe`、新启动器能启动 WinUI 进程并写出 `.clipboard-update\ready` 健康标记。
- 上述任一条件不满足时，仍回落到完整安装器覆盖安装。

### 6.11 本机冒烟：环境重定向是否生效（**不是**安装/迁移测试）

本节只回答一个问题：**在一个子进程里重定向五个环境变量后，数据根目录是否真的落在隔离根内。** 它**不**安装、不升级、不卸载、不启动应用，因此**不能**作为 §6.10 的放行证据。

适用：想在改动 §6.1 的目录约定前，先在本机确认重定向语义。

```powershell
$ErrorActionPreference = 'Stop'

$base         = Join-Path $env:TEMP ('cm-redirect-' + [guid]::NewGuid().ToString('N'))
$profileDir   = Join-Path $base 'Profile'
$localAppData = Join-Path $profileDir 'AppData\Local'
$roamingDir   = Join-Path $profileDir 'AppData\Roaming'
$tempDir      = Join-Path $base 'Temp'
$logsDir      = Join-Path $base 'logs'
New-Item -ItemType Directory -Force -Path $localAppData, $roamingDir, $tempDir, $logsDir | Out-Null

$env:USERPROFILE  = $profileDir
$env:LOCALAPPDATA = $localAppData
$env:APPDATA      = $roamingDir
$env:TEMP         = $tempDir
$env:TMP          = $tempDir

$assertPath = Join-Path $base 'assert-redirect.ps1'
@'
$ErrorActionPreference = 'Stop'
$base = $env:CM_EXPECT_BASE
function InRoot([string]$p) {
  if ([string]::IsNullOrWhiteSpace($p)) { return $false }
  return ([IO.Path]::GetFullPath($p)).StartsWith([IO.Path]::GetFullPath($base), [StringComparison]::OrdinalIgnoreCase)
}
$checks = [ordered]@{
  'env:LOCALAPPDATA'                    = $env:LOCALAPPDATA
  'env:APPDATA'                         = $env:APPDATA
  'env:USERPROFILE'                     = $env:USERPROFILE
  'env:TEMP'                            = $env:TEMP
  'env:TMP'                             = $env:TMP
  'GetFolderPath(LocalApplicationData)' = [Environment]::GetFolderPath('LocalApplicationData')
  'GetFolderPath(ApplicationData)'      = [Environment]::GetFolderPath('ApplicationData')
  'GetTempPath()'                       = [IO.Path]::GetTempPath()
  'expected AppPaths.RootDirectory'     = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ClipboardManager'
}
$failed = @()
foreach ($name in $checks.Keys) {
  $ok = InRoot $checks[$name]
  if (-not $ok) { $failed += $name }
  '{0,-38} inRoot={1,-5} {2}' -f $name, $ok, $checks[$name]
}
if ($failed.Count) { throw ('Redirect failed for: ' + ($failed -join ', ')) }
'END-REDIRECT-OK'
'@ | Set-Content -LiteralPath $assertPath -Encoding utf8

$env:CM_EXPECT_BASE = $base
$assertOut = Join-Path $logsDir 'assert-redirect.out.txt'
$assertErr = Join-Path $logsDir 'assert-redirect.err.txt'
$assert = Start-Process -FilePath (Get-Process -Id $PID).Path `
  -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$assertPath) `
  -NoNewWindow -Wait -PassThru -WorkingDirectory $base `
  -RedirectStandardOutput $assertOut -RedirectStandardError $assertErr
Get-Content -LiteralPath $assertOut
if ($assert.ExitCode -ne 0) { Get-Content -LiteralPath $assertErr; throw "Redirect assertion failed: $($assert.ExitCode)" }
if (-not (Select-String -LiteralPath $assertOut -Pattern '^END-REDIRECT-OK$' -Quiet)) { throw 'Redirect assertion did not complete.' }
```

只有输出出现 `END-REDIRECT-OK` 才说明重定向生效。若要在本机继续用这个重定向会话做别的检查，请记住：**它不能证明安装/升级/卸载正确**，那些只能在 §6.1–§6.10 的一次性环境里验证。
