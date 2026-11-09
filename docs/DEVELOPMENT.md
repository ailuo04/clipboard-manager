# 单窗口覆盖层开发约定

设置、应用更新和记录详情都属于主窗口中的辅助页面。新增或改造辅助页面时，沿用 `MainWindow`、`Views/WindowFrame.xaml` 和同一个 `WindowOverlayHost`；不要为设置、更新、详情或其他应用内页面创建新的 WPF `Window`。这样主界面状态、主题、焦点和页面返回都留在同一个窗口会话内。

原生确认 `MessageBox` 是有意保留的例外（例如降低保留上限、确认安装更新）；它不是承载设置或其他页面的替代窗口。

## 架构分层

- `MainViewModel` 只负责可绑定状态、命令与设置/窗口行为；剪贴板采集在 `ClipboardCaptureCoordinator`，更新管线在 `UpdateCoordinator`。
- `WindowOverlayHost` 只负责页面栈、生命周期与焦点；窗口几何与状态快照在 `OverlayWindowPresenter`。
- `UpdateService` 只做编排；GitHub 流量在 `GitHubReleaseClient`，HTTP 错误处理在 `GitHubHttp`，文件差分与工作区在 `UpdateFiles`（后者同时被 .NET Framework 更新器编译，必须保持旧语法）。
- 视图为编译式 XAML（`x:Class` partial 类），代码通过生成字段访问控件，不要写 `FindName("...")`。

## 页面与返回

- 页面继承 `OverlayView`，在其 `UserControl` 内容中加载相应的 `Views/*.xaml`。页面通过 `Complete(bool?)` 请求关闭，不直接关闭窗口。
- 由 `WindowOverlayHost.ShowAsync(page, size)` 显示页面，并等待它的 `Task<bool?>`：`true` 表示确认/保存，`false` 表示显式取消，`null` 表示关闭或取消式退出。调用者应只在与操作相符的结果下提交数据。
- 第一个页面开始覆盖层会话；后续 `ShowAsync` 调用把当前页面实例压入父页面栈，再显示子页面。子页面结束后宿主恢复原父页面实例，因此例如从设置进入更新再返回时，不会丢失未保存的输入。不要重新构造父页面来实现“返回”。
- 关闭、Esc、宿主整体关闭及页面显示失败等退出路径由宿主集中完成。页面的 `OnDismissed(result)` 在其完成任务前调用；实现该钩子时应保持可重复调用安全，并负责页面自身的清理，例如取消页面生命周期 `CancellationTokenSource`、解除事件订阅、清空密码输入或释放图片引用。异步操作应在页面退出后停止更新已卸载的 UI。

## 异步操作

当前更新页在首次 `Loaded` 时自动检查一次；通过 `StartOperation` 跟踪并观察异步任务、记录异常，检查和下载使用页面生命周期令牌。页面关闭时 `OnDismissed` 取消页面生命周期令牌。主视图模型等待更新页关闭后还会等待该页的 `WhenIdle`，再继续安装交接或收尾。详情页的图片加载与复制同样使用页面生命周期令牌，并在 `await` 后检查页面是否已关闭。

新增异步检查或网络/文件操作时，沿用这些现有约束：异步任务必须被等待或显式观察，使用可取消令牌，捕获并报告失败；每次 `await` 返回后，在触碰控件前检查页面仍然有效。不要留下未观察的任务或让已关闭页面继续修改界面。

## 视图与控件访问

`Views/*.xaml` 现在是编译式 XAML（`<Page>` + `x:Class`），对应 `.xaml.cs` 中的 `partial class` 调用 `InitializeComponent()`。业务代码直接用生成的字段访问命名控件（例如 `view.SaveButton`），**不要再写 `FindName("...")` 加类型转换**：控件名写错会在编译期报错，而不是等到运行时才抛异常。页面类是内部实现细节，但仍需保留 `x:Name`，因为测试会直接读取这些字段。

新增视图时同步在 `ClipboardManager.csproj` 的 `<ItemGroup>` 中加 `<Page Include="Views\XxxView.xaml" />`，并创建同名 `.xaml.cs`。`App.xaml` 例外：它仍是 `EmbeddedResource`，由 `ViewLoader` 在启动时解析成 `ResourceDictionary`。

两套调色板 `LightGlass.xaml` 与 `LiquidGlass.xaml` 也是编译式 `ResourceDictionary`（`partial class : ResourceDictionary`），`ThemeManager` 通过 `new LightGlass()` / `new LiquidGlass()` 切换。

## 主题资源

`App.xaml` 放置跨主题的共享资源；随系统/用户主题变化的颜色、画刷和效果放在 `Views/LightGlass.xaml` 与 `Views/LiquidGlass.xaml`，并在两套调色板中使用完全相同的 `x:Key`。视图中使用 `{DynamicResource Key}` 引用可随主题切换的资源，不要把主题颜色复制成页面局部常量，也不要将主题画刷改为 `StaticResource`。

新增主题键时同时更新两套调色板。测试会检查调色板键集合相等，并扫描 `Views/*.xaml` 中的动态资源引用，要求每个键存在于共享 `App.xaml` 资源或两套调色板；四个覆盖层资源 `OverlayScrimBrush`、`OverlayGlassBrush`、`OverlayEdgeBrush`、`OverlayShadowEffect` 也会被显式检查。页面内用 `x:Key` 声明的局部资源不应被误报成调色板遗漏。

## 焦点、Tab 与 Esc

覆盖层显示时，宿主禁用主表面的输入并关闭其命中测试，在根 `OverlayLayer` 上方接收交互；不要依赖单独窗口来阻止背景输入。宿主尝试聚焦页面中第一个可用控件，并在页面/会话返回时恢复之前保存的焦点（如果该元素不可用则回退）。

`WindowFrame.xaml` 在遮罩层与内容控件上配置了 `KeyboardNavigation.TabNavigation="Cycle"`，页面仍需提供合理的 Tab 顺序和可访问名称。保持遮罩位于主表面之上，避免把主界面控件加入覆盖页的键盘导航路径。宿主处理 Esc：它只关闭当前栈顶页面；存在父页面时返回父页面，否则结束覆盖层会话。不要在子页面里用 Esc 直接关闭主窗口。

## 修改后的自检

1. 页面仍由主窗口的覆盖层宿主显示；没有为页面新增 `Window`。
2. 保存、显式取消和关闭/Esc 的结果语义正确；嵌套返回恢复原页面实例。
3. `OnDismissed` 清理页面拥有的异步资源、事件处理器和敏感/大型数据引用。
4. 新增动态主题键存在于 `App.xaml` 或两套调色板，且 Light/Dark 键集合仍一致。
5. 页面初始焦点、Tab 循环、Esc 返回和遮罩期间的背景输入约束没有被破坏。

## WinUI 3 新方案开发说明（基线已锁定）

本节说明迁移到 WinUI 3 / .NET 10 后的开发约定；上面的 WPF 覆盖层约定在旧工程退役前仍然有效，作为迁移参照保留。

- **现状**：仓库仍以旧 WPF 工程（`ClipboardManager.csproj`，`net8.0-windows`）为正式构建/发布入口。新的 .NET 10 解决方案尚未建立；首批骨架、分层工程与命令由实施计划 Task 2 引入，完成前不要假设 `ClipboardManager.sln` 存在。
- **基线证据**：旧版测试基线、数据布局、发布资产命名、安装默认路径、互斥体名、旧应用入口链，以及 WPF → WinUI 首发切换策略，记录在 `docs/superpowers/plans/evidence/legacy-baseline.md`。改动用户数据布局、发布资产名或互斥体前，先更新该证据文档。
- **目标分层**：`src/ClipboardManager.Core`、`src/ClipboardManager.Storage`、`src/ClipboardManager.Windows`、`src/ClipboardManager.App`、`src/ClipboardManager.UpdateWorker`，测试位于 `tests/`。Core/Storage/UpdateWorker 不得引用 WPF 或 WinUI 控件类型；Win32/WinRT 细节集中在 Windows 适配层。
- **工具链**：公开 .NET 10 SDK（`global.json` 固定 `10.0.403`，`rollForward: latestPatch`）、Windows App SDK 2.6.5、公开 VS C++/WinUI 工作负载、PowerShell 7。构建、测试、打包与发布都不得依赖个人私钥、代码签名证书、PAT 或开发者本机私有文件。
- **常用命令**（新方案建立后）：`dotnet restore ClipboardManager.sln`、`dotnet test ClipboardManager.sln -c Release`、`dotnet build src/ClipboardManager.App/ClipboardManager.App.csproj -c Release -p:Platform=x64`。旧基线仍可单独运行：`dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj -c Release --nologo`。只有在实际执行并核对退出码/测试数后才可声称通过。
- **数据边界**：用户数据根目录保持 `%LOCALAPPDATA%\ClipboardManager`；SQLite 是迁移完成后的唯一运行时权威，旧 JSON 只导入一次且原文件与备份不被自动删除。更新 worker 只操作清单管理的程序文件，永不读写该数据目录。
- **首发切换**：在隔离环境用合成数据完成升级验证并留存证据之前，WPF → WinUI 首发必须要求用户运行完整安装器覆盖安装，不允许旧 `ClipboardManager.Update.exe` 直接增量替换新架构；发行说明也不得暗示可无缝增量升级。协议见 `legacy-baseline.md` 第 6 节。
- **迁移演练的隔离边界**：本机演练只能通过 `USERPROFILE`/`LOCALAPPDATA`/`APPDATA`/`TEMP`/`TMP` 重定向文件路径（`AppPaths` 的数据根目录跟随 `USERPROFILE`，不是 `LOCALAPPDATA`）；`HKCU` 卸载注册表项、开始菜单/桌面快捷方式与应用互斥体不受这些变量影响，且 `GetFolderPath` 在同一进程内首次调用后会被缓存。**升级/卸载演练必须在一次性 VM 或 CI runner 上执行**，禁止在普通开发桌面运行；收尾必须核对卸载注册表项与快捷方式已移除、合成用户数据哈希未变。
