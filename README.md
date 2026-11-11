# 剪贴板管理器

Windows 11 x64 本地剪贴板历史工具，使用 WinUI 3 单窗口界面。文本、图片、收藏和设置保存在本机，不上传剪贴板内容。

## 获取与升级

正式发行版提供每用户安装器和便携 ZIP：

- **安装版**：下载并运行 `ClipboardManager-<版本>-win-x64-setup.exe`。默认安装到 `%LOCALAPPDATA%\Programs\ClipboardManager`，不需要管理员权限。
- **便携版**：完整解压 ZIP，在解压目录运行 `ClipboardManager.App.exe`。不要只复制 EXE。
- 两种形式使用相同的 `%LOCALAPPDATA%\ClipboardManager` 用户数据，不能同时运行；卸载不会删除历史数据。
- 从旧 WPF 版首次升级时，先退出旧程序并运行完整安装器覆盖安装。旧版应用内增量更新器不能安装 WinUI 包；新版本启动后会把合成兼容范围内的旧 JSON、主题和图片导入 SQLite，并保留原始文件作回退备份。此迁移尚需在一次性 Windows 环境完成真实安装验证。

## 功能

- 历史与收藏页面、文本/图片记录预览、全文搜索、复制、删除。
- 设置主题、历史容量、置顶、托盘、复制后隐藏和自动检查更新。
- 更新中心明确区分自动检查、手动检查和“下载并更新”；自动检查只在启动 20 秒后首次执行，之后每 60 分钟检查一次，只提醒、不下载。
- 下载后逐文件校验大小和 SHA-256；独立自包含 worker 执行程序文件事务替换，启动健康确认失败时回滚。用户历史、图片、数据库、设置和迁移备份不属于 worker 的目标范围。

## 开发

需要 Windows 11 x64、PowerShell 7、.NET SDK `10.0.403` 和可构建 WinUI 3 的公开 Visual Studio / Windows App SDK 工具链。无需个人私钥、证书或 PAT。

```powershell
dotnet restore ClipboardManager.sln
dotnet test ClipboardManager.sln -c Release
dotnet build src/ClipboardManager.App/ClipboardManager.App.csproj -c Release -p:Platform=x64
./build.ps1 -Version 1.0.0
./package.ps1 -Version 1.0.0 -OutputDirectory ./dist
./installer.ps1 -Archive ./dist/ClipboardManager-1.0.0-win-x64.zip -Version 1.0.0
```

版本号示例仅演示命令格式；再次打包相同版本会被拒绝。`package.ps1` 只从 `ClipboardManager.sln` 的 WinUI App 与 UpdateWorker 工程发布，不读取旧 WPF 工程作为发行入口。

## 安全与发行

发行包未做代码签名，Windows 可能提示未知发布者。请只使用你信任的仓库发行版，并核对随包 SHA-256：

```powershell
Get-FileHash .\ClipboardManager-<版本>-win-x64.zip -Algorithm SHA256
```

HTTPS 和 SHA-256 可发现下载损坏或文件不匹配，但不能抵御仓库/Release 权限被攻破后清单与文件同时被替换。剪贴板内容不会写入更新请求或诊断日志。公开 GitHub Actions 以仓库提供的短期 `GITHUB_TOKEN` 发布；本地打包无需凭证。旧 WPF 源码保留在仓库作为迁移参考，不参与新 Release 构建。
