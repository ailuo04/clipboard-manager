using System.Windows.Input;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ClipboardManager;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.ViewModels;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class WidgetTests(SettingsOverlayWpfFixture wpf)
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainViewHasACapsuleAndAPanelSwitchedByTheCollapsedState()
    {
        XElement root = LoadView("MainView.xaml");
        XElement capsule = Named(root, "Capsule");
        Assert.Equal("240", (string?)capsule.Attribute("Width"));
        Assert.Equal("40", (string?)capsule.Attribute("Height"));
        Assert.Equal("Left", (string?)capsule.Attribute("HorizontalAlignment"));
        Assert.Equal("Top", (string?)capsule.Attribute("VerticalAlignment"));
        XElement panel = Named(root, "Panel");

        Assert.Equal("{Binding IsWidgetCollapsed, Converter={StaticResource BoolVisibility}}", (string?)capsule.Attribute("Visibility"));
        Assert.Contains(capsule.Descendants(Presentation + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding CapsuleText}");
        Assert.Contains(capsule.Descendants(Presentation + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding RecordCount}");
        XElement trigger = panel.Descendants(Presentation + "DataTrigger")
            .First(element => (string?)element.Attribute("Binding") == "{Binding IsWidgetCollapsed}");
        Assert.Equal("True", (string?)trigger.Attribute("Value"));
        // The panel keeps everything the keyboard handling in MainWindow relies on.
        Assert.Equal(panel, Named(root, "HistoryList").Ancestors().First(element => element == panel));
        Assert.Equal(panel, Named(root, "SearchBox").Ancestors().First(element => element == panel));
        Assert.Equal(panel, Named(root, "DragHandle").Ancestors().First(element => element == panel));
        Assert.Equal(panel, Named(root, "MenuButton").Ancestors().First(element => element == panel));
    }

    [Fact]
    public void CapsuleLatestAreaCopiesAndArrowOpensHistoryBelowWithSettingsAccess()
    {
        XElement root = LoadView("MainView.xaml");
        XElement capsule = Named(root, "Capsule");
        XElement latest = Named(root, "LatestCopyButton");
        Assert.Equal("{Binding LatestRecord.CopyCommand}", (string?)latest.Attribute("Command"));
        Assert.Contains(capsule.Descendants(Presentation + "TextBlock"), text => (string?)text.Attribute("Text") == "{Binding CapsuleText}");
        Assert.NotNull(Named(root, "CapsuleDragHandle"));
        XElement historyToggle = Named(root, "HistoryToggleButton");
        Assert.Equal(Presentation + "ToggleButton", historyToggle.Name);
        Assert.Equal("{StaticResource PinCaptionButton}", (string?)historyToggle.Attribute("Style"));
Assert.Equal("0,0,6,0", (string?)historyToggle.Attribute("Margin"));

        XElement popup = Named(root, "HistoryPopup");
        Assert.Equal("Bottom", (string?)popup.Attribute("Placement"));
        Assert.Equal("{Binding ElementName=Capsule}", (string?)popup.Attribute("PlacementTarget"));
        XElement history = Named(root, "HistoryRecords");
        Assert.Equal("{Binding HistoryRecords}", (string?)history.Attribute("ItemsSource"));
        Assert.Contains(history.Descendants(Presentation + "Button"), button => (string?)button.Attribute("Command") == "{Binding CopyCommand}");
        Assert.Equal("{Binding SettingsCommand}", (string?)Named(root, "SettingsButton").Attribute("Command"));
        XElement moreButton = Named(root, "MoreButton");
        Assert.NotNull(moreButton);
    }



    [Fact]
    public void ClickingInsideHistoryPopupDoesNotDismissItBeforeMoreMenuClick()
    {
        var policy = typeof(MainWindow).GetMethod("ShouldCloseHistoryPopupOnMouseDown",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(policy);

        Assert.False((bool)policy!.Invoke(null, [true, true, false])!);
        Assert.False((bool)policy.Invoke(null, [true, false, true])!);
        Assert.True((bool)policy.Invoke(null, [true, false, false])!);
        Assert.False((bool)policy.Invoke(null, [false, false, false])!);
    }

    [Fact]
    public void DetachedContextMenuPreventsHistoryPopupDismissalOnDeactivation()
    {
        var policy = typeof(MainWindow).GetMethod("ShouldCloseHistoryPopupOnDeactivation",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(policy);

        bool closeWhileMenuOpen = (bool)policy!.Invoke(null, [true, true, false])!;
        bool closeAfterClickingOutside = (bool)policy.Invoke(null, [true, false, false])!;

        Assert.False(closeWhileMenuOpen);
        Assert.True(closeAfterClickingOutside);
    }


    [Fact]
    public void MoreButtonTogglesIndependentMenuWithoutClosingHistoryPopup()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var view = new ClipboardManager.Views.MainView();
            var owner = new Window { Content = view, Width = 240, Height = 40, ShowInTaskbar = false };
            try
            {
                owner.Show();
                owner.UpdateLayout();
                var moreButton = (Button)typeof(ClipboardManager.Views.MainView)
                    .GetField("MoreButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(view)!;
                var historyPopup = (Popup)typeof(ClipboardManager.Views.MainView)
                    .GetField("HistoryPopup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(view)!;
                var moreMenu = (ContextMenu)typeof(ClipboardManager.Views.MainView)
                    .GetField("_menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(view)!;

                historyPopup.IsOpen = true;
                moreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, moreButton));
                Assert.True(moreMenu.IsOpen);
                Assert.True(view.IsMenuOpen);
                Assert.True(view.IsContextMenuOpen);
                Assert.True(historyPopup.IsOpen);

                moreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, moreButton));
                Assert.False(moreMenu.IsOpen);
                Assert.False(view.IsMenuOpen);
                Assert.True(historyPopup.IsOpen);
                historyPopup.IsOpen = false;
            }
            finally
            {
                owner.Close();
            }
        });
    }

    [Fact]
    public void MainPanelMenuButtonStillOpensItsContextMenu()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var view = new ClipboardManager.Views.MainView();
            var menuButton = (Button)typeof(ClipboardManager.Views.MainView)
                .GetField("MenuButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(view)!;
            var menu = (ContextMenu)typeof(ClipboardManager.Views.MainView)
                .GetField("_menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(view)!;

            menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, menuButton));

            Assert.True(view.IsContextMenuOpen);
            menu.IsOpen = false;
            Assert.False(menu.IsOpen);
        });
    }

    [Fact]
    public void MainViewLoadsHistoryPopupAndChevronRaisesToggleRequest()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var view = new ClipboardManager.Views.MainView();
            int requests = 0;
            view.HistoryToggleRequested += (_, _) => requests++;
            var toggle = (ToggleButton)typeof(ClipboardManager.Views.MainView)
                .GetField("HistoryToggleButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(view)!;
            var popup = (System.Windows.Controls.Primitives.Popup)typeof(ClipboardManager.Views.MainView)
                .GetField("HistoryPopup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(view)!;

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toggle));

            Assert.Equal(1, requests);
            Assert.Equal(PlacementMode.Bottom, popup.Placement);
        });
    }

    [Fact]
    public void HistoryPopupPrefersBelowAndFlipsAboveOnlyWhenItCannotFit()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var preferredSize = new Size(300, 320);

        CapsulePopupLayout below = CapsulePopupPositioning.Calculate(workArea, new Rect(1600, 80, 240, 40), preferredSize);
        CapsulePopupLayout above = CapsulePopupPositioning.Calculate(workArea, new Rect(1600, 1000, 240, 40), preferredSize);
        CapsulePopupLayout exactlyFits = CapsulePopupPositioning.Calculate(workArea, new Rect(1800, 716, 240, 40), preferredSize);
        CapsulePopupLayout missesByOne = CapsulePopupPositioning.Calculate(workArea, new Rect(1800, 717, 240, 40), preferredSize);

        Assert.Equal(PlacementMode.Bottom, below.Placement);
        Assert.Equal(PlacementMode.Top, above.Placement);
        Assert.Equal(PlacementMode.Bottom, exactlyFits.Placement);
        Assert.Equal(PlacementMode.Top, missesByOne.Placement);
        Assert.Equal(320, below.MaxHeight);
        Assert.Equal(320, above.MaxHeight);
        Assert.Equal(300, below.Width);
        Assert.Equal(300, above.Width);
        Assert.Equal(-180, exactlyFits.HorizontalOffset);
    }

    [Fact]
    public void HoverPreviewUsesAvailableSpaceToPlacePopupBesideItsTarget()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var view = new ClipboardManager.Views.MainView();
            var owner = new Window { Content = view, Width = 320, Height = 160, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
            owner.Left = SystemParameters.WorkArea.Left + 20;
            owner.Top = SystemParameters.WorkArea.Top + 120;
            var target = new Button { Width = 30, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            target.Style = (Style)view.FindResource("RecordCopySurface");
            var tooltip = new ToolTip { ContentTemplateSelector = ClipboardManager.Views.MainView.RecordHoverSelector };
            target.ToolTip = tooltip;
            view.Children.Add(target);

            try
            {
                owner.Show();
                owner.UpdateLayout();
                var opening = (ToolTipEventArgs)Activator.CreateInstance(typeof(ToolTipEventArgs),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    binder: null, args: [false], culture: null)!;
                opening.RoutedEvent = ToolTipService.ToolTipOpeningEvent;
                target.RaiseEvent(opening);

                Assert.Equal(424, tooltip.MaxWidth);
                Assert.Equal(360, tooltip.MaxHeight);
                Assert.NotNull(tooltip.CustomPopupPlacementCallback);
                CustomPopupPlacement[] placements = tooltip.CustomPopupPlacementCallback!(new Size(420, 340), target.RenderSize, new Point());

                Assert.Single(placements);
                Assert.True(placements[0].Point.X > target.ActualWidth);
            }
            finally
            {
                owner.Close();
            }
        });
    }
    [Fact]
    public void RecordHoverPreviewsUseCustomPlacementBesideTheirContent()
    {
        XElement root = LoadView("MainView.xaml");
        XElement[] previews = root.Descendants(Presentation + "ToolTip")
            .Where(element => (string?)element.Attribute("ContentTemplateSelector") == "{x:Static local:MainView.RecordHoverSelector}")
            .ToArray();

        Assert.Equal(3, previews.Length);
        Assert.All(previews, preview => Assert.Equal("Custom", (string?)preview.Attribute("Placement")));
    }
    [Fact]
    public void HoverPreviewsHaveVisibleBorderAndShadowAgainstLightBackground()
    {
        XElement root = LoadView("MainView.xaml");
        XElement[] previews = root.Descendants(Presentation + "ToolTip")
            .Where(element => (string?)element.Attribute("ContentTemplateSelector") == "{x:Static local:MainView.RecordHoverSelector}")
            .ToArray();

        Assert.Equal(3, previews.Length);
        Assert.All(previews, preview =>
            Assert.Equal("{StaticResource HoverPreviewToolTip}", (string?)preview.Attribute("Style")));
        XElement style = root.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "HoverPreviewToolTip");
        XElement Setter(string property) => style.Elements(Presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == property);
        Assert.Equal("{DynamicResource TextSecondaryBrush}", (string?)Setter("BorderBrush").Attribute("Value"));        Assert.Equal("{DynamicResource HoverPreviewBackgroundBrush}", (string?)Setter("Background").Attribute("Value"));
        XElement template = style.Elements(Presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == "Template");
        Assert.Equal("10", (string?)template.Descendants(Presentation + "Border").Single().Attribute("CornerRadius"));

        XElement light = LoadView("LightGlass.xaml");
        XElement dark = LoadView("LiquidGlass.xaml");
        XElement lightBackground = light.Descendants(Presentation + "SolidColorBrush")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "HoverPreviewBackgroundBrush");
        XElement darkBackground = dark.Descendants(Presentation + "SolidColorBrush")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "HoverPreviewBackgroundBrush");
        Assert.Equal("#F2E9EDEF", (string?)lightBackground.Attribute("Color"));
        Assert.Equal("#F2292D35", (string?)darkBackground.Attribute("Color"));
        Assert.Equal("1", (string?)Setter("BorderThickness").Attribute("Value"));
        Assert.Equal("8", (string?)Setter("Margin").Attribute("Value"));
        Assert.Equal("{StaticResource HoverPreviewShadow}", (string?)Setter("Effect").Attribute("Value"));
        XElement shadow = root.Descendants().Single(element => (string?)element.Attribute(Xaml + "Key") == "HoverPreviewShadow");
        Assert.Equal("18", (string?)shadow.Attribute("BlurRadius"));
        Assert.Equal("0.30", (string?)shadow.Attribute("Opacity"));
    }
    [Fact]
    public void RecordDataDragFactoryProvidesTextImageAndFileDropFormats()
    {
        Type? factory = typeof(ClipboardManager.Views.MainView).Assembly.GetType("ClipboardManager.Utilities.RecordDragData");
        Assert.NotNull(factory);
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var textFactory = factory!.GetMethod("CreateText", flags);
        var imageFactory = factory.GetMethod("CreateImage", flags);
        Assert.NotNull(textFactory);
        Assert.NotNull(imageFactory);

        const string text = "拖到其他软件的文字";
        var textData = Assert.IsType<DataObject>(textFactory!.Invoke(null, [text]));
        Assert.True(textData.GetDataPresent(DataFormats.UnicodeText));
        Assert.Equal(text, textData.GetData(DataFormats.UnicodeText));

        BitmapSource bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        bitmap.Freeze();
        const string imagePath = "C:\\clipboard-history\\image.png";
        var imageData = Assert.IsType<DataObject>(imageFactory!.Invoke(null, [imagePath, bitmap]));
        Assert.True(imageData.GetDataPresent(DataFormats.Bitmap));
        Assert.True(imageData.GetDataPresent(DataFormats.FileDrop));
        Assert.Equal(imagePath, Assert.IsType<string[]>(imageData.GetData(DataFormats.FileDrop))[0]);
        var fileOnlyData = Assert.IsType<DataObject>(imageFactory.Invoke(null, [imagePath, null]));
        Assert.False(fileOnlyData.GetDataPresent(DataFormats.Bitmap));
        Assert.Equal(imagePath, Assert.IsType<string[]>(fileOnlyData.GetData(DataFormats.FileDrop))[0]);
    }

    [Fact]
    public void DragGestureUsesSystemThresholdAndDoesNotReplaceCopyBindings()
    {
        Type? factory = typeof(ClipboardManager.Views.MainView).Assembly.GetType("ClipboardManager.Utilities.RecordDragData");
        Assert.NotNull(factory);
        var threshold = factory!.GetMethod("HasExceededThreshold",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(threshold);
        var origin = new Point(20, 30);
        Assert.False((bool)threshold!.Invoke(null, [origin, new Point(23, 30), 4d, 4d])!);
        Assert.True((bool)threshold.Invoke(null, [origin, new Point(24, 30), 4d, 4d])!);
        Assert.True((bool)threshold.Invoke(null, [origin, new Point(20, 26), 4d, 4d])!);

        XElement root = LoadView("MainView.xaml");
        XElement style = root.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute(Xaml + "Key") == "RecordCopySurface");
        string[] dragEvents = style.Elements(Presentation + "EventSetter")
            .Select(element => (string?)element.Attribute("Event") ?? string.Empty)
            .ToArray();
        Assert.Contains("PreviewMouseLeftButtonDown", dragEvents);
        Assert.Contains("PreviewMouseMove", dragEvents);
        Assert.Contains("PreviewMouseLeftButtonUp", dragEvents);
        Assert.Equal("{Binding LatestRecord.CopyCommand}", (string?)Named(root, "LatestCopyButton").Attribute("Command"));
        Assert.Contains(root.Descendants(Presentation + "Button"), button =>
            (string?)button.Attribute("Command") == "{Binding CopyCommand}"
            && (string?)button.Attribute("Style") == "{StaticResource RecordCopySurface}");
    }
    [Fact]
    public void RecordDragLifecycleExposesActiveStateAndRaisesCompletionOnce()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var view = new ClipboardManager.Views.MainView();
            var viewType = typeof(ClipboardManager.Views.MainView);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var begin = viewType.GetMethod("BeginRecordDrag", flags);
            var complete = viewType.GetMethod("CompleteRecordDrag", flags);
            Assert.NotNull(begin);
            Assert.NotNull(complete);

            int completed = 0;
            view.RecordDragCompleted += (_, _) => completed++;
            begin!.Invoke(view, null);

            Assert.True(view.IsRecordDragInProgress);
            complete!.Invoke(view, null);
            Assert.False(view.IsRecordDragInProgress);
            Assert.Equal(1, completed);

            complete.Invoke(view, null);
            Assert.Equal(1, completed);
        });
    }

    [Fact]
    public void WindowDeactivationIsDeferredDuringRecordDragAndReplayedWhenStillInactive()
    {
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var shouldDefer = typeof(MainWindow).GetMethod("ShouldDeferDeactivationForRecordDrag", flags);
        var shouldReplay = typeof(MainWindow).GetMethod("ShouldReplayDeactivationAfterRecordDrag", flags);
        Assert.NotNull(shouldDefer);
        Assert.NotNull(shouldReplay);

        Assert.True((bool)shouldDefer!.Invoke(null, [true])!);
        Assert.False((bool)shouldDefer.Invoke(null, [false])!);
        Assert.True((bool)shouldReplay!.Invoke(null, [true, false])!);
        Assert.False((bool)shouldReplay.Invoke(null, [true, true])!);
        Assert.False((bool)shouldReplay.Invoke(null, [false, false])!);
    }

    [Fact]
    public void RecordDragReleasesButtonCaptureBeforeNativeDrag()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var view = new ClipboardManager.Views.MainView();
            var button = new Button();
            var owner = new Window { Content = button, Width = 100, Height = 60, ShowInTaskbar = false };
            try
            {
                owner.Show();
                owner.UpdateLayout();
                Assert.True(button.CaptureMouse());
                Assert.True(button.IsMouseCaptured);

                var releaseCapture = typeof(ClipboardManager.Views.MainView).GetMethod(
                    "ReleaseMouseCaptureForRecordDrag",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(releaseCapture);
                releaseCapture!.Invoke(null, [button]);

                Assert.False(button.IsMouseCaptured);
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void RecordDragMouseReleaseIsHandledSoItCannotCopyAgain()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var view = new ClipboardManager.Views.MainView();
            var button = new Button();
            var viewType = typeof(ClipboardManager.Views.MainView);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            viewType.GetField("_draggedButtonAwaitingRelease", flags)!.SetValue(view, button);
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                Source = button
            };
            var handler = viewType.GetMethod("OnRecordDragMouseLeftButtonUp", flags);
            Assert.NotNull(handler);

            handler!.Invoke(view, [button, args]);

            Assert.True(args.Handled);
            Assert.Null(viewType.GetField("_draggedButtonAwaitingRelease", flags)!.GetValue(view));
        });
    }
    [Fact]
    public void HoverPreviewDefaultsToTheLeftOfTheRecord()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var target = new Rect(1200, 400, 300, 27);
        var preferred = new Size(420, 340);

        HoverPreviewLayout layout = HoverPreviewPositioning.Calculate(workArea, target, preferred);

        Assert.Equal(HoverPreviewSide.Left, layout.Side);
        Assert.Equal(420, layout.Width);
        Assert.Equal(340, layout.Height);
        Assert.Equal(target.Left - HoverPreviewPositioning.DefaultGap - 420, layout.Left);
        Assert.Equal(target.Top + (target.Height - 340) / 2, layout.Top);
    }

    [Fact]
    public void HoverPreviewFlipsToTheRightWhenTheLeftSideCannotFit()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var target = new Rect(180, 400, 300, 27);
        var preferred = new Size(420, 340);

        HoverPreviewLayout layout = HoverPreviewPositioning.Calculate(workArea, target, preferred);

        Assert.Equal(HoverPreviewSide.Right, layout.Side);
        Assert.Equal(420, layout.Width);
        Assert.Equal(target.Right + HoverPreviewPositioning.DefaultGap, layout.Left);
    }

    [Fact]
    public void HoverPreviewUsesTheRoomierSideWhenNeitherSideFitsTheFullWidth()
    {
        var workArea = new Rect(0, 0, 700, 600);
        var target = new Rect(250, 200, 200, 27);
        var preferred = new Size(400, 300);

        HoverPreviewLayout layout = HoverPreviewPositioning.Calculate(workArea, target, preferred);

        Assert.Equal(HoverPreviewSide.Right, layout.Side);
        Assert.True(layout.Width < 400);
        Assert.InRange(layout.Left + layout.Width, workArea.Left, workArea.Right);
    }

    [Fact]
    public void HoverPreviewStaysInsideTheWorkAreaVertically()
    {
        var workArea = new Rect(0, 40, 1920, 1000);
        var preferred = new Size(400, 320);

        HoverPreviewLayout top = HoverPreviewPositioning.Calculate(workArea, new Rect(1200, 42, 300, 27), preferred);
        HoverPreviewLayout bottom = HoverPreviewPositioning.Calculate(workArea, new Rect(1200, 1020, 300, 27), preferred);

        Assert.Equal(workArea.Top + HoverPreviewPositioning.DefaultMargin, top.Top);
        Assert.Equal(workArea.Bottom - HoverPreviewPositioning.DefaultMargin - 320, bottom.Top);
        Assert.All(new[] { top, bottom }, layout =>
        {
            Assert.InRange(layout.Top, workArea.Top, workArea.Bottom - layout.Height);
            Assert.InRange(layout.Left, workArea.Left, workArea.Right - layout.Width);
        });
    }

    [Fact]
    public void HoverPreviewImageKeepsItsAspectRatioAndIsNotUpscaledPastTheBox()
    {
        WithViewModel(owner =>
        {
            var wide = new RecordViewModel(ImageRecord(1600, 400), owner);
            var tall = new RecordViewModel(ImageRecord(300, 1200), owner);
            var small = new RecordViewModel(ImageRecord(120, 80), owner);

            Assert.Equal(new Size(400, 100), new Size(wide.PreviewImageWidth, wide.PreviewImageHeight));
            Assert.Equal(new Size(75, 300), new Size(tall.PreviewImageWidth, tall.PreviewImageHeight));
            Assert.Equal(new Size(120, 80), new Size(small.PreviewImageWidth, small.PreviewImageHeight));
        });
    }
    [Fact]
    public void PageHeaderLivesInsideTheOverlayCardAndTheWindowHasNoTitleBar()
    {
        XElement frameXaml = LoadView("WindowFrame.xaml");
        XElement overlayPanel = Named(frameXaml, "OverlayPanel");
        Assert.DoesNotContain(frameXaml.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "TitleBar");
        Assert.Contains(Named(frameXaml, "PageHeader"), overlayPanel.Descendants());
        Assert.Contains(Named(frameXaml, "ClosePageButton"), overlayPanel.Descendants());

        wpf.Run(() =>
        {
            var frame = new ClipboardManager.Views.WindowFrame();
            int requests = 0;
            frame.BackRequested += (_, _) => requests++;

            frame.SetPageHeader("设置");
            Assert.Equal("设置", frame.PageTitle.Text);
            Assert.Equal(Visibility.Collapsed, frame.BackButton.Visibility);
            frame.SetPageHeader("应用更新", canGoBack: true);
            Assert.Equal(Visibility.Visible, frame.BackButton.Visibility);

            frame.ClosePageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, frame.ClosePageButton));
            frame.BackButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, frame.BackButton));
            Assert.Equal(2, requests);
        });
    }

    [Fact]
    public void SettingsAndUpdatesOpenInsideTheWidgetWithoutResizingIt()
    {
        Assert.True(OverlaySizePolicy.StaysInWidget(OverlaySize.Settings));
        Assert.True(OverlaySizePolicy.StaysInWidget(OverlaySize.Update));
        Assert.False(OverlaySizePolicy.StaysInWidget(OverlaySize.Details));

        wpf.Run(() =>
        {
            var owner = new Window { Left = 40, Top = 50, Width = 300, Height = 400, WindowStartupLocation = WindowStartupLocation.Manual };
            var frame = new ClipboardManager.Views.WindowFrame();
            owner.Content = frame;
            using var host = new WindowOverlayHost(owner, frame);
            int starting = 0;
            host.SessionStarting += (_, _) => starting++;

            _ = host.ShowAsync(new PlainOverlay(), OverlaySize.Settings);
            Assert.Equal(new Rect(40, 50, 300, 400), new Rect(owner.Left, owner.Top, owner.Width, owner.Height));
            Assert.Equal(284, frame.OverlayPanel.Width);
            Assert.True(double.IsNaN(frame.OverlayPanel.Height));
            Assert.Equal(384, frame.OverlayPanel.MaxHeight);

            _ = host.ShowAsync(new PlainOverlay(), OverlaySize.Update);
            Assert.Equal(new Rect(40, 50, 300, 400), new Rect(owner.Left, owner.Top, owner.Width, owner.Height));
            Assert.Equal(264, frame.OverlayPanel.Width);
            Assert.Equal(Visibility.Visible, frame.BackButton.Visibility);
            Assert.Equal(1, starting);

            host.CloseAll(null);
            owner.Content = null;
        });
    }

    [Fact]
    public void SettingsOverlayRestoresTheCollapsedCapsuleBoundsAfterUsingExpandedBounds()
    {
        wpf.Run(() =>
        {
            var owner = new Window { Left = 40, Top = 50, Width = 240, Height = 40, WindowStartupLocation = WindowStartupLocation.Manual };
            var frame = new ClipboardManager.Views.WindowFrame();
            owner.Content = frame;
            using var host = new WindowOverlayHost(owner, frame);
            host.SessionStarting += (_, _) =>
            {
                owner.Width = 300;
                owner.Height = 400;
            };
            int ended = 0;
            bool restoredBeforeSessionEnded = false;
            host.SessionEnded += (_, _) =>
            {
                ended++;
                restoredBeforeSessionEnded = new Rect(owner.Left, owner.Top, owner.Width, owner.Height) == new Rect(40, 50, 240, 40);
            };

            _ = host.ShowAsync(new PlainOverlay(), OverlaySize.Settings);
            Assert.Equal(new Size(300, 400), new Size(owner.Width, owner.Height));

            host.CompleteCurrent(null);

            Assert.Equal(new Rect(40, 50, 240, 40), new Rect(owner.Left, owner.Top, owner.Width, owner.Height));
            Assert.Equal(1, ended);
            Assert.True(restoredBeforeSessionEnded);
            owner.Content = null;
        });
    }
    private sealed class PlainOverlay : OverlayView
    {
        internal override string? PageTitle => "页面";
    }

    [Theory]
    [InlineData("first line\r\n\tsecond   line\n", "first line second line")]
    [InlineData("  padded  ", "padded")]
    [InlineData(" \r\n ", "（空白文本，4 字符）")]
    public void SingleLineCollapsesWhitespaceRuns(string text, string expected)
    {
        WithViewModel(owner =>
        {
            var item = new RecordViewModel(TextRecord(text), owner);
            Assert.Equal(expected, item.SingleLine);
        });
    }

    [Fact]
    public void SingleLineOfAnImageIsItsSizeSummary()
    {
        WithViewModel(owner =>
        {
            var item = new RecordViewModel(new ClipboardRecord
            {
                Kind = ClipboardRecordKind.Image,
                ImageFileName = $"{Guid.NewGuid():N}.png",
                ImageWidth = 1200,
                ImageHeight = 800,
                ByteLength = 1,
                ContentHash = new string('A', 64),
                CopiedAtUtc = DateTime.UtcNow
            }, owner);

            Assert.Equal(item.ContentInfo, item.SingleLine);
            Assert.StartsWith("1200 × 800", item.SingleLine);
        });
    }

    [Fact]
    public void WidgetStartsCollapsedAndShowsTheLoadingStatusBeforeHistoryIsReady()
    {
        WithViewModel(owner =>
        {
            var raised = new List<string?>();
            owner.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

            Assert.True(owner.IsWidgetCollapsed);
            Assert.Equal("展开", owner.WidgetToggleLabel);
            Assert.Equal(owner.Status, owner.CapsuleText);
            Assert.Null(owner.WidgetPosition);
            Assert.Equal(0, owner.RecordCount);
            Assert.False(owner.HasRecords);

            owner.IsWidgetCollapsed = false;

            Assert.Equal("收起", owner.WidgetToggleLabel);
            Assert.Contains(nameof(MainViewModel.IsWidgetCollapsed), raised);
            Assert.Contains(nameof(MainViewModel.WidgetToggleLabel), raised);
        });
    }

    [Fact]
    public async Task CapsuleShowsTheNewestRecordWithoutLineBreaksAndPersistsItsPosition()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        MainViewModel? owner = null;
        HistoryService? history = null;
        try
        {
            history = new HistoryService(new HistoryStore(root));
            await history.LoadAsync();
            await history.AddAsync(ClipboardSnapshot.FromText("older"));
            await history.AddAsync(ClipboardSnapshot.FromText("newest\r\ncopy"));
            AppSettings settings = history.Settings;
            settings.WidgetLeft = 120;
            settings.WidgetTop = 64;
            await history.UpdateSettingsAsync(settings);

            Task initialized = Task.CompletedTask;
            wpf.Run(() =>
            {
                owner = new MainViewModel(new Window(), history);
                initialized = owner.InitializeAsync();
            });
            await initialized;
            wpf.Run(() =>
            {
                Assert.True(owner!.IsReady);
                Assert.Equal(2, owner.RecordCount);
                Assert.Equal("newest copy", owner.CapsuleText);
                var latest = Assert.IsType<RecordViewModel>(owner.LatestRecord);
                Assert.Equal("newest copy", latest.SingleLine);
                var records = owner.HistoryRecords;
                Assert.Equal(new[] { "newest copy", "older" }, records.Select(record => record.SingleLine));
                Assert.Equal(new Point(120, 64), owner.WidgetPosition);
            });

            Task saved = Task.CompletedTask;
            wpf.Run(() => saved = owner!.SaveWidgetPositionAsync(300, 200));
            await saved;
            wpf.Run(() => Assert.Equal(new Point(300, 200), owner!.WidgetPosition));

            var reloaded = new HistoryService(new HistoryStore(root));
            await reloaded.LoadAsync();
            Assert.Equal(300, reloaded.Settings.WidgetLeft);
            Assert.Equal(200, reloaded.Settings.WidgetTop);
            Assert.False(reloaded.Settings.HideCapsulePreview);
        }
        finally
        {
            if (owner is not null)
            {
                Task shutdown = Task.CompletedTask;
                wpf.Run(() => shutdown = owner.ShutdownAsync());
                await shutdown;
            }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SettingsCloneKeepsWidgetFields()
    {
        var settings = new AppSettings { HideCapsulePreview = true, WidgetLeft = 12.5, WidgetTop = -40 };

        AppSettings clone = settings.Clone();

        Assert.True(clone.HideCapsulePreview);
        Assert.Equal(12.5, clone.WidgetLeft);
        Assert.Equal(-40, clone.WidgetTop);
        Assert.Null(new AppSettings().WidgetLeft);
        Assert.False(new AppSettings().HideCapsulePreview);
    }

    [Fact]
    public void SettingsPageSavesTheCapsulePreviewChoiceAndKeepsTheWidgetPosition()
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(AppTheme.Light);
            var original = new AppSettings { WidgetLeft = 40, WidgetTop = 80 };
            var overlay = new SettingsOverlay(original, Path.GetTempPath());
            bool? result = null;
            overlay.CloseRequest = value => result = value;
            var view = (ClipboardManager.Views.SettingsView)overlay.Content!;

            Assert.False(view.HideCapsulePreviewCheckBox.IsChecked);
            Assert.Equal("保持展开", view.AlwaysOnTopCheckBox.Content);
            view.HideCapsulePreviewCheckBox.IsChecked = true;
            view.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, view.SaveButton));

            Assert.True(result);
            Assert.True(overlay.Settings.HideCapsulePreview);
            Assert.False(original.HideCapsulePreview);
            Assert.Equal(40, overlay.Settings.WidgetLeft);
            Assert.Equal(80, overlay.Settings.WidgetTop);
            overlay.OnDismissed(true);
        });
    }

    private void WithViewModel(Action<MainViewModel> action)
    {
        MainViewModel? owner = null;
        wpf.Run(() => owner = new MainViewModel(new Window(),
            new HistoryService(new HistoryStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))))));
        try { wpf.Run(() => action(owner!)); }
        finally
        {
            Task shutdown = Task.CompletedTask;
            wpf.Run(() => shutdown = owner!.ShutdownAsync());
            shutdown.GetAwaiter().GetResult();
        }
    }

    private static ClipboardRecord ImageRecord(int width, int height) => new()
    {
        Kind = ClipboardRecordKind.Image,
        ImageFileName = $"{Guid.NewGuid():N}.png",
        ImageWidth = width,
        ImageHeight = height,
        ByteLength = 1024,
        ContentHash = new string('C', 64),
        CopiedAtUtc = DateTime.UtcNow
    };

    private static ClipboardRecord TextRecord(string text) => new()
    {
        Kind = ClipboardRecordKind.Text,
        Text = text,
        ByteLength = text.Length,
        ContentHash = new string('B', 64),
        CopiedAtUtc = DateTime.UtcNow
    };

    private static XElement LoadView(string fileName) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Xaml", "Views", fileName)).Root!;

    private static XElement Named(XElement root, string name) =>
        root.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == name);
}
