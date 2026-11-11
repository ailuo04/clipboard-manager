# Release checklist

## Before building

- [ ] Select a new `major.minor.patch` version and update `RELEASE_NOTES.md` to describe only implemented changes.
- [ ] Confirm the working tree/commit contains no personal tokens, signing keys, certificates, private package feeds, user paths or real clipboard samples.
- [ ] Confirm the .NET SDK matches `global.json` and the public Windows App SDK build tools are available.
- [ ] Run `dotnet restore ClipboardManager.sln`, `dotnet test ClipboardManager.sln -c Release`, the x64 Release app build, packaging contracts and source secret scan.

## Package and installer

- [ ] `package.ps1` publishes only `src/ClipboardManager.App` and `src/ClipboardManager.UpdateWorker`; the app and worker are self-contained x64 and worker output is single-file.
- [ ] Inspect the ZIP: `release.json` and installed `update-manifest.json` both identify `packageKind=winui3`; manifest program files exist in the ZIP and no user-data, token, signing or test files are included.
- [ ] Verify ZIP SHA-256 sidecar and every compressed update attachment against its manifest entry.
- [ ] Build and run `installer.ps1 -VerifyInstall` only on a disposable GitHub-hosted Windows runner. Confirm app startup, per-user registration/shortcuts, file hashes, reinstall and uninstall behavior, and that synthetic LocalAppData data survives.
- [ ] Do not claim WPF→WinUI migration passed unless the separate one-time-account legacy installation protocol completes with synthetic JSON/PNG data, SQLite/favorite/theme/image assertions, a restart/idempotency check, uninstall and full data hash preservation.
- [ ] If that isolated legacy test is not complete, the release notes must require the full installer and explicitly prohibit the old WPF in-app updater for the first upgrade.

## Publishing and post-release

- [ ] Confirm CI and the source secret scan passed for the exact tag commit.
- [ ] Publish with the repository-provided `GITHUB_TOKEN` only; local release packaging must not ask for or store a personal PAT.
- [ ] Do not overwrite an existing release unless the workflow's explicit refresh approval is selected and the release is still mutable.
- [ ] Verify the Release contains the ZIP and its hash, setup EXE and its hash, a versioned WinUI update manifest, and every content-addressed blob referenced by that manifest.
- [ ] Verify the release notes accurately state the first-upgrade path and unsigned-publisher warning.

## 自动化更新故障覆盖与未完成门禁

以下为本地合成/隔离自动化覆盖；这些测试不请求真实 GitHub、不更新本机应用、不访问真实剪贴板，也不读取用户历史。

| 路径 | 自动化证据 | 当前结果/限制 |
|---|---|---|
| 无更新、自动检查关闭、检查不自动下载、用户取消 | `UpdateCoordinatorTests`、`DelayedUpdateCheckSchedulerTests`、`ReleaseValidationTests` | 已由本地测试覆盖 |
| API 暂时不可用、外部跳转、错误 Release | `ReleaseValidationTests` | 503 网络错误、非可信跳转、draft/prerelease 等由模拟 HTTP 响应覆盖；未模拟真实网络限速服务 |
| 恶意/错配清单、路径穿越、重复属性/路径、大小越界、缺失或超长附件、哈希不符 | `UpdateManifestTests`、`ReleaseValidationTests`、`UpdatePackageServiceTests` | 由本地纯合成数据覆盖，不下载真实附件 |
| 磁盘空间预算不足 | `UpdatePackageServiceTests` | 通过合成超大空间需求走拒绝分支；未耗尽真实磁盘 |
| 更新目录写入失败 | `UpdateWorkerRunnerTests` | 文件系统替身拒绝 journal 写入，断言程序与合成用户数据哈希保持不变 |
| 旧进程未退出 | `UpdateWorkerRunnerTests` | 运行时替身模拟超时，断言旧程序文件及合成用户数据 SHA-256 不变，helper 不启动新版本 |
| 文件替换中断、回滚中断后恢复、未管理文件保留 | `TransactionRecoveryTests` | 文件系统故障注入覆盖；关键恢复用例比较安装文件哈希并检查合成数据不变 |
| 健康标记超时、旧版本重启、成功确认后清理备份 | `UpdateWorkerRunnerTests`、`TransactionRecoveryTests` | 由合成进程运行时与安装目录覆盖 |
| 安装版启动、覆盖安装、卸载及数据保留 | `.github/workflows/release.yml` 中的 `installer.ps1 -VerifyInstall` | 仅在后续 GitHub-hosted disposable runner 执行；本地未运行，真实 WPF 首发迁移也未验证 |
| 便携版首次启动、合成 JSON/PNG 迁移、worker 合成更新、程序文件移除后数据保留 | `tests/packaging/Test-PortablePackage.ps1`（只允许 GitHub Actions disposable runner） | 已接入 Release 与 installer workflows；本地未启动 App/worker，需等 hosted job 实际运行；不会访问真实 Release，更新使用本地合成清单 |
| 每条故障路径持久化程序及合成用户数据哈希报告 | 当前 xUnit 断言 | 核心回滚用例会比较哈希；尚无涵盖全矩阵的独立持久化报告工件 |

便携集成脚本只使用 Release 工作流已提供的 ZIP 输入，并在测试步骤本身使用本地合成清单、关闭自动检查，且不会调用 GitHub Release API。脚本拒绝非 github-hosted runner。请注意：触发整个 Release/installer workflow 时，前置/后置发布步骤会按定义联网读取或写入真实 Release；本次本地开发没有触发这些 workflow。

> **发布门禁：** 上表的本地模拟测试不是完整更新故障矩阵的替代。GitHub Actions、Inno Setup、便携包 hosted 启动/合成更新、WPF→WinUI 一次性账户迁移和实时 UI/无障碍验收均未在本任务中执行。未获得 disposable Windows runner 证据前，不切换/发布正式 Release，也不宣称迁移或两种包形态的端到端矩阵已通过。



