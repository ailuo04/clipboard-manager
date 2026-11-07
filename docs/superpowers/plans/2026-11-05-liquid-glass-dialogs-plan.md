# 珍珠浅色液态玻璃与模态弹窗实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** 将剪贴板管理器统一为珍珠浅色液态玻璃视觉，并让设置、更新、详情及未来辅助页面通过共享的居中模态弹窗框架打开。

**Architecture:** 保留现有动态资源主题系统和 WPF 独立窗口模型。增加轻/深色主题策略测试、一个 GlassDialogWindow 基类和 DialogFrame.xaml；基类统一 owner、居中、无边框标题栏、DWM 背景和内容承载，现有辅助页保留业务逻辑并迁移到该基类。未保存主题时默认浅色，已保存的深色/系统偏好不变。

**Tech Stack:** .NET 8 WPF、C#、XAML、Windows DWM WindowChrome/DwmSetWindowAttribute、现有 ViewLoader 和动态 ResourceDictionary。

---

## 文件地图

本仓库当前没有配置 Git 提交者身份；执行提交步骤前需由用户配置仓库级或全局 Git 身份。不要替用户猜测或写入作者姓名/邮箱。

- Create: Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj、Tests/ClipboardManager.Tests/ThemePolicyTests.cs — 验证主题回退与调色板资源键一致性。
- Create: Properties/AssemblyInfo.cs — 向测试程序集开放内部主题策略方法。
- Create: GlassDialogWindow.cs — 可复用的 owner-centered 模态玻璃窗口基类。
- Create: Views/DialogFrame.xaml — 弹窗标题栏、关闭按钮、玻璃外框和内容承载区。
- Modify: ClipboardManager.csproj — 排除嵌套测试源码，避免根项目编译测试代码。
- Create: Properties/AssemblyInfo.cs — 仅向测试程序集开放内部策略方法。
- Modify: Services/ThemeManager.cs — 主题回退改浅色；抽取共享 DWM 背景应用方法。
- Modify: Views/LightGlass.xaml、Views/LiquidGlass.xaml — 加入一致的弹窗资源键并细化珍珠浅色层次，保持深色主题键集合对应。
- Modify: App.xaml — 新增可复用的弹窗标题栏和关闭按钮样式。
- Modify: MainWindow.cs — 改用共享 DWM 背景方法，保留已有主窗体 WindowChrome 和行为。
- Modify: DetailsWindow.cs、SettingsWindow.cs、UpdateWindow.cs — 继承共享基类，保持数据、复制、校验、更新和取消逻辑。
- Modify: Views/MainView.xaml、Views/WindowFrame.xaml、Views/SettingsView.xaml、Views/DetailsView.xaml、Views/UpdateView.xaml — 统一主窗口与弹窗玻璃表面和空间层级，保留业务绑定和 x:Name。
- Create: docs/DEVELOPMENT.md — 说明新增辅助页面必须以 owner 居中模态弹窗打开。

---

### Task 1: 先建立主题策略测试（RED）

Files:
- Create: Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj
- Create: Tests/ClipboardManager.Tests/ThemePolicyTests.cs
- Modify: ClipboardManager.csproj
- Modify: Services/ThemeManager.cs

- [ ] Step 1: 建立 Windows WPF 测试项目

运行：

    dotnet new xunit --framework net8.0 --output Tests/ClipboardManager.Tests

将生成的测试项目属性调整为下面内容并保留模板生成的 PackageReference：

    <PropertyGroup>
      <TargetFramework>net8.0-windows</TargetFramework>
      <UseWPF>true</UseWPF>
      <Nullable>enable</Nullable>
      <ImplicitUsings>enable</ImplicitUsings>
    </PropertyGroup>
    <ItemGroup>
      <ProjectReference Include="../../ClipboardManager.csproj" />
      <None Include="../../App.xaml" Link="Xaml/App.xaml" CopyToOutputDirectory="PreserveNewest" />
      <None Include="../../Views/*.xaml" Link="Xaml/Views/%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    </ItemGroup>

在根 ClipboardManager.csproj 的 ItemGroup 加入：

    <Compile Remove="Tests/**/*.cs" />

创建 Properties/AssemblyInfo.cs：

    using System.Runtime.CompilerServices;
    [assembly: InternalsVisibleTo("ClipboardManager.Tests")]

这样根 WPF 项目不会编译嵌套测试源码，测试程序集可调用内部的纯主题策略方法。

- [ ] Step 2: 增加当前默认行为的可测入口

在 Services/ThemeManager.cs 增加此内部纯函数，RED 阶段先保持当前默认深色：

    internal static AppTheme ResolvePreference(AppTheme? savedPreference) =>
        savedPreference is { } value && Enum.IsDefined(value) ? value : AppTheme.Dark;

- [ ] Step 3: 编写主题回退和资源字典测试

创建 Tests/ClipboardManager.Tests/ThemePolicyTests.cs：

    using System.Text.RegularExpressions;
    using System.Xml.Linq;
    using ClipboardManager.Models;
    using ClipboardManager.Services;
    using Xunit;

    namespace ClipboardManager.Tests;

    public sealed class ThemePolicyTests
    {
        [Theory]
        [InlineData(null, AppTheme.Light)]
        [InlineData(AppTheme.Light, AppTheme.Light)]
        [InlineData(AppTheme.Dark, AppTheme.Dark)]
        [InlineData(AppTheme.System, AppTheme.System)]
        [InlineData((AppTheme)99, AppTheme.Light)]
        public void ResolvePreferenceUsesLightOnlyForMissingOrInvalidValue(AppTheme? saved, AppTheme expected)
        {
            Assert.Equal(expected, ThemeManager.ResolvePreference(saved));
        }

        [Fact]
        public void LightAndDarkPalettesExposeTheSameResourceKeys()
        {
            string views = Path.Combine(AppContext.BaseDirectory, "Xaml", "Views");
            string light = Path.Combine(views, "LightGlass.xaml");
            string dark = Path.Combine(views, "LiquidGlass.xaml");
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            static string[] Keys(string path, XNamespace ns) => XDocument.Load(path).Root!.DescendantsAndSelf()
                .Attributes(ns + "Key").Select(attribute => attribute.Value)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();

            Assert.Equal(Keys(light, xaml), Keys(dark, xaml));
        }

        [Fact]
        public void EveryDynamicResourceReferenceHasAThemeValue()
        {
            string xamlRoot = Path.Combine(AppContext.BaseDirectory, "Xaml");
            string light = Path.Combine(xamlRoot, "Views", "LightGlass.xaml");
            var keys = XDocument.Load(light).Root!.DescendantsAndSelf()
                .Attributes((XNamespace)"http://schemas.microsoft.com/winfx/2006/xaml" + "Key")
                .Select(attribute => attribute.Value).ToHashSet(StringComparer.Ordinal);
            var pattern = new Regex(@"\{DynamicResource\s+([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

            foreach (string file in Directory.EnumerateFiles(xamlRoot, "*.xaml", SearchOption.AllDirectories))
            {
                XDocument.Load(file);
                foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                {
                    string key = match.Groups[1].Value;
                    Assert.True(key == "AppIcon" || keys.Contains(key), $"{Path.GetFileName(file)} uses missing theme key {key}.");
                }
            }
        }
    }

- [ ] Step 4: 运行测试并确认默认主题用例失败

运行：

    dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --filter FullyQualifiedName~ThemePolicyTests

预期：资源键一致和已保存 Light/Dark/System 用例通过；null 与非法枚举用例失败，因为 RED 阶段 resolver 仍返回 Dark。若项目恢复或编译失败，先修正测试项目配置。

- [ ] Step 5: 提交测试基线

    git add ClipboardManager.csproj Properties/AssemblyInfo.cs Services/ThemeManager.cs Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj Tests/ClipboardManager.Tests/ThemePolicyTests.cs docs/superpowers/specs/2026-11-05-liquid-glass-dialogs-design.md
    git commit -m "test: cover theme fallback and palette parity"

---

### Task 2: 将未配置主题默认设为珍珠浅色（GREEN）

Files:
- Modify: Services/ThemeManager.cs

- [ ] Step 1: 让所有默认与非法值路径使用被测策略

将 ResolvePreference 改为：

    internal static AppTheme ResolvePreference(AppTheme? savedPreference) =>
        savedPreference is { } value && Enum.IsDefined(value) ? value : AppTheme.Light;

在 Apply() 中先执行 preference = ResolvePreference(preference)。在 ReadSavedPreference() 中，缺少文件和反序列化异常时用 AppLogger.LogException("读取主题设置失败，沿用浅色主题", exception) 记录并返回 ResolvePreference(null)，成功读取后返回 ResolvePreference(document?.Theme)。将 ThemeDocument.Theme 初始值从 Dark 改为 Light，确保缺少 JSON 字段时也默认浅色。将 _preference 初始值改为 Light。保留所有合法保存值及系统主题监听逻辑。

- [ ] Step 2: 运行主题测试和应用构建

    dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --filter FullyQualifiedName~ThemePolicyTests
    dotnet build ClipboardManager.csproj --no-restore

预期：所有主题测试通过，主程序构建成功。

- [ ] Step 3: 提交默认主题变更

    git add Services/ThemeManager.cs
    git commit -m "fix: default new installs to light glass theme"

---

### Task 3: 建立共享液态玻璃弹窗外壳

Files:
- Create: GlassDialogWindow.cs
- Create: Views/DialogFrame.xaml
- Modify: App.xaml
- Modify: Services/ThemeManager.cs
- Modify: Views/LightGlass.xaml
- Modify: Views/LiquidGlass.xaml
- Modify: MainWindow.cs

- [ ] Step 1: 将 DWM 背景逻辑抽到 ThemeManager

新增 internal static void ApplyBackdrop(Window window)。它获取 new WindowInteropHelper(window).Handle；句柄为零时直接返回；依次设置 DwmwaSystemBackdropType= DwmsbtTransientWindow、DwmwaWindowCornerPreference=2、DwmwaUseImmersiveDarkMode=_isDark ? 1 : 0，大小参数传 Marshal.SizeOf<int>()；异常用 AppLogger.LogException("设置窗口玻璃背景失败", exception) 记录。把 MainWindow.ApplyWindowBackdrop() 方法体改为 ThemeManager.ApplyBackdrop(this)。不要改变主窗口 WindowChrome、标题栏、尺寸调整、置顶及关闭行为。

- [ ] Step 2: 创建共享基类 GlassDialogWindow.cs

新文件完整内容：

    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Media;
    using System.Windows.Shell;
    using ClipboardManager.Services;
    using ClipboardManager.Utilities;

    namespace ClipboardManager;

    public abstract class GlassDialogWindow : Window
    {
        private readonly Grid _frame;

        protected GlassDialogWindow(Window owner, string title, double width, double height, double minWidth, double minHeight)
        {
            ArgumentNullException.ThrowIfNull(owner);
            SetResourceReference(StyleProperty, typeof(Window));
            Owner = owner;
            Title = title;
            Width = width;
            Height = height;
            MinWidth = minWidth;
            MinHeight = minHeight;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Background = Brushes.Transparent;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                UseAeroCaptionButtons = false,
                CaptionHeight = 42,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(-1),
                CornerRadius = new CornerRadius(18)
            });
            _frame = ViewLoader.Load<Grid>("Views/DialogFrame.xaml");
            Content = _frame;
            ThemeManager.RegisterWindow(this);
            SourceInitialized += (_, _) => ThemeManager.ApplyBackdrop(this);
        }

        protected void SetDialogContent(UIElement content)
        {
            ((ContentControl)_frame.FindName("DialogContent")).Content = content;
        }
    }

基类统一窗口外壳，不自行调用 ShowDialog()；调用方继续指定当前 owner 并决定模态生命周期，这让从设置页打开更新页时仍由设置窗拥有。

- [ ] Step 3: 创建 Views/DialogFrame.xaml

创建 Views/DialogFrame.xaml，使用下列完整骨架；关闭按钮用 WPF 的 IsCancel 属性关闭模态窗，不另加 Click 处理器：

    <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
          xmlns:shell="clr-namespace:System.Windows.Shell;assembly=PresentationFramework">
        <Border Background="{DynamicResource DialogSurfaceBrush}"
                BorderBrush="{DynamicResource GlassEdgeBrush}" BorderThickness="1" CornerRadius="18">
            <Border.Effect>
                <DropShadowEffect Color="{DynamicResource CardShadowColor}" BlurRadius="24"
                                  ShadowDepth="5" Direction="270" Opacity="0.24" />
            </Border.Effect>
            <Grid ClipToBounds="True">
                <Grid.RowDefinitions>
                    <RowDefinition Height="42" />
                    <RowDefinition Height="*" />
                </Grid.RowDefinitions>
                <Border Grid.Row="0" Background="{DynamicResource DialogHeaderBrush}"
                        BorderBrush="{DynamicResource GlassEdgeBrush}" BorderThickness="0,0,0,1"
                        CornerRadius="17,17,0,0">
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <StackPanel Orientation="Horizontal" Margin="14,0,8,0"
                                    VerticalAlignment="Center" IsHitTestVisible="False">
                            <Image Source="{Binding Icon, RelativeSource={RelativeSource AncestorType=Window}}"
                                   Width="18" Height="18" Margin="0,0,8,0" />
                            <TextBlock Text="{Binding Title, RelativeSource={RelativeSource AncestorType=Window}}"
                                       FontSize="12" FontWeight="SemiBold"
                                       Foreground="{DynamicResource TextPrimaryBrush}" />
                        </StackPanel>
                        <Button x:Name="DialogCloseButton" Grid.Column="1"
                                Style="{StaticResource DialogCloseButton}" Margin="0,0,7,0"
                                IsCancel="True" shell:WindowChrome.IsHitTestVisibleInChrome="True"
                                ToolTip="关闭" AutomationProperties.Name="关闭">
                            <TextBlock Style="{StaticResource Icon}" Text="&#xE8BB;" FontSize="10" />
                        </Button>
                    </Grid>
                </Border>
                <ContentControl x:Name="DialogContent" Grid.Row="1"
                                Foreground="{DynamicResource TextPrimaryBrush}" />
            </Grid>
        </Border>
    </Grid>

- [ ] Step 4: 在 App.xaml 加入全局标题栏样式

新增 DialogCaptionButton（32x30、透明底、圆角 Chrome 边框和居中 ContentPresenter；悬停、按下、键盘焦点分别用 CaptionHoverBrush、CaptionPressedBrush、AccentBrush）和基于它的 DialogCloseButton（悬停/按下用 CloseHoverBrush、ClosePressedBrush 并切白色前景）。所有颜色通过 DynamicResource 获取。

- [ ] Step 5: 为 LightGlass.xaml 和 LiquidGlass.xaml 补全匹配的弹窗资源

浅色字典加入：

    <LinearGradientBrush x:Key="DialogSurfaceBrush" StartPoint="0,0" EndPoint="1,1">
      <GradientStop Color="#F7FFFFFF" Offset="0" />
      <GradientStop Color="#E6F0F4F8" Offset="1" />
    </LinearGradientBrush>
    <LinearGradientBrush x:Key="DialogHeaderBrush" StartPoint="0,0" EndPoint="1,1">
      <GradientStop Color="#E6FFFFFF" Offset="0" />
      <GradientStop Color="#66DCEBFF" Offset="1" />
    </LinearGradientBrush>

深色字典加入相同键名；DialogSurfaceBrush 两个色值为 #E61F2832 与 #D91B232C，DialogHeaderBrush 两个色值为 #553C4A5A 与 #22232D38。继续保持两字典全部 x:Key 集合相同，并用新增测试确认动态资源键齐全。

- [ ] Step 6: 测试共享外壳并构建

    dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --filter FullyQualifiedName~ThemePolicyTests
    dotnet build ClipboardManager.csproj --no-restore

预期：调色板资源键测试通过；应用构建成功；新增 DialogFrame.xaml 可被 XML 解析。

- [ ] Step 7: 提交共享弹窗外壳

    git add GlassDialogWindow.cs Views/DialogFrame.xaml App.xaml Services/ThemeManager.cs MainWindow.cs Views/LightGlass.xaml Views/LiquidGlass.xaml Tests/ClipboardManager.Tests/ThemePolicyTests.cs
    git commit -m "feat: add shared liquid glass dialog chrome"

---

### Task 4: 迁移设置、详情和更新窗口

Files:
- Modify: DetailsWindow.cs
- Modify: SettingsWindow.cs
- Modify: UpdateWindow.cs
- Verify: ViewModels/MainViewModel.cs

- [ ] Step 1: 迁移 DetailsWindow

类型改为 public sealed class DetailsWindow : GlassDialogWindow，构造函数使用 initializer：

    public DetailsWindow(Window owner, RecordViewModel item, ClipboardReader clipboard, IntPtr clipboardOwner)
        : base(owner, item.IsImage ? "图片详情" : "文本详情", 800, 660, 500, 420)

删除重复的 Window 样式引用、Owner、窗口尺寸、CenterOwner 和 ThemeManager.RegisterWindow 调用；将 Content = view 改为 SetDialogContent(view)。保留图片异步加载、取消令牌、复制与错误日志、缩放及 Closed 清理逻辑。

- [ ] Step 2: 迁移 SettingsWindow

类型改为 public sealed class SettingsWindow : GlassDialogWindow，使用 : base(owner, "设置", 510, 660, 460, 510)。在基类构造完成后设置 _openedTheme = ThemeManager.Preference。用 SetDialogContent(view) 替换 Content = view，删除重复窗口/主题注册设置。保留 FindName 依赖、实时主题预览、取消时回滚、输入验证、保存语义。

- [ ] Step 3: 迁移 UpdateWindow

类型改为 public sealed class UpdateWindow : GlassDialogWindow，使用 : base(owner, "应用更新", 510, 510, 430, 400)。用 SetDialogContent(view) 替换 Content = view，删除重复窗口/主题注册设置。保留自动检查、关闭取消、凭据保存/删除、下载进度和安装确认。

- [ ] Step 4: 审核调用方模态与 owner 链路

检查 ViewModels/MainViewModel.cs；保留详情、设置、更新的 ShowDialog() 调用。从设置页打开更新仍把当前 SettingsWindow 传作 UpdateWindow owner。不要将这些调用改成 Show()。

- [ ] Step 5: 测试、构建并审核调用点

    dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --filter FullyQualifiedName~ThemePolicyTests
    dotnet build ClipboardManager.csproj --no-restore
    rg -n "class (DetailsWindow|SettingsWindow|UpdateWindow)|ShowDialog\(\)|\.Show\(\)|SetDialogContent" DetailsWindow.cs SettingsWindow.cs UpdateWindow.cs ViewModels/MainViewModel.cs

预期：三个窗口都继承共享基类、所有视图使用 SetDialogContent、调用点仍是 ShowDialog()，设置→更新 owner 链完整。

- [ ] Step 6: 提交窗口迁移

    git add DetailsWindow.cs SettingsWindow.cs UpdateWindow.cs
    git commit -m "refactor: open auxiliary pages in shared modal windows"

---

### Task 5: 统一主窗口与弹窗页面的玻璃层级

Files:
- Modify: Views/MainView.xaml
- Modify: Views/WindowFrame.xaml
- Modify: Views/SettingsView.xaml
- Modify: Views/DetailsView.xaml
- Modify: Views/UpdateView.xaml
- Modify: Views/LightGlass.xaml
- Modify: Views/LiquidGlass.xaml

- [ ] Step 1: 精修主历史窗口和标题栏

审核搜索框、筛选器、历史容器、记录卡片、操作区、空状态、状态栏与自定义标题栏。使用统一动态资源表示玻璃表面、边缘、高光、置顶、选中、悬停、状态和文字；把 MainView.xaml 中硬编码阴影色改为调色板的 CardShadowColor。保留命令、绑定、x:Name、键盘交互和 ListBox 虚拟化。

- [ ] Step 2: 精修设置视图

保留所有布局行与控件名。为主题选择、数据/位置区域、更新行和底部操作区建立一致的 SurfaceBrush/SoftSurfaceBrush + GlassEdgeBrush 层次。验证信息继续使用 ErrorTextBrush；不改变任何设置字段或主题预览行为。

- [ ] Step 3: 精修详情视图

保留 CopyButton、FullImage、ImageScroll、FitCheckBox、ZoomSlider、ZoomLabel、StatusLabel、CloseButton 等原有 x:Name。文本和图片预览分别使用 SurfaceBrush、ImageWellBackgroundBrush、ImageWellBorderBrush 与 GlassEdgeBrush；保证原图滚动、适应窗口和缩放仍能用。

- [ ] Step 4: 精修更新视图

保留所有现有控件名与事件目标。统一更新状态、私有仓库 Expander 和底部按钮层级；保持 PasswordBox 遮蔽令牌，不把令牌加入日志或视觉预览；确保当前最小窗口尺寸下仍可操作取消和更新。

- [ ] Step 5: 运行测试与构建

    dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --filter FullyQualifiedName~ThemePolicyTests
    dotnet build ClipboardManager.csproj --no-restore

预期：浅/深资源集合相同、动态资源全部有定义、构建成功。若任何 x:Name 变化导致 code-behind 查找失效，恢复原控件名，不为视觉改动扩大代码-behind 范围。

- [ ] Step 6: 提交整体视觉调整

    git add Views/MainView.xaml Views/WindowFrame.xaml Views/SettingsView.xaml Views/DetailsView.xaml Views/UpdateView.xaml Views/LightGlass.xaml Views/LiquidGlass.xaml
    git commit -m "style: unify pearl glass surfaces across the app"

---

### Task 6: 固化未来页面的模态窗口开发约定

Files:
- Create: docs/DEVELOPMENT.md

- [ ] Step 1: 增加新辅助页开发说明

创建 docs/DEVELOPMENT.md 并加入中文小节：新页面继承 GlassDialogWindow，构造函数传入立即 owner/标题/尺寸/最小尺寸，以 ViewLoader 加载 XAML 并用 SetDialogContent(view) 承载，调用方使用 ShowDialog()；不得创建无 owner 的 modeless Show() 页面。交互控件应保留 AutomationProperties.Name 与默认/取消键盘行为。加入可复制示例：

    public sealed class ExampleWindow : GlassDialogWindow
    {
        public ExampleWindow(Window owner)
            : base(owner, "示例", 520, 420, 420, 320)
        {
            var view = ViewLoader.Load<Grid>("Views/ExampleView.xaml");
            SetDialogContent(view);
        }
    }

    new ExampleWindow(this).ShowDialog();

明确现有设置、更新和详情窗口本来已经使用模态方式，本次统一的是外观和后续开发模式。

- [ ] Step 2: 检查文档 API 名称并提交

    rg -n "GlassDialogWindow|SetDialogContent|ShowDialog\(\)" docs/DEVELOPMENT.md GlassDialogWindow.cs
    git add docs/DEVELOPMENT.md
    git commit -m "docs: document modal glass window pattern"

预期：文档引用的类、构造函数、方法名与实现一致。

---

### Task 7: 端到端验证

Files:
- Verify: Tests/ClipboardManager.Tests/ThemePolicyTests.cs
- Verify: all modified C# and XAML files listed above

- [ ] Step 1: 执行干净测试和构建

    dotnet clean ClipboardManager.csproj
    dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj
    dotnet build ClipboardManager.csproj

预期：全部测试通过，构建显示 Build succeeded；排查新增 XAML、nullable、资源键或主题回退警告。

- [ ] Step 2: 运行原生窗口冒烟矩阵

运行 dotnet run --project ClipboardManager.csproj 并手动验证：

1. 缺少主题偏好时为珍珠浅色；已保存的 Dark、Light、System 分别保持生效。
2. 设置窗居中且模态，主窗体不能被点击；关闭后焦点回到主窗体。
3. 主题预览同步更新主窗与打开的弹窗；取消恢复旧主题，保存保留所选主题。
4. 文本详情和图片详情使用同一弹窗外壳；复制、适应窗口和缩放正常。
5. 更新页检查、关闭和更新控件在最小尺寸时仍可见可操作。
6. 从设置弹窗进入更新页时，更新页 owner 是设置窗；关闭后依次返回设置和主窗口。
7. Tab、Enter、Esc、标题栏关闭、窗口缩放与键盘焦点边框在浅/深主题下工作。

- [ ] Step 3: 审核最终 diff

    git diff --check
    git status --short
    git log -6 --oneline

预期：没有空白错误；只有计划范围内的代码、测试和文档；没有用户历史、日志、令牌或临时文件被暂存。仅汇报实际执行并观察到的构建和 UI 检查结果。

---

## 计划自检

- 已覆盖批准设计中的珍珠浅色视觉、未配置时浅色默认、已有 Dark/System 偏好保留、设置/详情/更新弹窗统一及未来页面约定。
- 有明确的 RED/GREEN 主题策略测试、资源字典一致性与动态资源检查、应用构建及原生 UI 冒烟步骤。
- 保留现有 view-model 命令、异步任务、取消流程、主题动态切换和代码中 FindName 依赖的控件名。
- 不包含单窗口导航、主题编辑器、新业务功能或替换原生确认框。
