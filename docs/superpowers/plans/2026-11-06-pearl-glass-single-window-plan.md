# 珍珠冰透单窗口界面实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将剪贴板主界面与设置、更新、详情统一为珍珠冰透视觉，并把三个辅助页面改为同一主窗口内的可返回覆盖层。

**Architecture:** 保留现有 `MainWindow`、动态主题资源和业务服务；在主窗口根视觉树增加单窗口覆盖层宿主，通过共享页面栈保存当前/父级覆盖页面和完成结果。设置页按需要临时扩展主窗口并在退出时恢复；设置、更新、详情由 `UserControl` 页面承载，不再各自创建 WPF `Window`。

**Tech Stack:** .NET 8 WPF、C#、XAML、xUnit、现有 `ViewLoader`、`ThemeManager` 和动态 `ResourceDictionary`。

---

## 文件地图

- Create: `Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj`、`Tests/ClipboardManager.Tests/ThemeAndOverlayTests.cs` — 回归覆盖主题默认值、覆盖层页面栈、窗口尺寸裁切和两套调色板资源键。
- Modify: `ClipboardManager.csproj` — 排除嵌套测试源码，避免根应用项目重复编译。
- Create: `Properties/AssemblyInfo.cs` — 只向测试程序集开放纯策略辅助方法。
- Create: `Services/ThemePreferencePolicy.cs` — 缺失/无效主题配置的可测回退策略。
- Modify: `Services/ThemeManager.cs`、`Models/AppTheme.cs`（仅在测试显示需要时）— 采用浅色回退，保留有效已存偏好和系统主题监听。
- Create: `OverlayView.cs`、`OverlayPageStack.cs`、`OverlayWindowGeometry.cs`、`WindowOverlayHost.cs` — 页面关闭生命周期、嵌套返回栈、窗口工作区裁切和根覆盖层管理。
- Modify: `Views/WindowFrame.xaml`、`MainWindow.cs` — 建立根覆盖层宿主、背景遮罩和失焦/焦点恢复。
- Rename/modify: `SettingsWindow.cs` → `SettingsOverlay.cs`、`UpdateWindow.cs` → `UpdateOverlay.cs`、`DetailsWindow.cs` → `DetailsOverlay.cs` — 将窗口代码后置改为可嵌入的 `OverlayView`，保留既有表单、更新和详情业务行为。
- Modify: `ViewModels/MainViewModel.cs`、`ViewModels/RecordViewModel.cs` — 通过覆盖层宿主打开并等待页面结果，保留更新安装和设置保存事务。
- Modify: `Views/LightGlass.xaml`、`Views/LiquidGlass.xaml` — 同步覆盖层遮罩、卡片、边缘和焦点资源键，完善珍珠浅色层次。
- Modify: `Views/MainView.xaml`、`Views/SettingsView.xaml`、`Views/UpdateView.xaml`、`Views/DetailsView.xaml` — 调整页面在共享玻璃面板内的布局，保留现有绑定与自动化名称。
- Create: `docs/DEVELOPMENT.md` — 说明新增辅助页面必须作为主窗口覆盖层实现，不单独创建页面窗口。

## 实施任务

### Task 1: 建立回归测试项目并让新默认值测试先失败

**Files:**
- Create: `Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj`
- Create: `Tests/ClipboardManager.Tests/ThemeAndOverlayTests.cs`
- Create: `Properties/AssemblyInfo.cs`
- Modify: `ClipboardManager.csproj`
- Create: `Services/ThemePreferencePolicy.cs`

- [ ] **Step 1: 创建 Windows xUnit 测试项目**

运行：

```powershell
dotnet new xunit --framework net8.0 --output Tests/ClipboardManager.Tests
```

把测试项目属性改为 Windows WPF，并保留模板的 `PackageReference`：

```xml
<PropertyGroup>
  <TargetFramework>net8.0-windows</TargetFramework>
  <UseWPF>true</UseWPF>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="../../ClipboardManager.csproj" />
  <None Include="../../Views/*.xaml" Link="Xaml/Views/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: 防止应用项目包含测试目录源码**

在根项目 `<ItemGroup>` 中加入：

```xml
<Compile Remove="Tests/**/*.cs" />
```

创建 `Properties/AssemblyInfo.cs`：

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ClipboardManager.Tests")]
```

- [ ] **Step 3: 添加初始主题策略并断言浅色回退**

创建 `Services/ThemePreferencePolicy.cs`，先以当前深色回退作为 RED 基线：

```csharp
using ClipboardManager.Models;

namespace ClipboardManager.Services;

internal static class ThemePreferencePolicy
{
    internal static AppTheme Resolve(AppTheme? saved) =>
        saved is { } value && Enum.IsDefined(value) ? value : AppTheme.Dark;
}
```

在测试文件中加入以下测试：

```csharp
using System.Xml.Linq;
using ClipboardManager.Models;
using ClipboardManager.Services;
using Xunit;

namespace ClipboardManager.Tests;

public sealed class ThemeAndOverlayTests
{
    [Theory]
    [InlineData(null, AppTheme.Light)]
    [InlineData(AppTheme.Light, AppTheme.Light)]
    [InlineData(AppTheme.Dark, AppTheme.Dark)]
    [InlineData(AppTheme.System, AppTheme.System)]
    [InlineData((AppTheme)99, AppTheme.Light)]
    public void MissingOrInvalidThemeUsesLightAndValidPreferenceIsPreserved(AppTheme? saved, AppTheme expected)
    {
        Assert.Equal(expected, ThemePreferencePolicy.Resolve(saved));
    }
}
```

- [ ] **Step 4: 确认浅色默认值测试按预期失败**

运行：

```powershell
dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --filter FullyQualifiedName~MissingOrInvalidThemeUsesLightAndValidPreferenceIsPreserved
```

预期：编译成功；`null` 与 `(AppTheme)99` 两个用例失败，实际为 `Dark`。这验证新测试确实能捕获现状。

### Task 2: 改为浅色配置回退并验证主题资源一致

**Files:**
- Modify: `Services/ThemePreferencePolicy.cs`
- Modify: `Services/ThemeManager.cs`
- Modify: `Tests/ClipboardManager.Tests/ThemeAndOverlayTests.cs`
- Modify: `Views/LightGlass.xaml`、`Views/LiquidGlass.xaml`

- [ ] **Step 1: 实现浅色回退且只替换无效/缺失偏好**

把策略改为：

```csharp
internal static class ThemePreferencePolicy
{
    internal static AppTheme Resolve(AppTheme? saved) =>
        saved is { } value && Enum.IsDefined(value) ? value : AppTheme.Light;
}
```

`ThemeManager` 将静态初始值设为浅色，`Apply` 收到未定义枚举时回退浅色，并通过策略解析文件缺失、JSON 缺失主题、越界枚举和读取异常。保留 `SystemEvents.UserPreferenceChanged` 的现有行为，不改有效的 `Light`、`Dark`、`System` 保存值。`ThemeDocument.Theme` 的缺省值设为 `AppTheme.Light`，确保 JSON 没有 `theme` 字段时不意外采用深色。

- [ ] **Step 2: 增加两套调色板键集合相同的测试**

在测试类增加：

```csharp
[Fact]
public void LightAndDarkPalettesExposeIdenticalResourceKeys()
{
    string root = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views");
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
    static string[] Keys(string path, XNamespace ns) => XDocument.Load(path).Root!
        .DescendantsAndSelf().Attributes(ns + "Key").Select(a => a.Value)
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    Assert.Equal(Keys(Path.Combine(root, "LightGlass.xaml"), x),
        Keys(Path.Combine(root, "LiquidGlass.xaml"), x));
}
```

- [ ] **Step 3: 运行主题回归测试**

运行：

```powershell
dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj
```

预期：主题策略用例通过；资源键差异测试在新增覆盖层键前保持通过。后续每加入一个覆盖层键，必须同步在两份调色板中定义。

### Task 3: 为页面栈与工作区布局建立可测的纯逻辑

**Files:**
- Create: `OverlayView.cs`
- Create: `OverlayPageStack.cs`
- Create: `OverlayWindowGeometry.cs`
- Modify: `Tests/ClipboardManager.Tests/ThemeAndOverlayTests.cs`

- [ ] **Step 1: 写嵌套覆盖页返回顺序测试**

添加以下纯逻辑测试：

```csharp
[Fact]
public void OverlayStackReturnsToParentAndThenBecomesEmpty()
{
    var stack = new OverlayPageStack<string>();
    stack.Push("settings");
    stack.Push("updates");

    Assert.Equal("updates", stack.Current);
    Assert.Equal("updates", stack.Pop());
    Assert.Equal("settings", stack.Current);
    Assert.Equal("settings", stack.Pop());
    Assert.Null(stack.Current);
    Assert.Equal(0, stack.Count);
}
```

- [ ] **Step 2: 确认新页面栈测试先失败**

运行：

```powershell
dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --filter FullyQualifiedName~OverlayStackReturnsToParentAndThenBecomesEmpty
```

预期：失败，因为 `OverlayPageStack<T>` 尚未定义。

- [ ] **Step 3: 实现可嵌套且不接受空项的栈**

创建 `OverlayPageStack<T>`：

```csharp
internal sealed class OverlayPageStack<T> where T : class
{
    private readonly Stack<T> _items = new();
    internal int Count => _items.Count;
    internal T? Current => _items.TryPeek(out T? item) ? item : null;
    internal void Push(T item) => _items.Push(item ?? throw new ArgumentNullException(nameof(item)));
    internal T? Pop() => _items.TryPop(out T? item) ? item : null;
    internal void Clear() => _items.Clear();
}
```

创建页面基类，统一完成回调和页面退出清理：

```csharp
using System.Windows.Controls;

namespace ClipboardManager;

internal abstract class OverlayView : UserControl
{
    internal Action<bool?>? CloseRequest { get; set; }
    internal virtual void OnDismissed(bool? result) { }
    protected void Complete(bool? result) => CloseRequest?.Invoke(result);
}
```

- [ ] **Step 4: 添加工作区尺寸裁切策略及边界测试**

创建 `OverlayWindowGeometry.Fit(Rect workArea, Size desired, double margin)`。它返回居中且完整落在工作区内的 `Rect`；请求尺寸超过工作区时先将宽高限制为工作区减去两侧边距，并确保最终宽、高不小于零。测试正常居中、宽高超限和边距受限三种输入，断言结果 `Left/Top/Right/Bottom` 均在 `workArea` 内。

- [ ] **Step 5: 运行页面栈和几何测试**

运行：

```powershell
dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --filter FullyQualifiedName~Overlay
```

预期：页面栈返回顺序和所有几何边界用例通过。

### Task 4: 在主窗口根视图加入共享覆盖层宿主

**Files:**
- Create: `WindowOverlayHost.cs`
- Modify: `Views/WindowFrame.xaml`
- Modify: `Views/LightGlass.xaml`、`Views/LiquidGlass.xaml`
- Modify: `MainWindow.cs`

- [ ] **Step 1: 为根窗口 XAML 添加主表面与遮罩层**

把现有标题栏和 `WindowContent` 放入 `MainSurface`，并在其上添加默认折叠的 `OverlayLayer`。覆盖层占据根 Grid 全部区域，使主标题栏按钮也不能绕过遮罩接收点击；内容区使用居中的玻璃 `Border` 和 `ContentControl`，Esc 由根层处理。采用明确的 `x:Name`：`MainSurface`、`OverlayLayer`、`OverlayPanel`、`OverlayContent`。玻璃面板宽高不得超出宿主，页面内容超出时在各页内部滚动。

根节点结构：

```xml
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Background="{DynamicResource WindowBackgroundBrush}">
  <Grid x:Name="MainSurface">
    <!-- 保留现有两行标题栏与 WindowContent 内容。 -->
  </Grid>
  <Grid x:Name="OverlayLayer" Visibility="Collapsed" Panel.ZIndex="10"
        Background="{DynamicResource OverlayScrimBrush}"
        KeyboardNavigation.TabNavigation="Cycle">
    <Border x:Name="OverlayPanel" Width="Auto" MaxWidth="800" MaxHeight="760"
            Margin="18" Padding="0"
            Background="{DynamicResource OverlayGlassBrush}"
            BorderBrush="{DynamicResource OverlayEdgeBrush}" BorderThickness="1"
            CornerRadius="24" Effect="{DynamicResource OverlayShadowEffect}">
      <ContentControl x:Name="OverlayContent" />
    </Border>
  </Grid>
</Grid>
```

实际布局必须保留现有 `TitleBar`、`WindowContent` 名称及其 Grid 位置，面板使用最大约束而非依赖 `Width="Auto"` 的空内容测量值。

- [ ] **Step 2: 在两套调色板增加完全同名的覆盖层资源**

增加 `OverlayScrimBrush`、`OverlayGlassBrush`、`OverlayEdgeBrush` 和 `OverlayShadowEffect`；浅色使用低对比冷灰遮罩、带高不透明度的珍珠渐变和白色边缘高光，深色使用深色遮罩、深石墨半透明渐变和浅青蓝边缘。不得把遮罩/卡片刷写成固定颜色到 `WindowFrame.xaml`。

- [ ] **Step 3: 实现宿主会话、关闭、嵌套返回和几何恢复**

`WindowOverlayHost(Window owner, Grid root)` 读取四个命名元素；`ShowAsync(OverlayView page, OverlaySize size)` 返回 `Task<bool?>`。首次进入时记录主窗口 `WindowState`、正常窗口 `Left/Top/Width/Height`、打开前焦点元素和 `Topmost`，关闭主表面输入并启用柔和模糊；打开 Settings 页面时按屏幕工作区扩展主窗。后续 `ShowAsync` 将当前页保存在页面栈，切换到子页但不重新拍窗口快照。`CompleteCurrent(result)` 先调用页面 `OnDismissed(result)` 并完成该页任务；存在父页时恢复父页和焦点，否则折叠覆盖层、解除模糊/禁用、恢复窗口几何与主界面焦点。Esc 等同当前页 `Complete(null)`。宿主拒绝空页；任何退出路径都通过 `try/finally` 恢复窗口状态。

- [ ] **Step 4: 接入主窗生命周期和关闭流程**

在 `MainWindow` 加载 `WindowFrame.xaml` 后创建一次 `WindowOverlayHost`，并在 `Closed` 前调用 `CloseAll(null)`/清理效果；传给 `MainViewModel`。主窗口的 `Closing`、Alt+F4 和托盘退出必须关闭 overlay 会话并取消页面异步工作，不得卡在待完成的 `TaskCompletionSource`。

- [ ] **Step 5: 构建并运行纯逻辑回归**

运行：

```powershell
dotnet build ClipboardManager.csproj --configuration Debug
dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj
```

预期：应用与测试项目均构建成功；已有测试全部通过。

### Task 5: 把设置窗口改成可返回的覆盖层

**Files:**
- Rename: `SettingsWindow.cs` → `SettingsOverlay.cs`
- Modify: `Views/SettingsView.xaml`
- Modify: `ViewModels/MainViewModel.cs`
- Modify: `MainWindow.cs`、`WindowOverlayHost.cs`

- [ ] **Step 1: 将设置代码后置迁移为 `OverlayView`**

`SettingsOverlay` 构造时仍克隆 `AppSettings`、加载 `Views/SettingsView.xaml`、绑定原有主题 RadioButton/上限输入/托盘复选框/目录日志按钮。以 `Complete(false)` 替代 `DialogResult=false`，校验成功后设置 `Settings`、`SelectedTheme` 并调用 `Complete(true)`。验证失败继续将错误写入 `ValidationText` 并聚焦对应文本框。取消或外部关闭时 `OnDismissed(result)` 在 `result != true` 时回滚 `_openedTheme`。下调记录上限的确认框仍使用系统消息框，但 owner 取 `Window.GetWindow(this)`。

- [ ] **Step 2: 让设置表单在玻璃面板中滚动且保留底栏**

调整 `SettingsView.xaml`：将内容区 ScrollViewer 设为 `Grid.Row=0`、底部取消/保存按钮设为 `Grid.Row=1`，保持底栏不随内容滚动；页面根节点边距约 22px，标题与区域分组保留，表单内部最小宽度 430px。保留所有 `x:Name`、验证文字和 AutomationProperties。

- [ ] **Step 3: 通过宿主打开设置并提交成功结果**

`MainViewModel.ShowSettingsAsync` 创建 `SettingsOverlay` 并执行 `await _overlays.ShowAsync(dialog, OverlaySize.Settings)`。仅返回 `true` 时运行原 `UpdateSettingsAsync`、`ThemeManager.SetPreference`、`ApplyWindowSettings`、`ApplyTrayMode`、更新计时器、刷新记录与提示状态的保存事务；取消时不写配置。`MainWindow` 将相同的 `WindowOverlayHost` 注入 VM。

- [ ] **Step 4: 验证即时主题预览和取消回滚**

运行：

```powershell
dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj
```

手工：打开设置，切换深/浅主题确认整个玻璃层即时更新；取消后恢复打开前主题；修改限制并保存后确认数值和主界面状态更新。分别从普通尺寸和已最大化窗口打开设置，关闭后窗口状态/位置/尺寸恢复。

### Task 6: 把更新页面纳入同一覆盖层栈

**Files:**
- Rename: `UpdateWindow.cs` → `UpdateOverlay.cs`
- Modify: `Views/UpdateView.xaml`
- Modify: `Views/SettingsOverlay.cs`
- Modify: `ViewModels/MainViewModel.cs`
- Modify: `WindowOverlayHost.cs`

- [ ] **Step 1: 将更新页迁移为 `OverlayView` 并保留取消清理**

`UpdateOverlay` 加载既有 `UpdateView.xaml` 控件与当前版本；页面第一次挂载时自动调用一次 `CheckAsync`，以后从更新返回设置再切回时不得重复自动检查。重试按钮继续调用 `CheckAsync`；进度、取消下载、令牌管理（如果当前源文件包含该功能）、错误文字和安装可用状态保持原语义。关闭/取消由 `Complete(null)` 结束；`OnDismissed` 设置 `_closed`、取消 `_lifetime`、清空 PasswordBox，避免已离开的页面继续更新 UI。

- [ ] **Step 2: 迁移安装确认与成功退出路径**

安装确认继续用原生 Yes/No 消息框，owner 改为 `Window.GetWindow(this)`。准备更新成功时通过 `Complete(true)` 返回 VM；VM 仅在确认成功且主窗口未关闭时调用 `LaunchPrepared()`、关闭全部覆盖层并调用 `MainWindow.ExitApplication()`。取消、下载失败和异常继续显示原状态并保留 `CancelPrepared()` 清理。

- [ ] **Step 3: 从主界面或设置页打开更新子页面**

将 `UpdateCommand` 改为 `AsyncRelayCommand` 并更新 `RaiseCanExecuteChanged` 转型；主界面更新入口执行根 `ShowAsync`，设置页更新按钮执行子 `ShowAsync`。子页关闭后恢复同一个设置页实例与用户未提交输入；子页安装成功则清空宿主栈后正常退出以启动更新器。

- [ ] **Step 4: 验证设置嵌套往返和更新取消**

运行：

```powershell
dotnet build ClipboardManager.csproj --configuration Debug
dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj
```

手工打开设置并修改一个未保存值，进入应用更新后取消/关闭，确认仍回到设置且输入未变；在更新页验证检查、错误、进度与取消仍正常。

### Task 7: 把详情页面迁移为覆盖层

**Files:**
- Rename: `DetailsWindow.cs` → `DetailsOverlay.cs`
- Modify: `Views/DetailsView.xaml`
- Modify: `ViewModels/MainViewModel.cs`
- Modify: `ViewModels/RecordViewModel.cs`
- Modify: `WindowOverlayHost.cs`

- [ ] **Step 1: 迁移详情复制与图片生命周期**

`DetailsOverlay : OverlayView` 保留 RecordViewModel `DataContext`、文本只读、复制按钮、Fit/Zoom 控件和原图异步加载。关闭按钮执行 `Complete(null)`；关闭页面时 `OnDismissed` 取消并释放 `_lifetime`、清空 `FullImage.Source`。防止父页面再次挂载时重复加载同一原图。

- [ ] **Step 2: 将详情命令改为打开覆盖层且回归主界面上下文**

`MainViewModel.ShowDetails` 创建 `DetailsOverlay` 并启动异步宿主调用，捕获异常后使用现有 `Report("打开详情失败", exception)`；`RecordViewModel.DetailsCommand` 改用 `AsyncRelayCommand` 并同步更新状态刷新时的命令类型。详情关闭后不重建主视图，不刷新/重置搜索、筛选、选择或列表滚动位置。

- [ ] **Step 3: 确认详情布局在可用区域内**

把详情页内容限制于面板可用高度/宽度；文本与图片视图继续共享滚动容器，较小窗口下启用滚动，图片 Fit 模式按实际 viewport 尺寸缩放。保留复制状态、图片加载状态和缩放自动化名称。

- [ ] **Step 4: 验证文本与图片详情路径**

手工检查文本详情完整选择/滚动/复制；图片详情加载、适应窗口和缩放；关闭后原选中项、筛选条件、搜索和列表滚动均保持。确保没有设置/更新/详情类独立 `Window` 被创建。

### Task 8: 打磨珍珠冰透主界面与覆盖层一致性

**Files:**
- Modify: `Views/LightGlass.xaml`、`Views/LiquidGlass.xaml`
- Modify: `Views/WindowFrame.xaml`、`Views/MainView.xaml`
- Modify: `Views/SettingsView.xaml`、`Views/UpdateView.xaml`、`Views/DetailsView.xaml`

- [ ] **Step 1: 校准浅色玻璃层次与交互对比**

微调浅色 palette 中标题栏、主背景、卡片、输入区、筛选选中、记录 hover/pin、状态栏、玻璃高光、边缘和青绿色强调状态；保持所有正文与按钮文字在普通、hover、focus、disabled 状态可读。不要改数据模板绑定或图片缩略图逻辑。

- [ ] **Step 2: 同步深色调色板新增资源及主题状态**

逐项核对 `LightGlass.xaml` 与 `LiquidGlass.xaml` 所有 `x:Key` 完全一致；对比焦点环、错误、选中、悬停、压下、置顶和关闭按钮状态，确保主题切换不缺失资源。

- [ ] **Step 3: 为主视图细化玻璃面板与空状态视觉**

只调整 `MainView.xaml` 的视觉属性（圆角、边框、留白、卡片层次和空状态）；保留 ListBox 虚拟化、`x:Name`、命令绑定和 `DataTemplate`。用当前截图的 340×500 作为紧凑基准，并检查普通与最大化布局。

- [ ] **Step 4: 完善覆盖层标题、焦点与键盘关闭语义**

三个 overlay 页面提供一致标题和关闭按钮；遮罩期间主表面不可聚焦，Tab 循环留在当前页，Esc 仅关闭当前栈顶页。关闭后恢复触发覆盖层前的有效焦点；若元素已卸载则回退到搜索框/历史列表。

### Task 9: 固化未来开发约定并做完整验证

**Files:**
- Create: `docs/DEVELOPMENT.md`
- Modify: `Tests/ClipboardManager.Tests/ThemeAndOverlayTests.cs`
- Verify: all changed app/XAML files

- [ ] **Step 1: 文档说明新增辅助页走单窗口覆盖层**

在 `docs/DEVELOPMENT.md` 说明 `OverlayView`、`WindowOverlayHost.ShowAsync`、页面栈返回、`OnDismissed` 资源清理、动态主题资源和键盘焦点约束；明确设置/更新/详情不得重新建 `Window`，系统确认消息框是保留的既有例外。

- [ ] **Step 2: 加入主题资源覆盖检查**

为 Light/Dark palette 资源键相等之外，再从所有 `Views/*.xaml` 收集 `{DynamicResource Key}` 并断言这些键在两套 palette 或公共 `App.xaml` 内定义。将四个 overlay 资源名加入检查，保证遗漏键在测试期报错。

- [ ] **Step 3: 执行完整 Release 构建和测试**

运行：

```powershell
dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --configuration Release
dotnet build ClipboardManager.csproj --configuration Release
git diff --check
```

预期：测试与应用均以 0 退出码完成，输出 `Build succeeded` / `Passed`，差异空白检查无输出。若本机没有 .NET SDK，使用仓库既有 `build.ps1` 验证主应用，并清楚记录测试项目未能运行的限制，不报告未执行测试通过。

- [ ] **Step 4: 完成运行时手工矩阵并记录结果**

记录以下项目逐项通过/失败，不得用构建结果替代运行检查：首次启动浅色、旧 Dark/Light/System 配置恢复、系统主题切换、根覆盖层遮罩和 Esc、设置打开临时扩展与原位置恢复、设置取消/保存、设置→更新→返回保留编辑值、更新检查/取消、文本/图片详情与复制缩放、遮罩期间主界面不能点击、托盘退出时异步取消且程序正常关闭。

## 计划自检

- 设计覆盖：浅色首次默认/既有偏好保留由 Task 1–2；两主题视觉键同步由 Task 2/8/9；单窗口覆盖层与输入遮罩由 Task 3–4；设置扩展/恢复与设置取消回滚由 Task 5；设置嵌套更新与安装退出由 Task 6；详情、复制和上下文恢复由 Task 7；可访问性/焦点与后续约定由 Task 8–9；构建和手工矩阵由 Task 9。
- 页面完成状态与主题、窗口几何、嵌套页栈均有独立测试或明确的 Windows 手工验证步骤。
- 没有未定义的类型签名：`OverlayView` 提供 `CloseRequest`、`OnDismissed`、`Complete`；`OverlayPageStack<T>` 提供 `Count/Current/Push/Pop/Clear`；`OverlayWindowGeometry.Fit` 接收 `Rect/Size/margin`；`WindowOverlayHost.ShowAsync` 返回 `Task<bool?>`。
- 本计划不包含独立设置/更新/详情窗口基类；页面只在主窗口的覆盖层宿主中创建。
