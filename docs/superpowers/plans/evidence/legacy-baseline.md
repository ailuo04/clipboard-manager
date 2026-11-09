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

应用单实例互斥体的补充说明：任务简报指定的四个核对来源（`AppPaths.cs`、`installer.ps1`、`UpdateFiles.cs`、release workflow）中只包含**更新**互斥体；`Local\ClipboardManager.Desktop.v1` 实际出现在 `App.xaml.cs`（`new Mutex(true, "Local\\ClipboardManager.Desktop.v1", out bool created)`）与 `installer/ClipboardManager.iss` 的 `AppMutex` 声明中，此处按实际文件标注来源，不臆测为四个来源之一。

旧应用入口链（依据 `build-update-tools.ps1`、`UpdateHelper/Launcher.cs`、`UpdateHelper/Program.cs`、`ClipboardManager.csproj`）：

1. 用户启动 `<安装目录>\ClipboardManager.exe`。该文件不是 .NET 应用主机，而是由 .NET Framework `csc.exe` 编译的启动器（`UpdateHelper/Launcher.cs`）。
2. 启动器先检查 `.clipboard-update\journal.json`：存在则调用 `worker.exe --recover` 恢复未完成更新；存在 `complete` 则清理工作区。
3. 启动器优先启动 `<安装目录>\ClipboardManager.App.exe`（自包含发布时，原应用主机被改名为该文件）；否则回退到 `runtime\dotnet.exe ClipboardManager.dll`。
4. 真正的 WPF 应用为 `ClipboardManager.dll`（`net8.0-windows`、`UseWPF`）；`ClipboardManager.Update.exe` 是独立更新 worker，支持 `--apply`、`--recover`、`--uninstall-managed`。
5. 启动器支持 `--verify-update`：等待新版写出 `.clipboard-update\ready` 健康标记，失败即返回非零。

以上入口链是“旧 helper 直接增量替换新架构”风险评估的对象：启动器按固定文件名寻找并等待子进程，新 WinUI 应用必须满足同一文件名/健康标记契约，否则不能走增量路径。

## 6. 隔离升级验证协议（未来执行，当前尚未执行）

**状态：未执行。** 本节是可重演的协议与判定标准，不是结果记录。任何“升级已通过”的结论都必须来自实际执行并保存输出；在此之前一律按“未证明”处理。

隔离要求：

- 首选一次性 Windows VM 或一次性 CI runner；不得在开发者日常桌面或真实用户数据目录上执行。
- 若必须本机演练，则为子进程显式覆盖 `LOCALAPPDATA`、`APPDATA`、`USERPROFILE`、`TEMP` 指向一次性根目录，并在结束后整体删除该根目录。安装器与应用的“用户数据保留”检查只针对该隔离根目录。
- 环境变量无法隔离的副作用必须提前确认并接受：安装器会写 `HKCU` 卸载注册表项（`{8BCDA40E-D12B-4A21-BBD5-EF1653BD6F7B}_is1`）与开始菜单/桌面快捷方式。演练前确认本机没有同 AppId 的正式安装，结束后核对注册表项与快捷方式已被卸载移除；否则优先使用一次性 VM/CI runner。
- 上面的 `$env:` 覆盖只作用于当前 PowerShell 会话及其子进程；演练结束应关闭该会话（或在 VM 中整体丢弃），避免影响后续正常的开发与测试。
- 全程只用**合成数据**：虚构文本（例如 `sample-text-001`）、程序生成的小 PNG、合成设置值；不得复制任何真实剪贴板内容。

### 6.1 准备隔离目录

```powershell
$base = Join-Path $env:TEMP ('cm-mig-' + [guid]::NewGuid().ToString('N'))
$installOld = Join-Path $base 'old-app'
$installNew = Join-Path $base 'new-app'
$isolatedLocalAppData = Join-Path $base 'LocalAppData'
$dataRoot = Join-Path $isolatedLocalAppData 'ClipboardManager'
New-Item -ItemType Directory -Force -Path $installOld, $installNew, (Join-Path $dataRoot 'images') | Out-Null
```

### 6.2 安装旧版样本

```powershell
# 旧版安装器与校验文件来自已发布资产（示例版本号，按实际替换）
$oldSetup = Join-Path $base 'ClipboardManager-0.0.7-win-x64-setup.exe'
$oldSetupHash = Join-Path $base 'ClipboardManager-0.0.7-win-x64-setup.sha256'
(Get-Content -LiteralPath $oldSetupHash -Raw).Trim().Split(' ')[0] -eq (Get-FileHash -LiteralPath $oldSetup -Algorithm SHA256).Hash.ToLowerInvariant()

$env:LOCALAPPDATA = $isolatedLocalAppData
$env:APPDATA = Join-Path $base 'AppData\Roaming'
Start-Process -FilePath $oldSetup -Wait -ArgumentList @(
  '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
  "/DIR=$installOld","/LOG=$base\install-old.log")
```

判定：`<安装目录>\ClipboardManager.exe`、`ClipboardManager.dll`、`ClipboardManager.runtimeconfig.json`、`ClipboardManager.Update.exe`、`update-manifest.json` 均存在；注册表项指向 `$installOld`。

### 6.3 写入合成历史、图片与主题

```powershell
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
# 记录基线哈希（后续逐项比对）
$baseline = @{}
Get-ChildItem -LiteralPath $dataRoot -Recurse -File | ForEach-Object {
  $baseline[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
```

### 6.4 安装新版并启动

```powershell
$newSetup = Join-Path $base 'ClipboardManager-<new-version>-win-x64-setup.exe'
Start-Process -FilePath $newSetup -Wait -ArgumentList @(
  '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
  "/DIR=$installNew","/LOG=$base\install-new.log")
```

覆盖安装路径（首发首选）应使用**同一个** `/DIR` 覆盖旧目录，并在安装前确认旧应用已退出（应用互斥体 `Local\ClipboardManager.Desktop.v1` 未被占用）。

### 6.5 核对清单（必须全部满足）

1. **数据库**：`<数据根目录>\clipboard.db` 存在；记录数 = 3；`sample-text-001` 文本逐字一致；图片记录 `imageFileName`、宽高一致；收藏数 = 1（`isPinned=true` 映射为收藏）；时间按 UTC 保存且顺序一致。
2. **图片**：`<数据根目录>\images\<imageName>` 仍存在，SHA-256 与 `$baseline` 记录值比对。字节一致是首选期望；若新管线按设计“规范化”PNG 而重新编码，必须在证据中显式记录该决定，并改为核对解码后的像素尺寸与像素内容一致，不得静默接受哈希变化。
3. **主题与设置**：主题 = `Dark`；`maxRecords`=500、`maxStorageMb`=512 等设置值与旧 `history.json` 一致。
4. **旧文件留存**：`history.json` 与 `theme.json` 仍存在，哈希与 `$baseline` 一致；存在迁移备份/完成标记。
5. **重启幂等**：关闭并再次启动新版，记录数、收藏数、图片哈希（或按第 2 项记录的等价判据）不变，不重复导入。
6. **数据与程序分离**：安装目录中不存在 `clipboard.db`、`history.json`、`images\`；更新清单不得包含数据文件。
7. **故障注入（可选但建议）**：在数据库创建、导入事务、完成标记各阶段终止进程或制造磁盘错误，重启后旧 JSON/图片仍可恢复且可安全重试。

### 6.6 判定规则

- **通过**：上述 1–6 全部通过，并在实际输出中留存查询结果与哈希值。此时才可讨论保留在地增量更新。
- **未通过或未执行**：按“WPF → WinUI 在地更新安全性未证明”处理。

### 6.7 首发切换策略（硬性约束）

在 6.5 的隔离验证**实际通过并留存证据**之前：

- 首发必须要求用户运行**完整安装器**覆盖安装，不允许旧 `ClipboardManager.Update.exe` 直接以增量方式替换为新架构程序文件。
- 首发说明/发行说明必须明确写出“请下载并运行完整安装器升级”，不得暗示可无缝自动增量升级。
- 安装器只替换安装目录内的程序文件；`%LOCALAPPDATA%\ClipboardManager` 内的历史、图片、主题与日志不得被安装器或更新器改写或删除。
- 即便将来启用增量路径，也必须同时满足：旧清单与新清单 `packaging` 值相同（`UpdateFiles.Plan` 对不一致直接拒绝）、新文件清单包含 `ClipboardManager.exe`/`ClipboardManager.dll`/`ClipboardManager.runtimeconfig.json`/`ClipboardManager.Update.exe`、新启动器能启动 WinUI 进程并写出 `.clipboard-update\ready` 健康标记。
- 上述任一条件不满足时，仍回落到完整安装器覆盖安装。

## 7. 证据卫生与校验

- 运行 `git diff --check` 检查空白/行尾问题（结果见 Task 1 报告）。
- 本文与相关改动只包含：环境变量形式路径、合成示例文本、固定测试哈希占位值、公开文件名与版本号。
- 未包含：真实剪贴板内容、真实用户数据、开发者本机绝对路径、令牌、证书、私钥或任何个人标识信息。
