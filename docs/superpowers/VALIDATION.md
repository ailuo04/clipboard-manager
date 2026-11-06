# 覆盖层 Task 9 验证记录

日期：2026-11-06

## 手工运行矩阵

本地 v0.0.6 便携包已生成；但本次 GUI 验收无法启动窗口：Windows 桌面自动化返回“桌面合成已禁用”（0x80263001），随后确认本地验收进程未运行。因此以下项目统一标记为 **Not manually verified**；源码和自动化测试覆盖情况单独列出，不把这些检查当作运行时通过。

| 项目 | 源码/自动化测试覆盖 | 手工运行结果 |
| --- | --- | --- |
| 首次启动使用浅色主题 | `ThemePreferencePolicy` 缺省/无效值及保存 JSON 用例；`ThemeManager` 缺少文件时解析为浅色 | **Not manually verified** — desktop composition disabled; app window not launched |
| 已保存 Dark / Light / System 偏好恢复 | 保存主题 JSON 的 Light、Dark、System 用例和偏好解析用例 | **Not manually verified** — desktop composition disabled; app window not launched |
| 跟随系统时切换系统主题 | `ThemeManager` 监听系统偏好变化，并只在偏好为 System 时重新应用主题；无系统设置交互测试 | **Not manually verified** — desktop composition disabled; app window not launched |
| 遮罩显示与 Esc 关闭 | `WindowFramePlacesCollapsedOverlayAboveNamedMainSurface`、宿主遮罩/输入状态测试；设置与更新 Esc 测试 | **Not manually verified** — desktop composition disabled; app window not launched |
| 设置打开时窗口扩展并在关闭后恢复 | 宿主窗口状态/尺寸/位置恢复测试及几何策略测试；未用真实显示器手工观察设置页扩展 | **Not manually verified** — desktop composition disabled; app window not launched |
| 设置取消与保存 | `SettingsOverlayTests.CancelAndEscapeDismissalRestoreTheOpenedTheme`、`SuccessfulSaveReturnsCloneAndDoesNotRollBackPreviewedTheme` 及输入校验测试 | **Not manually verified** — desktop composition disabled; app window not launched |
| 设置 → 更新 → 返回保留未保存编辑 | `UpdateOverlayTests.SettingsUpdateCallbackOpensNestedOverlayAndPreservesUnsavedValues` 与宿主嵌套页返回测试 | **Not manually verified** — desktop composition disabled; app window not launched |
| 更新检查与取消 | 更新页首次挂载检查、Esc/关闭取消令牌、延迟检查收尾及下载取消路径测试 | **Not manually verified** — desktop composition disabled; app window not launched |
| 文本/图片详情、复制与缩放 | `DetailsOverlayTests` 覆盖文本/图片复制、图片加载、适应视口、缩放和滚动条；无真实剪贴板/视觉手工检查 | **Not manually verified** — desktop composition disabled; app window not launched |
| 遮罩期间阻止背景输入 | 宿主测试断言主表面 `IsEnabled` 与 `IsHitTestVisible` 关闭，并检查遮罩层位置；无物理鼠标点击测试 | **Not manually verified** — desktop composition disabled; app window not launched |
| 托盘退出时取消异步页面并关闭 | `MainWindow.OnClosing` 调用覆盖层关闭，`MainViewModel.ShutdownAsync` 取消生命周期并等待任务；没有专门的托盘退出 UI 自动化测试 | **Not manually verified** — desktop composition disabled; app window not launched |

## 自动化验证

以下为实际自动化验证结果；构建/测试结果不代表上方手工矩阵通过。

- `dotnet test Tests/ClipboardManager.Tests/ClipboardManager.Tests.csproj --configuration Release`：通过，85/85 测试通过，0 失败、0 跳过。
- `dotnet build ClipboardManager.csproj --configuration Release`：成功，0 警告、0 错误。
- `git diff --check`：通过，无输出。
- XAML/主题资源检查通过：XAML 均可解析；动态资源键均存在；Light/Dark 调色板键集合匹配，覆盖层所需资源齐全。
- TDD 自检：实际资源清单覆盖测试先通过；随后缺失键自检在空实现下按预期失败（期望发现 `DefinitelyMissingResource`，实际为空），实现纯测试 helper 后，两个针对性测试通过。