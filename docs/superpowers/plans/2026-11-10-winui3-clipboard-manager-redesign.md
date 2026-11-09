# WinUI 3 剪贴板管理器重构实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将现有 WPF/.NET 8 剪贴板应用迁移为 WinUI 3/.NET 10 单窗口工作台，安全迁移本地历史，并交付可回滚的更新与无需个人秘密的双格式发行流程。

**Architecture:** 建立 Core、Storage、Windows、App、UpdateWorker 五个运行时边界及对应测试工程。Core 持有领域规则与稳定契约；SQLite 与 JSON 导入留在 Storage；Win32/WinRT 集成留在 Windows；WinUI 只负责呈现和命令；更新 worker 只操作清单所管理的程序文件。保留原 WPF 工程作为迁移前基线，直到新版本所有门禁通过，再把新工程设为唯一正式构建/发布入口。

**Tech Stack:** C# / .NET 10 (`global.json` 固定 SDK 10.0.403)、WinUI 3 与 Windows App SDK 2.6.5、`Microsoft.Data.Sqlite`、`CommunityToolkit.Mvvm`、`Microsoft.Extensions.DependencyInjection`、xUnit、PowerShell 7、GitHub Actions、Inno Setup（只在发行 Windows runner 上使用）。App SDK 使用 unpackaged/self-contained 配置，具体由第一阶段 clean-build 验证锁定。

**Spec:** `docs/superpowers/specs/2026-11-10-winui3-clipboard-manager-redesign-design.md`

## Global Constraints

- 目标框架为 .NET 10，Windows x64；发布应用自包含，不要求终端用户另装 .NET Runtime。
- UI 使用 WinUI 3/Fluent；不得把 WPF `Window`、`BitmapSource` 或 Dispatcher 类型带入 Core、Storage 或 UpdateWorker。
- 数据根目录保持 `%LOCALAPPDATA%\ClipboardManager`；更新 worker 不得读写该目录内的历史、数据库、图片、主题或日志。
- SQLite 是迁移完成后的唯一运行时数据权威；旧 JSON 只导入一次，原文件和备份不得被迁移自动删除。
- 收藏记录免于自动容量清理；明确的单条删除和清空仍删除收藏，并在确认 UI 中提示。
- 自动更新只检查，不静默下载；下载由用户明确启动。验证通过后更新并自动重启，替换失败必须恢复旧程序文件。
- 构建、测试、打包和发布不得依赖个人密钥、代码签名证书、PAT 或开发者机器私有文件；只允许仓库标准最小权限令牌执行发布。
- 剪贴板内容不上传，也不得写入诊断日志、更新请求或崩溃报告。
- 发行版同时生成用户级安装器与便携 ZIP；卸载保留用户数据；WPF → WinUI 若不能通过隔离升级验证，首发明确要求一次性完整安装器升级。
- 所有阶段先写失败测试，再以最小实现通过；每个任务结束运行其列出的验证命令，任务内不提交与其无关的文件。

---

## 文件与项目地图

### 新解决方案

- `ClipboardManager.sln`：汇总所有新工程；旧根目录 WPF `.csproj` 保持原样，只用于 legacy 基线与首发前向兼容测试。
- `global.json`：固定 .NET SDK `10.0.403`，`rollForward: latestPatch`。
- `Directory.Build.props`：仅对 `src/` 和新 `tests/` 工程统一 nullable、implicit usings 与确定性构建；条件限定，根目录 WPF 与旧 `Tests/` 保持现有构建属性。
- `Directory.Packages.props`：集中固定全部 NuGet 版本；不得使用浮动版本。
- `src/ClipboardManager.Core/`：领域类型、服务契约、收藏/容量/搜索规则、更新状态与版本决策；不引用 Windows UI/平台程序集。
- `src/ClipboardManager.Storage/`：SQLite schema、仓储、FTS5/trigram 搜索、JSON/主题/图片引用一次性迁移、备份与重试标记。
- `src/ClipboardManager.Windows/`：剪贴板 Win32/WinRT 读写、隐藏消息窗口、STA、图像管线、全局快捷键、托盘、系统主题、窗口 HWND 适配。
- `src/ClipboardManager.App/`：WinUI App、DI、单窗口导航、ViewModels、Fluent 资源、历史/收藏/更新/设置页面。
- `src/ClipboardManager.UpdateWorker/`：独立自包含 helper，验证操作清单、同盘事务替换、健康等待、恢复和安全清理；不引用 WinUI。
- `tests/ClipboardManager.Core.Tests/`、`Storage.Tests/`、`Windows.Tests/`、`Update.Tests/`、`App.Tests/`：分层测试；平台集成测试仅在 Windows CI 运行，Core/Storage/更新纯逻辑测试可在 Windows runner 全跑。
- `Directory.Build.targets`：仅为 `src/` 与新 `tests/` 工程隔离测试/发布输出，避免旧项目 `bin/obj` 混入新发布资产；不改变 legacy 默认输出路径。

### 核心数据与契约（签名在 Core 定义）

```csharp
public enum ClipboardContentKind { Text = 1, Image = 2 }
public enum HistoryKindFilter { All, Text, Image }

public sealed record ClipboardRecord(
    Guid Id, ClipboardContentKind Kind, string? Text, string? ImageFileName,
    long ByteLength, int ImageWidth, int ImageHeight, string ContentHash,
    DateTime CopiedAtUtc, bool IsFavorite);

public sealed record HistoryQuery(
    string SearchText, HistoryKindFilter Kind, bool FavoritesOnly, int Offset, int Limit);

public sealed record HistoryPage(
    IReadOnlyList<ClipboardRecord> Records, int TotalCount, int Offset, int Limit);

public sealed record HistoryMutation(
    ClipboardRecord? Upsert, IReadOnlyList<Guid> DeleteIds,
    IReadOnlyList<string> PendingImageDeletes, IReadOnlyList<string> CompletedImageDeletes, AppSettings? Settings);
public sealed record HistoryChangeResult(IReadOnlyList<ClipboardRecord> Upserted, IReadOnlyList<Guid> DeletedIds, string? Warning);
public sealed record AppSettings(int MaxRecords, int MaxStorageMb, bool AlwaysOnTop, bool AutoCheckUpdates, bool MinimizeToTray, bool HideAfterCopy, string ThemePreference);
public sealed record RetentionResult(IReadOnlyList<Guid> DeleteIds, string? CapacityWarning);

public interface IHistoryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken);
    Task<ClipboardRecord?> FindDuplicateAsync(ClipboardContentKind kind, string contentHash, CancellationToken cancellationToken);
    Task ApplyAsync(HistoryMutation mutation, CancellationToken cancellationToken);
    Task<bool> HasImportedMigrationAsync(string migrationId, CancellationToken cancellationToken);
    Task ImportLegacyAsync(string migrationId, IReadOnlyList<ClipboardRecord> records, AppSettings settings, IReadOnlyList<string> pendingImageDeletes, CancellationToken cancellationToken);
    Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> LoadPendingImageDeletesAsync(CancellationToken cancellationToken);
}

public interface IHistoryService
{
    Task<HistoryPage> SearchAsync(HistoryQuery query, CancellationToken cancellationToken);
    Task<HistoryChangeResult> AddAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken);
    Task<HistoryChangeResult> DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<HistoryChangeResult> ClearAsync(CancellationToken cancellationToken);
    Task<HistoryChangeResult> SetFavoriteAsync(Guid id, bool isFavorite, CancellationToken cancellationToken);
    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task<HistoryChangeResult> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken);
}

public sealed record ClipboardSnapshot(ClipboardContentKind Kind, string? Text, ReadOnlyMemory<byte> PngBytes, DateTime CapturedAtUtc);
public sealed record ClipboardReadResult(uint SequenceNumber, ClipboardSnapshot? Snapshot, string? Warning);

public interface IClipboardReader
{
    Task<ClipboardReadResult> ReadAsync(CancellationToken cancellationToken);
}
public interface IClipboardWriter
{
    Task WriteTextAsync(string text, CancellationToken cancellationToken);
    Task WriteImageAsync(string imagePath, CancellationToken cancellationToken);
}
public interface IClipboardMonitor : IDisposable
{
    event EventHandler? ClipboardChanged;
    void Start();
}

public static class DatabaseSchemaVersion { public const int Current = 1; }
public sealed record DatabaseSchema(int Version);
public interface IDatabaseMigration
{
    int FromVersion { get; }
    int ToVersion { get; }
    Task ApplyAsync(System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken);
}
public interface IDatabaseSchemaMigrator
{
    Task MigrateAsync(CancellationToken cancellationToken);
}
```

`HistoryMutation` 必须在一个 SQLite transaction 内提交记录、设置、待删图片名称、完成清理的队列移除；`migration_history` 用唯一 migrationId 保证 legacy import commit 与导入数据原子提交；coordinator 以 `HasImportedMigrationAsync` 处理 DB commit 后、文件标记前崩溃。容量策略在 Core 计算删除 ID，Storage 在同一事务提交；图片写入先落盘，元数据事务失败则删掉新图片；数据库提交后图片清理由持久待删队列重试。分页和搜索永远在 SQLite 执行，不从 UI 内存集合做全量字符串扫描。

### 测试门禁和常用命令

- 纯领域/存储：`dotnet test tests/ClipboardManager.Core.Tests/ClipboardManager.Core.Tests.csproj -c Release`、`dotnet test tests/ClipboardManager.Storage.Tests/ClipboardManager.Storage.Tests.csproj -c Release`。
- 新方案总门禁：`dotnet test ClipboardManager.sln -c Release --no-restore`；Windows App SDK 首次 restore 后 CI 可以省略 `--no-restore`。
- 新应用构建：`dotnet build src/ClipboardManager.App/ClipboardManager.App.csproj -c Release -p:Platform=x64`。
- 包：`pwsh ./package.ps1 -Version <semver> -OutputDirectory <隔离目录>`；安装器：`pwsh ./installer.ps1 -Archive <已生成 ZIP> -Version <semver> -VerifyInstall`。
- 禁止在没有执行对应命令并检查退出码/测试数前声称成功。

---

## Task 1：锁定 WPF 基线和首发切换策略

**Files:**
- Create: `docs/superpowers/plans/evidence/legacy-baseline.md`
- Modify: `docs/DEVELOPMENT.md`
- Test: `Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj`（只运行，不改现有测试）

- [ ] 在当前干净工作树运行 `dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj -c Release --nologo`，记录实际总数、通过数和耗时到 `legacy-baseline.md`。
- [ ] 在同文件记录当前 WPF 数据布局、发布资产命名、安装默认路径、互斥体名及旧应用入口。数据布局与发布资产仅据 `AppPaths.cs`、`installer.ps1`、`UpdateFiles.cs` 与 release workflow 核实；互斥体与旧应用入口链另据 `App.xaml.cs`、`installer/ClipboardManager.iss`、`UpdateHelper/Launcher.cs`、`UpdateHelper/Program.cs`、`build-update-tools.ps1` 核实（这些补充来源仅用于互斥体/入口链，不用于推导数据布局或发布资产）。
- [ ] 写隔离升级验证协议：旧版样本安装到临时目录、复制合成 JSON/PNG 到独立 LocalAppData、安装新包、核对 DB/图片哈希、旧 JSON 留存；若完整流程无法证明可靠则首发文案要求安装版覆盖安装，不允许旧 helper 直接增量替换新架构。
- [ ] 运行 `git diff --check` 并检查证据不包含真实剪贴板数据、用户路径以外的敏感信息或私钥。

**验收：** 记录中的测试数来自真实命令输出；旧文件清单和首发迁移流程有可重演的命令/检查项。

## Task 2：建立 .NET 10 solution 与 clean-clone 构建骨架

**Files:**
- Create: `global.json`、`Directory.Build.props`、`Directory.Build.targets`、`Directory.Packages.props`、`ClipboardManager.sln`
- Create: `src/ClipboardManager.Core/ClipboardManager.Core.csproj`
- Create: `src/ClipboardManager.Storage/ClipboardManager.Storage.csproj`
- Create: `src/ClipboardManager.Windows/ClipboardManager.Windows.csproj`
- Create: `src/ClipboardManager.App/ClipboardManager.App.csproj`、`src/ClipboardManager.App/App.xaml`、`src/ClipboardManager.App/App.xaml.cs`
- Create: `src/ClipboardManager.UpdateWorker/ClipboardManager.UpdateWorker.csproj`、`src/ClipboardManager.UpdateWorker/Program.cs`
- Create: 各层测试工程（含 `tests/ClipboardManager.Architecture.Tests/`）与最小 smoke test

- [ ] 先建立独立 `tests/ClipboardManager.Architecture.Tests/ClipboardManager.Architecture.Tests.csproj` 与架构测试；断言 Core TFM 为 `net10.0`，Core/Storage 不含 `UseWPF`、`UseWindowsForms`、`PresentationFramework` 或 `Windows.UI.Xaml` 引用；App 与 Windows Adapter 分别允许 WinUI/Windows SDK。
- [ ] 运行该测试确认新工程缺失时失败。
- [ ] 固定 SDK 与 Windows App SDK 版本；App 设置 WinUI、unpackaged、自包含属性，发布目标 `win-x64`；将 Windows App SDK 运行库自包含属性放进一个可由 CI 覆盖但默认启用的属性组。
- [ ] 加入集中包版本：`Microsoft.WindowsAppSDK` 2.6.5、`Microsoft.Data.Sqlite` 10.0.14、`CommunityToolkit.Mvvm` 8.4.2、`Microsoft.Extensions.DependencyInjection` 10.0.14；测试包固定为 `Microsoft.NET.Test.Sdk` 17.14.1、`xunit` 2.9.3、`xunit.runner.visualstudio` 3.1.4。所有工程以 `PackageReference` 不写版本，版本只在 `Directory.Packages.props` 出现。
- [ ] 新增每个工程的最小启动类型与 smoke tests；Core contracts 由 Task 3 定义，不在此 task 复制声明；不触碰旧根 WPF project 的默认编译规则。
- [ ] 运行 `dotnet restore ClipboardManager.sln`、`dotnet test ClipboardManager.sln -c Release`、`dotnet build src/ClipboardManager.App/ClipboardManager.App.csproj -c Release -p:Platform=x64`，在 Windows x64 runner 上确认 unpackaged WinUI 可启动。
- [ ] 新建 `docs/DEVELOPMENT.md` 的新方案章节：安装公开 .NET 10 SDK、公开 VS C++/WinUI workload、restore/build/test 命令；不得引用开发者本机路径。

**验收：** 新方案从仅含跟踪文件的 clean clone 可 restore/build/test；旧 WPF 基线仍可单独 build/test。

## Task 3：Core 领域类型与收藏/容量策略

**Files:**
- Create: `src/ClipboardManager.Core/History/ClipboardRecord.cs`
- Create: `src/ClipboardManager.Core/History/HistoryQuery.cs`
- Create: `src/ClipboardManager.Core/History/HistoryMutation.cs`
- Create: `src/ClipboardManager.Core/History/IHistoryRepository.cs`、`IHistoryService.cs`
- Create: `src/ClipboardManager.Core/Clipboard/ClipboardSnapshot.cs`、`ClipboardReadResult.cs`、`IClipboardReader.cs`、`IClipboardWriter.cs`、`IClipboardMonitor.cs`
- Create: `src/ClipboardManager.Core/History/HistoryChangeResult.cs`
- Create: `src/ClipboardManager.Core/Settings/AppSettings.cs`
- Create: `src/ClipboardManager.Core/History/RetentionPolicy.cs`
- Create: `src/ClipboardManager.Core/Storage/DatabaseSchema.cs`、`IDatabaseMigration.cs`、`IDatabaseSchemaMigrator.cs`
- Create: `tests/ClipboardManager.Core.Tests/RetentionPolicyTests.cs`

- [ ] 先添加失败测试：在容量限制内不删记录；超条数时删最早非收藏；超字节限制时连续删最早非收藏直到满足；收藏从不自动删；仅有收藏超限时保留并返回 warning；设置范围仍为 50–5000 条和 64–4096 MB。
- [ ] 运行 `dotnet test tests/ClipboardManager.Core.Tests/ClipboardManager.Core.Tests.csproj -c Release --filter FullyQualifiedName~RetentionPolicy` 确认策略类缺失导致失败。
- [ ] 实现上述 immutable 记录与 `RetentionPolicy.Trim(IReadOnlyCollection<ClipboardRecord>, AppSettings)`，返回按 ID 删除集合和 `CapacityWarning`，所有日期必须 `DateTimeKind.Utc`。
- [ ] 将 `IsPinned` 仅保留在 JSON 迁移 DTO；Core 的业务/UI 模型只暴露 `IsFavorite`。同任务创建 `IHistoryRepository`、`IHistoryService`、clipboard contracts 与不依赖 SQLite provider 的 schema version/migration contracts（`DatabaseSchema.CurrentVersion`、`IDatabaseMigration`、`IDatabaseSchemaMigrator`）；这些契约由后续 Storage、Windows、App 工程消费。
- [ ] 运行 Core tests 并检查边界：恰好达到上限、0 字节、重复时间、同一毫秒排序、全收藏超限。重复内容更新与收藏保留由 Task 6 的 service tests 覆盖。

**验收：** 收藏和容量规则无任何 UI 或 SQLite 依赖，测试覆盖所有规则和边界。

## Task 4：SQLite schema、事务仓储与搜索契约

**Files:**
- Create: `src/ClipboardManager.Storage/Sqlite/SqliteHistoryRepository.cs`
- Create: `src/ClipboardManager.Storage/Sqlite/SqliteSchemaMigrationRunner.cs`
- Create: `src/ClipboardManager.Storage/Sqlite/SqliteConnectionFactory.cs`
- Create: `src/ClipboardManager.Storage/Sqlite/SqliteStorageException.cs`
- Create: `tests/ClipboardManager.Storage.Tests/SqliteHistoryRepositoryTests.cs`
- Create: `tests/ClipboardManager.Storage.Tests/SearchCompatibilityTests.cs`

- [ ] 先用架构测试建立项目引用后写临时目录测试，要求初始化后存在 `PRAGMA user_version` 等于 `DatabaseSchemaVersion.Current`、`records`、`settings`、`pending_image_deletes`、`migration_history` 表，重复初始化无副作用，未知 schema 版本安全失败而不删库。
- [ ] 添加事务失败注入测试：用临时 DB 中仅测试启用的 abort trigger 在 mutation 的中间写入步骤触发约束失败；断言记录、设置、待删队列都保持提交前状态。
- [ ] 实现 Task 3 定义的 `IDatabaseSchemaMigrator`；Core 仅持有当前 schema version 与 migration contract，SQLite DDL、PRAGMA 和 provider 操作全部在 Storage。
- [ ] 为 `records` 建 `id` 主键、`content_hash`、`copied_at_ticks`、`kind`、`is_favorite` 索引；用参数化 SQL 禁止拼接用户搜索文字；设置外键与 SQLite `busy_timeout`。
- [ ] 实现 `SearchAsync` 分页、排序（UTC tick 降序再以 ID 稳定排序）、类型筛选、收藏筛选与总数；初始化时探测 FTS5 trigram。只有差异测试证明对该查询字符类不会漏掉 `StringComparison.OrdinalIgnoreCase` 匹配时才用 trigram 缩小候选集；每个候选最终由 .NET `StringComparison.OrdinalIgnoreCase` 精确复核。短于 3 字符、无法证明索引无漏项的 Unicode/query 类和 FTS 初始化失败时执行完整扫描回退；禁止依赖 SQLite ASCII-only `lower()`/`NOCASE` 作为语义判定。
- [ ] 写中英文大小写、中文 1/2/3 字符、emoji、引号、百分号、下划线、换行、空格和长文本差异测试；将当前 `HistoryService` 的大小写不敏感 `Contains` 作为期望结果。
- [ ] 运行 Storage tests；重复运行测试以确认临时 DB、SQLite 连接和文件句柄释放。

**验收：** 一个 mutation 是单 transaction；搜索不注入 SQL，查询结果和旧包含匹配规则一致。

## Task 5：JSON/主题/图片引用一次性迁移与崩溃恢复

**Files:**
- Create: `src/ClipboardManager.Storage/Migration/LegacyHistoryDocument.cs`
- Create: `src/ClipboardManager.Storage/Migration/LegacyMigrationCoordinator.cs`
- Create: `src/ClipboardManager.Storage/Migration/MigrationJournal.cs`
- Create: `tests/ClipboardManager.Storage.Tests/LegacyMigrationTests.cs`
- Create: `tests/ClipboardManager.Storage.Tests/Fixtures/legacy-history.json`、合成 PNG fixtures

- [ ] 将现有 `history.json`、`theme.json`、图片名校验规则整理为完全合成 fixture；fixture 覆盖固定→收藏、缺省设置、等待删除图片、重复 ID、损坏 JSON 与图片引用。
- [ ] 先写 migration tests：先备份 JSON 与 theme；目标 DB 新建且导入数量/哈希/排序/收藏/设置/待删队列准确；导入事务中抛异常后 DB 回滚、旧文件不变、重试只导入一次；JSON 无效时不能建立“已迁移”标记。
- [ ] 实现只读旧 DTO，`IsPinned` 映射 `IsFavorite`；旧图片只校验合法 `{guid}.png` 文件名和有效文件引用，不解码后重编码；旧 `theme.json` 合入 settings 的主题字段；使用 Task 3 的 `IHistoryRepository.ImportLegacyAsync(migrationId, ...)` 在一个 DB transaction 中提交完整数据，并在同一事务保存已导入 migrationId。重启若发现 DB 已提交该 migrationId，则先校验数据再写外部完成标记，不重复导入。
- [ ] 以 legacy JSON、theme JSON（若存在）和所有被引用 PNG 的规范相对路径与 SHA256 生成稳定 `migrationId`；备份写入 `%LOCALAPPDATA%\ClipboardManager\migration-backup\<migrationId>\`，逐文件先写 `.tmp`、Flush(true)、原子 rename；创建 `migration.json` journal 记录阶段，但完成标记只在 SQLite commit 后写入。
- [ ] 为备份完成、schema 创建、导入事务前后、校验和完成标记逐点注入故障；每个恢复测试重启 coordinator 后验证可重试且原 JSON/PNG SHA256 不变。
- [ ] 实现启动检测：有有效迁移完成标记则不再读取 JSON；无 JSON 时启动空 DB；迁移中断时使用原文件重新跑幂等导入；未知/损坏的已存在 DB 只给恢复路径，不用空库覆盖。
- [ ] 运行迁移测试，并以 `sqlite3`/仓储查询核对 fixture 记录数、favorite 数、图片引用数和 settings 字段。

**验收：** 标记绝不早于 commit；旧数据永不由迁移删除；每个阶段中断均可恢复或明确停机。

## Task 6：Core history service 与文件清理队列

**Files:**
- Create: `src/ClipboardManager.Core/History/HistoryService.cs`
- Create: `src/ClipboardManager.Core/History/IImageStore.cs`
- Create: `tests/ClipboardManager.Core.Tests/HistoryServiceTests.cs`

- [ ] 使用 fake repository/image store 写失败测试：首次加文本/图像、hash 去重更新时间、保留收藏、写图片后提交失败删新文件、提交后清理失败将合法名称留待重试、手动删除收藏、清空含收藏、容量清理只删非收藏。
- [ ] 在接口中固定签名：`IImageStore.SavePngAsync(Guid id, ReadOnlyMemory<byte> png, CancellationToken)`、`DeleteAsync(string fileName, CancellationToken)`、`GetPath(string fileName)`；实现不得允许任意路径传入，文件名必须是 Guid `.png`。
- [ ] 实现 service mutation 流程：先经 Core 校验与去重，再写新图片，然后单个 repository mutation 提交 record/deletions/settings/pending deletes；只有提交成功才公布变更结果；删除文件永远发生在事务成功之后。
- [ ] 搜索请求将 query 长度限制、分页 offset/limit 范围校验放 Core；UI 每次输入取消前一个令牌，不能返回过期页面结果。
- [ ] 运行 Core tests 与 Storage integration tests。

**验收：** 仓储和图片存储失败不会导致内存/UI 声称成功或丢失可恢复图片。

## Task 7：Windows 图像管线与剪贴板适配

**Files:**
- Create: `src/ClipboardManager.Windows/Images/WindowsImageCodec.cs`
- Create: `src/ClipboardManager.Windows/Clipboard/NativeClipboardReader.cs`
- Create: `src/ClipboardManager.Windows/Clipboard/NativeClipboardWriter.cs`
- Create: `src/ClipboardManager.Windows/Clipboard/ClipboardSnapshotFactory.cs`
- Create: `tests/ClipboardManager.Windows.Tests/ImageCodecTests.cs`
- Create: `tests/ClipboardManager.Windows.Tests/ClipboardFormatTests.cs`
- Replace after migration: `Services/ImageProcessor.cs` WPF-specific path (legacy source retained until switch)

- [ ] 先用合成 PNG/BMP/DIB fixtures 写 codec 测试：规范化 RGBA hash 稳定、PNG 往返尺寸/像素一致、超过 40,000,000 像素/128 MB/8 MB 文本限制被拒绝、损坏输入返回分类错误。
- [ ] 基于 `Windows.Graphics.Imaging`、`SoftwareBitmap` 和 `BitmapEncoder` 实现 decode/normalize/PNG encode/DIB/DIBV5 转换；跨层只传 `ReadOnlyMemory<byte>` 和尺寸/像素值，不暴露 WinRT bitmap 到 Core。
- [ ] 用 `RegisterClipboardFormat` 与 Win32 GlobalMemoryLock/Unlock 实现 `PNG`、`image/png`、`JFIF`、`JPEG`、`image/jpeg`、`BMP`、`image/bmp`、CF_DIBV5、CF_DIB、CF_BITMAP、CF_HDROP 和 Unicode text 读取；优先顺序与旧实现一致，CF_BITMAP 像素必须在 `CloseClipboard` 前复制。
- [ ] 建专属 STA worker，clipboard busy 时有限退避重试；读写 API 都接受 cancellation token；用私有源标记防写回反馈环。
- [ ] 以注入 Win32 调用表覆盖 `OpenClipboard` 重试、格式注册/序列号/写回 marker；自动测试禁止调用真实系统剪贴板，真实桌面读写仅列入人工 smoke checklist。
- [ ] 在 Windows 11 runner 运行 Windows tests 和 image fixture tests；对缺少桌面交互的 runner 将真实剪贴板用例标为显式 skip，并保留 P/Invoke fake tests 必跑。

**验收：** 所有读取、解码、编码与剪贴板调用都在 adapter 层；关键尺寸上限和数据格式得到自动测试。

## Task 8：Win32 生命周期服务（热键、托盘、监听、主题）

**Files:**
- Create: `src/ClipboardManager.Windows/WindowsMessageWindow.cs`
- Create: `src/ClipboardManager.Windows/Clipboard/ClipboardChangeMonitor.cs`
- Create: `src/ClipboardManager.Windows/Input/GlobalHotKeyService.cs`
- Create: `src/ClipboardManager.Windows/Tray/SystemTrayService.cs`
- Create: `src/ClipboardManager.Windows/Theme/SystemThemeService.cs`
- Create: `src/ClipboardManager.Windows/WindowsServiceCollectionExtensions.cs`
- Create: `tests/ClipboardManager.Windows.Tests/NativeLifecycleTests.cs`

- [ ] 先以 injectable native function table 写测试：启动/停止幂等，热键冲突映射到可读错误，clipboard listener 注册/注销恰好一次，窗口销毁后不再派发事件，Dispose 释放通知 icon/callback/handle。
- [ ] 实现一个 STA 隐藏 HWND/message-only window，集中收取 `WM_CLIPBOARDUPDATE`、`WM_HOTKEY`、托盘 callback 和系统主题变化；委托严格在 UI dispatcher/同步上下文边界派发，且关闭时 detach。
- [ ] 注册 `Ctrl+Shift+Space`，提供激活主窗口事件；托盘仅在设置开启后建图标，菜单提供打开、暂停/恢复、退出；无权限/热键冲突不阻止应用启动。
- [ ] 用 WinUI `Window` 的 HWND adapter 提供置顶、最小化、隐藏、DWM backdrop 与高 DPI 尺寸；Core/VM 不引用 HWND 类型。
- [ ] 运行 Windows native lifecycle tests，增加手动 smoke checklist：热键冲突、Explorer 重启、系统主题变化、托盘重复启停。

**验收：** 所有 HWND、消息 hook、Win32 handle 生命周期集中于 Windows Adapter；应用关闭后没有后台原生资源存活。

## Task 9：App 组合根、启动恢复与日志边界

**Files:**
- Create: `src/ClipboardManager.App/App.xaml.cs`
- Create: `src/ClipboardManager.App/Services/AppHost.cs`
- Create: `src/ClipboardManager.App/Services/AppStartupCoordinator.cs`、`IUpdateRecoveryService.cs`
- Create: `tests/ClipboardManager.App.Tests/StartupCoordinatorTests.cs`
- Create: `src/ClipboardManager.App/Services/AppLogger.cs`
- Create: `src/ClipboardManager.App/Utilities/AppPaths.cs`

- [ ] 固定 `IUpdateRecoveryService.RecoverIfNeededAsync(CancellationToken)` 接口，由假的 recovery service 验证顺序；Task 14 再接入 worker process implementation。
- [ ] 先写启动次序测试：single instance → `IUpdateRecoveryService.RecoverIfNeededAsync` → data dirs → SQLite schema/migration → settings/theme → platform services → window → clipboard monitor → delayed update check；任何 DB/migration 错误不得开始监听或覆盖已有文件。
- [ ] 把日志接口抽象为结构化事件（时间、级别、操作、关联 ID、错误分类、程序版本）；加入自动测试，日志序列化绝不接受 clipboard text、image bytes、Authorization/header。
- [ ] 用 App Host/DI 注册 Core service、SQLite repository、image store、Windows adapter、update coordinator；只在 App 组合根取得 concrete type。
- [ ] 使用本机 `%LOCALAPPDATA%\ClipboardManager`，提供明确的 Open Data Directory / Open Log 操作；不要改变旧数据路径。
- [ ] 实现全局异常报告：UI 展示短说明+日志路径+重试入口；详细堆栈只进日志，异常消息含疑似用户内容时做敏感字段过滤。
- [ ] 运行 App startup tests 与 solution build。

**验收：** 出错启动不会启动采集；日志不含剪贴板内容；依赖通过 DI 明确组合。

## Task 10：WinUI Fluent 主窗口、导航与响应式骨架

**Files:**
- Create: `src/ClipboardManager.App/MainWindow.xaml`、`MainWindow.xaml.cs`
- Create: `src/ClipboardManager.App/Views/ShellPage.xaml`、`ShellPage.xaml.cs`
- Create: `src/ClipboardManager.App/ViewModels/ShellViewModel.cs`
- Create: `src/ClipboardManager.App/Resources/Styles.xaml`、`ThemeResources.xaml`
- Create: `tests/ClipboardManager.App.Tests/ShellStructureTests.cs`

- [ ] 先写 XAML 结构测试（沿用 Task 2 Architecture.Tests）：唯一 MainWindow；导航项显示“历史、收藏、更新中心、设置”；不包含 WPF Namespace、旧 `WindowOverlayHost` 或第二个用于 app page 的 Window；颜色资源 Light/Dark 键集合相同。
- [ ] 构造单窗口 NavigationView 与 Fluent title bar；设置页面只作为 Frame 页面，错误/破坏性确认使用 `ContentDialog`，不创建辅助 Window。
- [ ] 设启动默认窗口 1000×720、最小 720×520；在窗口宽度 <900 收起导航，在 <760 隐藏详情侧栏；至少保持主操作可见，不产生横向裁剪。
- [ ] 实现系统/浅色/深色主题切换资源与 `RequestedTheme`；系统主题变更即时应用；全部页面采用共享设计令牌、中文 copy、加载/空/错误/焦点态。
- [ ] 加 accessibility names、AutomationProperties、Tab 顺序和 keyboard focus visible；运行结构测试和 App build。

**验收：** 应用只有单窗口、导航与响应式框架；无需旧 WPF overlay 仍可导航全部一级页。

## Task 11：历史/收藏分页 UI、预览和键盘行为

**Files:**
- Create: `src/ClipboardManager.App/Views/HistoryPage.xaml`、`HistoryPage.xaml.cs`
- Create: `src/ClipboardManager.App/Views/RecordDetailsView.xaml`、`RecordDetailsView.xaml.cs`
- Create: `src/ClipboardManager.App/ViewModels/HistoryViewModel.cs`
- Create: `src/ClipboardManager.App/ViewModels/RecordItemViewModel.cs`
- Create: `src/ClipboardManager.App/Controls/AsyncThumbnail.xaml`、`AsyncThumbnail.xaml.cs`
- Create: `tests/ClipboardManager.App.Tests/HistoryViewModelTests.cs`

- [ ] 先写 ViewModel tests：加载分页；搜索 160 ms debounce，取消过期 query；全部/文本/图片筛选；收藏页面使用 FavoritesOnly；selected record 删除后 selection 合理回退；空结果、错误和 retry 状态准确。
- [ ] 构造 `ItemsRepeater`/`ListView` 的虚拟化记录列表，异步 thumbnail loader 限并发、限解码尺寸、离开视口/页面卸载时取消并释放 bitmap；详情文本显示完整可选文本，图片适配/缩放。
- [ ] 实现收藏/取消收藏、复制全文/图像、详情、删除；清空 `ContentDialog` 必须明确写明包括多少收藏和图片；UI 使用“收藏”而不是“固定”，窗口置顶单独标签。
- [ ] 键盘规则：Up/Down 移动选中；Enter/Ctrl+C 复制；Delete 删除；Ctrl+P 收藏状态切换；搜索框保留 Ctrl+C/V/A，按 Down 才把焦点移入结果列表；焦点项自动滚动到可见区域。
- [ ] 每次搜索/选择变化以单条集合通知更新，不全量重建 view model；复制后根据设置隐藏并恢复先前前台窗口。
- [ ] 运行 ViewModel/App tests；启动应用在 720、900、1440 logical px 和 100/150/200% display scale 检查无裁剪、虚拟化与焦点可访问名称。

**验收：** 历史、收藏、搜索、完整详情、复制、键盘、删除和高 DPI 均可实际操作；图片加载不阻塞 Dispatcher。

## Task 12：设置页与剪贴板采集生命周期

**Files:**
- Create: `src/ClipboardManager.App/Views/SettingsPage.xaml`、`SettingsPage.xaml.cs`
- Create: `src/ClipboardManager.App/ViewModels/SettingsViewModel.cs`
- Create: `src/ClipboardManager.App/Services/ClipboardCaptureCoordinator.cs`
- Create: `tests/ClipboardManager.App.Tests/SettingsViewModelTests.cs`
- Create: `tests/ClipboardManager.App.Tests/CaptureCoordinatorTests.cs`

- [ ] 写设置 tests：主题预览与保存/取消语义、records/MB 上下限、自动检查、tray、hide-after-copy、window topmost、数据目录操作；数据库保存失败时 UI 不假报成功且窗口状态不发生不一致。
- [ ] 实现收藏容量规则的可见提示；托盘选项立即启停，退出和最小化行为保持原应用语义；没有自启动新增项。
- [ ] 以 `IClipboardMonitor` 事件 + 100 ms debounce 实现 capture coordinator，暂停时清除 pending capture，恢复时不补抓旧内容；用 sequence number 去重系统通知。
- [ ] 串行执行 clipboard read → snapshot normalize/hash → history service AddAsync；每个阶段捕获异常并输出脱敏诊断，关闭时取消并等待所有操作结束。
- [ ] 运行 App tests 与手动集成 checklist：文本、截图、图片文件、程序写回不反馈循环、暂停期间不补抓、复制后可直接 Ctrl+V。

**验收：** 设置可保存，采集可暂停/恢复，旧剪贴板功能在新架构中端到端保持。

## Task 13：更新模型、状态机、Release 校验与自动检查

**Files:**
- Create: `src/ClipboardManager.Core/Updates/UpdateState.cs`
- Create: `src/ClipboardManager.Core/Updates/UpdateErrorKind.cs`
- Create: `src/ClipboardManager.Core/Updates/UpdateManifest.cs`
- Create: `src/ClipboardManager.Core/Updates/UpdateStateMachine.cs`
- Create: `src/ClipboardManager.App/Services/GitHubReleaseClient.cs`
- Create: `src/ClipboardManager.App/Services/UpdateCoordinator.cs`
- Create: `tests/ClipboardManager.Core.Tests/UpdateStateMachineTests.cs`
- Create: `tests/ClipboardManager.Update.Tests/ReleaseValidationTests.cs`

- [ ] 先写状态 transition tests：Idle→Checking→UpToDate/Available→Downloading→Verifying→Applying→StartupHealthCheck→Complete；无效跳转拒绝；Applying 后不能取消；每种 error kind 具有 retry allowed 与 user recovery copy。
- [ ] 编写 GitHub client contract tests（fake HttpMessageHandler）：只接受固定仓库、正式 release 非 draft/prerelease、正确 semver/platform、允许的 HTTPS 域名、release asset ID/name/size 一致；metadata ≤4 MiB，asset 大小有界，ETag 304 缓存结果正确。
- [ ] 计划文件格式字段：`schema/application/version/platform/packageKind/files[]`；path 为规范相对路径、拒绝 traversal/reparse/device name/data-directory overlap；单文件 ≤512 MiB、总解压体积 ≤2 GiB、附件压缩长度严格匹配。
- [ ] 仅在 20 秒启动延时后运行首次检查，后续 interval 60 分钟；settings 关闭时不请求网络；自动检查只发无阻塞导航徽标，绝不下载。
- [ ] `UpdateCoordinator` 只通过 UI command 开始下载；取消下载删除 partial staging；状态及错误分类通过可绑定状态对外呈现；所有网络任务可取消/观察。
- [ ] 运行 Core/Update tests，断言日志和请求中无剪贴板记录内容。

**验收：** 状态机与 GitHub 来源策略可独立测试；自动检查间隔正确且检查从不触发下载。

## Task 14：独立 UpdateWorker 的暂存、事务替换、健康标记和回滚

**Files:**
- Create: `src/ClipboardManager.UpdateWorker/UpdateTransaction.cs`
- Create: `src/ClipboardManager.UpdateWorker/UpdateOperation.cs`
- Create: `src/ClipboardManager.UpdateWorker/UpdateJournal.cs`
- Create: `src/ClipboardManager.App/Services/UpdateRecoveryService.cs`
- Create: `src/ClipboardManager.UpdateWorker/Program.cs`
- Create: `tests/ClipboardManager.Update.Tests/TransactionRecoveryTests.cs`
- Modify: `src/ClipboardManager.App/Services/UpdateCoordinator.cs`、`src/ClipboardManager.App/Services/AppStartupCoordinator.cs`、`src/ClipboardManager.App/App.xaml.cs`

- [ ] 通过 filesystem abstraction 写失败测试：下载/校验全部完成前正式安装目录哈希不可变化；journal 写入后逐文件备份/替换；注入任意替换故障恢复到全部旧 SHA256；恢复失败时保留 workspace 与诊断信息；成功健康标记后才清理备份。
- [ ] helper 只接受由已校验 manifest 生成的操作列表；二次验证所有 staged 文件 size/hash、现存旧文件哈希、更新路径及目录链接；绝不把 `%LOCALAPPDATA%\ClipboardManager` 或 workspace 外路径作为目标。
- [ ] 应用关闭后 helper 同盘备份每个目标、原子替换并写 journal 进度；每步可由启动时 `--recover` 重放/回滚；旧 manifest 最后提交。
- [ ] 使用 transaction GUID 与受限随机令牌写 `health-request.json`；启动 app 完成 DB 初始化、关键 adapter 建立、MainWindow Loaded 后原子写 `health-ready.json`。helper 等待最多 60 秒；进程退出或超时即回滚并启动旧版。
- [ ] helper 使用独立互斥体与 app single-instance mutex 协同，必须校验 PID + process start ticks，最多等待 app 退出 120 秒；占用/退出失败不改任何程序文件。
- [ ] worker 错误写入工作区诊断 JSON 和本地事件日志，不把用户文本放进对话框；恢复后启动旧版。Worker 不引用 `Microsoft.WindowsAppSDK`、WPF 或 WinForms。
- [ ] 对一次替换、删除过期文件、日志中断、app 启动崩溃、health timeout、恢复中断、便携目录只读、安装目录无权限逐项测试。

**验收：** 所有失败路径在受管程序文件中恢复旧版本或保留可继续恢复的事务；用户数据目录测试前后哈希一致。

## Task 15：更新中心 UI 与自动安装交互

**Files:**
- Create: `src/ClipboardManager.App/Views/UpdatesPage.xaml`、`UpdatesPage.xaml.cs`
- Create: `src/ClipboardManager.App/ViewModels/UpdatesViewModel.cs`
- Create: `tests/ClipboardManager.App.Tests/UpdatesViewModelTests.cs`

- [ ] ViewModel tests 覆盖版本/发行说明/下载量/最后检查/进度/校验/错误恢复按钮的状态映射；Checking/Downloading/Verifying 时重复点击不可启动第二个操作。
- [ ] 实现更新中心一级页面，清楚区分“自动检查”开关、“检查更新”、“下载并更新”；展示当前与可用版本、简明 release notes、下载总量、进度、验证状态、阶段错误和重试/取消操作。
- [ ] 发现更新仅显示 NavigationView badge/非阻塞 banner，不强制切页，不弹模态更新窗口。
- [ ] 下载校验成功后展示“正在准备重启”短暂状态，自动交接 worker 并关闭 UI；无需第二次重启确认；apply 开始后禁用取消。
- [ ] 无写权限时告知安装器覆盖或便携版手动替换路径；任何失败提供日志位置与可恢复动作。
- [ ] 运行 UI tests 和窄/默认/宽窗口更新页 visual QA，键盘和屏幕阅读器能读出阶段与进度。

**验收：** 更新 UI 为主窗口页面，无旧覆盖窗；确认用户主动下载后全流程自动更新、重启和错误恢复可见。

## Task 16：安装包、便携包、WPF 首发迁移与干净构建文档

**Files:**
- Modify: `package.ps1`
- Modify: `installer.ps1`
- Modify: `build.ps1`
- Modify: `package-updates.ps1`
- Modify: `publish-release.ps1`
- Modify: `.github/workflows/release.yml`
- Modify: `.github/workflows/installer.yml`
- Modify: `README.md`、`docs/USAGE.md`、`docs/DEVELOPMENT.md`
- Create: `tests/packaging/Test-Package.ps1`
- Create: `tests/packaging/Test-LegacyUpgrade.ps1`

- [ ] 将脚本 SDK 检测改为 .NET 10 SDK；所有输出在隔离目录；构建严格从 solution 新 App 发布，不把 WPF 根 project、obj/bin、测试样例、app data、token 或个人文件打入发行包。
- [ ] 先写 PowerShell 契约测试：相同构建产物生成 ZIP 与 installer payload；SHA256 sidecar 与 ZIP hash 一致；manifest 列出每个发行程序文件；拒绝同版本覆盖；不签名、不访问凭证存储、不使用 PAT。
- [ ] 配置 WinUI unpackaged self-contained x64 publish，确认 Windows App SDK framework 也随包部署；分别执行便携 ZIP 解压启动与每用户 Inno Setup install/upgrade/uninstall；uninstall 保留 LocalAppData 数据目录。
- [ ] 生成 helper 与 app 同 version、同架构发布包；更新 manifest 排除用户数据、卸载注册表敏感区及更新工作区；校验清单前后运行包文件哈希一致。
- [ ] 执行 clean clone 模拟：仅 checkout tracked files + 安装公开 SDK/VS workload → restore → build → tests → package → installer verify；用 secret scanning（例如 `gitleaks detect --no-git` 的公开工具）扫描源文件和发行 staging，不安装或配置个人 token。
- [ ] WPF 迁移隔离测试运行新安装版覆盖用户安装目录，并把旧 JSON、theme 与图片自动导入 SQLite，核验图片和用户数据；若 runner 或安装路径不能证明安全，则发布资产说明清楚写“首次升级必须退出旧应用并运行完整安装器，勿使用旧版本应用内增量更新”，release notes 也作相同说明。
- [ ] 更新 README/USAGE：架构、数据位置、收藏语义、自动检查时机与不下载规则、安装器/便携更新、WPF 一次性迁移、未签名安全提示、hash 校验命令、仓库受控威胁边界。

**验收：** 无秘密的 clean clone 完成两种打包；安装/卸载留存用户数据；WPF 转换路径明确且通过隔离验证或明确要求手动覆盖安装。

## Task 17：CI 门禁、故障矩阵和发布切换

**Files:**
- Modify: `.github/workflows/release.yml`
- Modify: `.github/workflows/installer.yml`
- Create: `.github/workflows/ci.yml`
- Create: `docs/RELEASE-CHECKLIST.md`
- Create: `docs/SECURITY.md`

- [ ] CI Pull Request job 在 Windows runner 安装固定 .NET 10 SDK 与公开 WinUI workload，执行 restore、Core/Storage/Windows/Update/App tests、Release build、package tests 和 source secret scan。
- [ ] 发布 job 只在全部测试通过之后以 `contents: write` 获取仓库默认 `GITHUB_TOKEN`；其余 CI job 使用 `contents: read`；禁止外部凭证和私有 signing secrets；不允许覆盖已有 Release，除非现有手动批准开关明确启用。
- [ ] 对隔离目录执行 updater failure matrix：无更新、禁自动检查、手动检查、取消下载、断网、限速、错误 repo/host、无效 manifest、丢失/超大/短附件、哈希不符、磁盘不足、只读目录、app 不退出、替换中断、health timeout、rollback 中断、成功后清理。
- [ ] 每条失败路径保存自动断言：应用文件在验证前哈希不变；回滚后旧文件哈希一致；`clipboard.db`、`images`、settings 与 JSON 备份 hash/bytes 始终不变。
- [ ] 对安装版与便携版验证 fresh launch、同版本拒绝、覆盖升级、新版健康启动、卸载保留数据；记录 runner/OS/SDK/版本/资产 SHA256。
- [ ] `docs/SECURITY.md` 明示 HTTPS + 固定 GitHub 来源 + SHA256 能发现传输损坏/非对应附件，但仓库或 release 权限被攻破可同时替换清单和文件；未签名 Windows 会提示未知发布者；不承诺此方案防恶意仓库持有人。
- [ ] 只有所有 gating checks green 后修改默认工作流正式从 WinUI 项目发布；保留旧工程用于历史追溯，不再将旧 WPF 包作为 release 输出。

**验收：** CI 和本地发布步骤一致；发布不需要开发者私人凭证；故障矩阵通过、威胁边界在公开文档中明确。

## Task 18：最终回归、性能测量和 legacy 工程停用

**Files:**
- Modify: `README.md`、`docs/USAGE.md`、`docs/DEVELOPMENT.md`
- Modify: `docs/RELEASE-CHECKLIST.md`
- Keep legacy source: 根 WPF project 与旧 WPF 专属 `Views/`、`MainWindow.cs`、WPF overlay/control/service/viewmodel 文件继续保留在仓库原路径，作为历史基线和后续数据迁移参考；只将其从新 solution 默认构建与 Release 流水线隔离，不删除、不移动。

- [ ] 用合成 5,000 条文本/图片数据库执行性能测量，记录机器、CPU、磁盘、SQLite build、首次/重复搜索耗时、滚动 frame hitch、thumbnail 并发和内存；若找不到验证标准，不编造阈值，先记录实际测量并以可重复 baseline 定义预算。
- [ ] 按规格验收清单检查 720/900/1440 宽度、100/150/200% 缩放、浅/深/跟随系统、空/加载/错误、Tab 顺序、读屏名称、热键、托盘、暂停、文本/图片全文复制、收藏与容量、删/清空、重启迁移恢复。
- [ ] 从 WPF 合成历史完整走一次安装器首发迁移，之后启动新版再次确认不读/双写 JSON；数据库备份和 JSON 回退备份仍在。
- [ ] 运行全部 `dotnet test ClipboardManager.sln -c Release`、`dotnet build ClipboardManager.sln -c Release`、clean publish、package/installer verify、`git diff --check`、secret scan。
- [ ] 验证 legacy WPF 项目仍可按 Task 1 的单独命令 build/test，但不参与新 solution 的默认 Release 打包；保留全部旧源文件、历史迁移 DTO 与 fixtures，README 将旧项目标为迁移参考。
- [ ] 以 `git status --short` 核对没有临时数据、发布包、个人路径、token、签名素材或真实剪贴板样本进入提交范围。

**验收：** 新 WinUI 方案满足完整手动与自动验收；旧 WPF 源码持续保留作迁移参考；新 Release 仅发布 WinUI 包，数据导入代码保留。

---

## 规格覆盖自检

- 工作台、历史/收藏、响应式详情、Fluent theme、空/加载/错误/键盘/accessibility：Tasks 10–12、18。
- 剪贴板格式、图片上限、去重、STA、全局快捷键、托盘、暂停/恢复、数据本地化：Tasks 7–9、12、18。
- JSON/主题/图片备份、事务、schema、幂等、故障注入、SQLite 全文包含语义：Tasks 4–6、9、16、18。
- 更新 UI 状态、自动检查 20s/60min、不自动下载、HTTPS 来源/附件、哈希、取消、应用退出、更新事务、健康确认、回滚、数据目录隔离：Tasks 13–15、17。
- WPF→WinUI 首发决定、安装版和便携版、无秘密 clean clone、最小权限 Release、未签名威胁边界和 uninstall 留存：Tasks 1–2、16–18。
- 性能测量、高 DPI、屏幕阅读器、窄/宽视图和真实集成回归：Tasks 10–12、18。

## 执行顺序与停靠点

- Tasks 1–6：建立可测试新底座与可回滚数据迁移；Storage 故障注入未过不得接 UI。
- Tasks 7–9：完成 Windows 平台能力与安全启动；clipboard lifecycle 未通过不得启动正式监听。
- Tasks 10–12：完成主 UI 与原日常能力；UI smoke 未通过不得对外切换入口。
- Tasks 13–15：更新管线和正式更新中心；事务故障矩阵未过不得提供应用内“下载并更新”。
- Tasks 16–18：发行、CI、迁移回归、性能测量与 legacy 工程停用（源码保留）；clean clone 和迁移验收为正式切换硬门槛。

每个停靠点都应先运行相应层测试并查看差异。发现 SQLite 迁移、Windows App SDK self-contained 或 WPF 首发升级策略不能通过，保留旧发行入口并只修复阻塞项；不得通过删验收、忽略错误或静默清空数据来推进。
