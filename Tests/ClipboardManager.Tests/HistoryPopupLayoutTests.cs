using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Reflection;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Input;
using ClipboardManager.Utilities;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class HistoryPopupLayoutTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public void HistoryPopupRemeasuresRecordsAfterClosedPopupBindingsChange()
    {
        wpf.Run(() => WithView(view =>
        {
            // Prime a closed popup with empty bindings, then supply its records before opening.
            object? populatedContext = view.DataContext;
            view.DataContext = null;
            PumpLayout(view);
            view.MeasureHistoryPopup(300);
            view.DataContext = populatedContext;
            Size desired = view.MeasureHistoryPopup(300);
            var beforeList = (ListBox)view.FindName("HistoryRecords");
            string before = $"items={beforeList.Items.Count}, desired={beforeList.DesiredSize}, valid={beforeList.IsMeasureValid}, context={beforeList.DataContext is not null}";
            view.ShowHistoryPopup(CapsulePopupPositioning.Calculate(
                SystemParameters.WorkArea, new Rect(100, 100, 240, 40), desired));
            PumpLayout(view);
            var list = (ListBox)view.FindName("HistoryRecords");
            Assert.Equal(2, list.Items.Count);
            Assert.True(list.ActualHeight >= 74, $"Two rows need space, but list height is {list.ActualHeight}; measured popup: {desired}; before: {before}.");
            for (int index = 0; index < 2; index++)
            {
                var row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(index));
                Rect bounds = row.TransformToAncestor(list).TransformBounds(new Rect(row.RenderSize));
                Assert.True(bounds.Bottom <= list.ActualHeight, $"Row {index} is clipped: {bounds}, list height {list.ActualHeight}.");
            }
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MoreMenuShowsEveryActionEvenWhenHistoryPopupIsShort(bool nearScreenEdge)
    {
        wpf.Run(() => WithView(view =>
        {
            view.ShowHistoryPopup(new CapsulePopupLayout(PlacementMode.Bottom, 300, 90, 0));
            PumpLayout(view);
            var more = (Button)view.FindName("MoreButton");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, more));
            PumpLayout(view);
            var menu = GetMenu(view);
            Assert.Equal(new[] { "展开", "暂停监听", "设置", "检查更新", "清空全部历史记录…", "退出" },
                menu.Items.OfType<MenuItem>().Select(item => item.Header));
            Assert.True(menu.ActualHeight > 160);
            var exit = Assert.IsType<MenuItem>(menu.Items[^1]);
            Rect exitBounds = exit.TransformToAncestor(menu).TransformBounds(new Rect(exit.RenderSize));
            Assert.True(exitBounds.Bottom <= menu.ActualHeight,
                $"Last action ends at {exitBounds.Bottom}, but menu height is {menu.ActualHeight}.");
            Point screenOrigin = menu.PointToScreen(new Point());
            Point origin = PresentationSource.FromVisual(menu)!.CompositionTarget!.TransformFromDevice.Transform(screenOrigin);
            Rect workArea = SystemParameters.WorkArea;
            Assert.InRange(origin.X, workArea.Left - 1, workArea.Right - menu.ActualWidth + 1);
            Assert.InRange(origin.Y, workArea.Top - 1, workArea.Bottom - menu.ActualHeight + 1);
            Assert.True(view.IsHistoryPopupOpen);

            menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(menu),
                Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            PumpLayout(view);
            Assert.False(view.IsMoreMenuOpen);
            Assert.True(view.IsHistoryPopupOpen);

            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, more));
            PumpLayout(view);
            Assert.True(view.IsMoreMenuOpen);
            view.CloseHistoryPopup();
            Assert.False(view.IsMoreMenuOpen);
        }, nearScreenEdge));
    }

    private static void PumpLayout(MainView view)
    {
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        ((Border)view.FindName("HistoryPopupBorder")).UpdateLayout();
        GetMenu(view).UpdateLayout();
    }

    private static ContextMenu GetMenu(MainView view) =>
        (ContextMenu)typeof(MainView).GetField("_menu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static void WithView(Action<MainView> action, bool nearScreenEdge = false)
    {
        var view = new MainView
        {
            DataContext = new
            {
                IsWidgetCollapsed = true,
                HasRecords = true,
                RecordCount = 2,
                WidgetToggleLabel = "展开",
                PauseToolTip = "暂停监听",
                HistoryRecords = new[]
                {
                    new { SingleLine = "first", PositionLabel = "1", ShortTimeLabel = "12:00" },
                    new { SingleLine = "second", PositionLabel = "2", ShortTimeLabel = "11:59" }
                }
            }
        };
        var owner = new Window { Content = view, Width = 240, Height = 40, ShowInTaskbar = false,
            Left = 100, Top = 100, WindowStartupLocation = WindowStartupLocation.Manual };
        if (nearScreenEdge)
        {
            owner.Left = SystemParameters.WorkArea.Right - owner.Width - 10;
            owner.Top = SystemParameters.WorkArea.Bottom - owner.Height - 10;
        }
        try
        {
            owner.Show();
            owner.UpdateLayout();
            action(view);
        }
        finally
        {
            view.CloseHistoryPopup();
            owner.Close();
        }
    }
}
