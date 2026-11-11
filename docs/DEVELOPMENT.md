# 开发指南

## 当前正式架构

新方案入口是 `ClipboardManager.sln`，目标为 Windows x64、.NET 10 和 WinUI 3。根目录 WPF 工程与旧页面保留作为迁移参考，不是当前 WinUI Release 的构建/打包入口。

- `src/ClipboardManager.Core`：历史、容量策略、更新清单、更新事务与跨层契约；不引用 UI 框架。
- `src/ClipboardManager.Storage`：SQLite 仓储、schema 迁移、一次性旧 JSON/主题/图片迁移。
- `src/ClipboardManager.Windows`：Win32/WinRT、剪贴板、全局热键、托盘、系统主题及 HWND 适配。
- `src/ClipboardManager.App`：WinUI 单窗口、页面、ViewModel、DI 与应用生命周期。
- `src/ClipboardManager.UpdateWorker`：Windows x64 自包含单文件 helper；只修改安装目录内由程序清单管理的文件。
- `tests/`：Core、Storage、Windows、Update、App 与 Architecture 分层测试。

## 工具链

- 安装公开 .NET SDK `10.0.403`（`global.json` 锁定该 feature band）。
- 使用 Windows 11 x64；安装可构建 WinUI 3 的公开 Visual Studio/Windows App SDK 工具链。
- PowerShell 7 用于构建、打包、安装器与发行脚本。
- 本地开发/打包不需要 PAT、个人 GitHub token、代码签名证书或私钥。GitHub Actions 发布只使用仓库自带的 `GITHUB_TOKEN`。

## 日常命令

```powershell
dotnet restore ClipboardManager.sln
dotnet test ClipboardManager.sln -c Release
dotnet build src/ClipboardManager.App/ClipboardManager.App.csproj -c Release -p:Platform=x64
./build.ps1 -Version 1.0.0
./package.ps1 -Version 1.0.0 -OutputDirectory ./dist
./installer.ps1 -Archive ./dist/ClipboardManager-1.0.0-win-x64.zip -Version 1.0.0
pwsh ./tests/packaging/Test-Package.ps1
```

版本值仅作命令格式示例。脚本会拒绝覆盖已有 ZIP、清单或安装器产物；正式打包前应使用一个新的隔离输出目录。

## 数据与迁移

默认数据根目录为 `%LOCALAPPDATA%\ClipboardManager`。SQLite 数据库为 `history.db`，图片位于 `images`；旧 WPF 版的 `history.json` 和 `theme.json` 只作为一次性迁移输入。SQLite 迁移完成后保留旧源和回退备份。收藏使用 `isFavorite` 语义，容量策略不会自动删除收藏。

安装器卸载不应删除用户数据。更新 helper 不访问数据根目录；更新清单不得包含数据库、图片、主题、日志、迁移标记、用户数据或安装器卸载文件。

## 更新与 Release

更新清单必须使用 `packageKind=winui3`；WPF updater 清单的 `packaging` 字段与 WinUI 格式不兼容。更新下载由用户明确启动；应用检查更新只在设定时机请求固定 GitHub Release 数据，不上传剪贴板内容。应用与 worker 以同版本、`win-x64` 打包；健康标记前保留旧程序文件备份。

WPF→WinUI 安装路径尚未通过一次性账户端到端验证，因此首发要求用户退出 WPF 应用并运行完整安装器覆盖安装，不能宣传旧版本应用内增量升级。版本说明和安装器工作流必须保留此提醒，直至隔离迁移协议真正通过。

ZIP 与安装器未签名。SHA-256 可发现传输损坏/附件错配，不构成独立的发布者签名，也无法防范仓库或 Release 权限被攻破后同时替换清单和文件。

## 合成历史性能基线

性能脚本只创建临时合成数据库，不读取本机剪贴板或现有用户历史：

```powershell
pwsh -NoProfile -File tests/performance/Measure-History.ps1
```

结果覆盖 5,000 条文本/图片历史记录的 SQLite 查询和内存差值；当前机器的具体结果及未测 UI 指标见 `docs/performance/README.md`。这不是滚动帧、缩略图解码或不同 DPI 的验收替代。
