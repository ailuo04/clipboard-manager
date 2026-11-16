using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.ViewModels;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class FavoritesTests(SettingsOverlayWpfFixture wpf)
{
    [Fact]
    public Task FavoritesTabFiltersRecordsWithoutChangingTheCapsuleLatestRecord() => WithView(async (history, model, view) =>
    {
        wpf.Run(() =>
        {
            Assert.Equal(2, view.HistoryRecords.Items.Count);
            string? latest = model.LatestRecord?.Record.Text;
            Click(Assert.IsAssignableFrom<ButtonBase>(view.FindName("FavoritesTab")));
            Pump(view);
            var favorite = Assert.IsType<RecordViewModel>(Assert.Single(view.HistoryRecords.Items.Cast<object>()));
            Assert.Equal("常用内容", favorite.Record.Text);
            Assert.True(Assert.IsType<RadioButton>(view.FindName("FavoritesTab")).IsChecked);
            Assert.False(Assert.IsType<RadioButton>(view.FindName("HistoryTab")).IsChecked);
            Assert.Equal("1", favorite.PositionLabel);
            Assert.Equal(latest, model.LatestRecord?.Record.Text);
            Assert.Equal(latest, model.CapsuleText);
            Assert.Equal(2, model.RecordCount);
            Click(Assert.IsAssignableFrom<ButtonBase>(view.FindName("HistoryTab")));
            Pump(view);
            Assert.Equal(2, view.HistoryRecords.Items.Count);
            Assert.True(Assert.IsType<RadioButton>(view.FindName("HistoryTab")).IsChecked);
            Assert.False(Assert.IsType<RadioButton>(view.FindName("FavoritesTab")).IsChecked);
        });
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StarSavesTextAndImageFavoritesWithoutCopying(bool image) => WithView(async (history, model, view) =>
    {
        int copied = 0;
        wpf.Run(() =>
        {
            model.CopyCompleted += (_, _) => copied++;
            var row = Row(view, image ? 0 : 1);
            var star = Assert.Single(Descendants<Button>(row), button => button.Name == "FavoriteButton");
            Assert.True(star.IsEnabled);
            Click(star);
        });
        await WaitUntil(() => !model.IsBusy && history.Records.Count(record => record.IsPinned) == 2);
        var reloaded = new HistoryService(new HistoryStore(history.RootDirectory));
        await reloaded.LoadAsync();
        Assert.Equal(2, reloaded.Records.Count(record => record.IsPinned));
        wpf.Run(() =>
        {
            Assert.Equal(0, copied);
            Assert.True(view.IsHistoryPopupOpen);
        });
    }, includeImage: true);

    [Fact]
    public Task FailedSaveKeepsTheRecordUnfavoritedAndReportsTheError() => WithView(async (history, model, view) =>
    {
        using var locked = new FileStream(Path.Combine(history.RootDirectory, "history.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        wpf.Run(() => Click(Assert.Single(Descendants<Button>(Row(view, 0)), button => button.Name == "FavoriteButton")));
        await WaitUntil(() => !model.IsBusy && model.Notice.Contains("更新收藏失败"));
        Assert.Single(history.Records, record => record.IsPinned);
        wpf.Run(() =>
        {
            Assert.False(((RecordViewModel)view.HistoryRecords.Items[0]).IsPinned);
            Assert.True(view.IsHistoryPopupOpen);
        });
    });

    [Fact]
    public Task RemovingTheLastFavoriteUpdatesTheListAndEmptyMessage() => WithView(async (history, model, view) =>
    {
        wpf.Run(() =>
        {
            Click(Assert.IsAssignableFrom<ButtonBase>(view.FindName("FavoritesTab")));
            Pump(view);
            Click(Assert.Single(Descendants<Button>(Row(view, 0)), button => button.Name == "FavoriteButton"));
        });
        await WaitUntil(() => !model.IsBusy && history.Records.All(record => !record.IsPinned));
        wpf.Run(() =>
        {
            Pump(view);
            Assert.Empty(view.HistoryRecords.Items);
            var empty = Assert.IsType<TextBlock>(view.FindName("HistoryEmptyMessage"));
            Assert.Equal(Visibility.Visible, empty.Visibility);
            Assert.Contains("收藏", empty.Text);
            Assert.True(view.IsHistoryPopupOpen);
            Click(Assert.IsAssignableFrom<ButtonBase>(view.FindName("HistoryTab")));
            Pump(view);
            Assert.Equal(2, view.HistoryRecords.Items.Count);
            Assert.Equal(Visibility.Collapsed, empty.Visibility);
        });
    });

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public Task FavoriteControlsFitThePopupAndBothRowTypes(AppTheme theme) => WithView(async (history, model, view) =>
    {
        wpf.Run(() =>
        {
            ThemeManager.Apply(theme);
            Pump(view);
            foreach (string name in new[] { "HistoryTab", "FavoritesTab" })
            {
                var tab = Assert.IsAssignableFrom<ButtonBase>(view.FindName(name));
                Assert.True(tab.IsVisible);
                Assert.True(new Rect(view.HistoryHeader.RenderSize).Contains(tab.TransformToAncestor(view.HistoryHeader)
                    .TransformBounds(new Rect(tab.RenderSize))));
            }
            for (int index = 0; index < 3; index++)
            {
                var row = Row(view, index);
                var star = Assert.Single(Descendants<Button>(row), button => button.Name == "FavoriteButton");
                Rect bounds = star.TransformToAncestor(row).TransformBounds(new Rect(star.RenderSize));
                Assert.True(bounds.Width >= 26 && bounds.Height >= 26);
                Assert.True(new Rect(row.RenderSize).Contains(bounds), $"Star clipped: {bounds} in {row.RenderSize}");
                var copy = Assert.Single(Descendants<Button>(row), button => button.Command == ((RecordViewModel)row.DataContext).CopyCommand);
                Assert.DoesNotContain(star, Descendants<Button>(copy));
                Point center = star.TranslatePoint(new Point(star.ActualWidth / 2, star.ActualHeight / 2), row);
                DependencyObject? hit = row.InputHitTest(center) as DependencyObject;
                while (hit is not null && !ReferenceEquals(hit, star)) hit = VisualTreeHelper.GetParent(hit);
                Assert.Same(star, hit);
            }
            string? directory = Environment.GetEnvironmentVariable("CLIPBOARD_FAVORITES_QA_DIR");
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Capture(view.HistoryPopupBorder, Path.Combine(directory, $"history-{theme}.png"));
                Click(Assert.IsAssignableFrom<ButtonBase>(view.FindName("FavoritesTab")));
                Pump(view);
                Capture(view.HistoryPopupBorder, Path.Combine(directory, $"favorites-{theme}.png"));
            }
        });
        await Task.CompletedTask;
    }, includeImage: true);

    private async Task WithView(Func<HistoryService, MainViewModel, MainView, Task> action, bool includeImage = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "clipboard-favorites-" + Guid.NewGuid().ToString("N"));
        MainViewModel? model = null;
        MainView? view = null;
        Window? owner = null;
        try
        {
            var history = new HistoryService(new HistoryStore(root));
            await history.LoadAsync();
            var settings = history.Settings;
            settings.AutoCheckUpdates = false;
            await history.UpdateSettingsAsync(settings);
            await history.AddAsync(ClipboardSnapshot.FromText("常用内容"));
            await history.TogglePinAsync(history.Records[0].Id);
            await history.AddAsync(ClipboardSnapshot.FromText("最近复制"));
            if (includeImage)
            {
                var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)180, 64).ToArray(), 16);
                bitmap.Freeze();
                await history.AddAsync(ClipboardSnapshot.FromImage(bitmap, "FavoritesTest"));
            }
            Task initialize = Task.CompletedTask;
            wpf.Run(() =>
            {
                owner = new Window { Width = 240, Height = 40, ShowInTaskbar = false, Left = 100, Top = 100 };
                view = new MainView();
                owner.Content = view;
                model = new MainViewModel(owner, history);
                view.DataContext = model;
                initialize = model.InitializeAsync();
            });
            await initialize;
            wpf.Run(() =>
            {
                Assert.True(model!.IsReady, model.Notice);
                model.PauseCommand.Execute(null);
                owner!.Show();
                view!.ShowHistoryPopup(CapsulePopupPositioning.Calculate(SystemParameters.WorkArea,
                    new Rect(100, 100, 240, 40), view.MeasureHistoryPopup(300)));
                Pump(view);
            });
            await action(history, model!, view!);
        }
        finally
        {
            Task shutdown = Task.CompletedTask;
            wpf.Run(() => { view?.CloseHistoryPopup(); if (model is not null) shutdown = model.ShutdownAsync(); });
            await shutdown;
            wpf.Run(() => { owner?.Close(); ThemeManager.Apply(AppTheme.Light); });
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 500; i++)
        {
            bool done = false;
            wpf.Run(() => done = condition());
            if (done) return;
            await Task.Delay(10);
        }
        Assert.Fail("Favorite operation did not complete within five seconds.");
    }

    private static ListBoxItem Row(MainView view, int index)
    {
        view.HistoryRecords.ScrollIntoView(view.HistoryRecords.Items[index]);
        Pump(view);
        return Assert.IsType<ListBoxItem>(view.HistoryRecords.ItemContainerGenerator.ContainerFromIndex(index));
    }

    private static void Pump(MainView view)
    {
        view.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        view.HistoryPopupBorder.UpdateLayout();
    }

    private static void Click(ButtonBase button) => typeof(ButtonBase)
        .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Capture(FrameworkElement element, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
