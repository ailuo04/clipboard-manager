# 开发约定

## 辅助页面使用模态液态玻璃窗口

设置、更新和详情页面原本就通过 `ShowDialog()` 以模态方式打开；本次改造统一了这些窗口的外观与共享外壳，并为后续辅助页面明确相同的开发模式。

新增辅助页面时，窗口应继承 `GlassDialogWindow`，并在构造函数中立即传入当前所属窗口、标题、初始尺寸和最小尺寸。通过 `ViewLoader` 加载对应的 XAML 视图，再用 `SetDialogContent(view)` 放入共享弹窗内容区。由调用方使用 `ShowDialog()` 启动窗口：

```csharp
using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Utilities;

namespace ClipboardManager;

public sealed class ExampleWindow : GlassDialogWindow
{
    public ExampleWindow(Window owner)
        : base(owner, "示例", 520, 420, 420, 320)
    {
        var view = ViewLoader.Load<Grid>("Views/ExampleView.xaml");
        SetDialogContent(view);
    }
}

// 在所属窗口中打开
new ExampleWindow(this).ShowDialog();
```

调用时应传入当前页面的直接 owner；例如，从设置窗口打开更新窗口时，更新窗口的 owner 应是设置窗口，而不是跳回主窗口。不要为辅助页面创建无 owner 的普通窗口，也不要用非模态 `Show()` 打开页面，以免破坏居中、焦点和模态链路。

交互控件应提供清晰的 `AutomationProperties.Name`。需要默认或取消操作的页面应保留恰当的 Enter/Esc 键盘行为（例如 `IsDefault`、`IsCancel`），并确保 Tab 导航、关闭按钮和窗口缩放仍可用。