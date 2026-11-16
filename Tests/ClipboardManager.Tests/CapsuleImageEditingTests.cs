using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.ViewModels;
using ClipboardManager.Views;
using Xunit;

namespace ClipboardManager.Tests;

[Collection(SettingsOverlayWpfCollection.Name)]
public sealed class CapsuleImageEditingTests(SettingsOverlayWpfFixture wpf)
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task CapsuleMenuDisablesImageEditingWhenHistoryHasNoImages() => WithState(
        history => history.AddAsync(ClipboardSnapshot.FromText("只有文字")),
        (history, vm, view) =>
        {
            wpf.Run(() =>
            {
                MenuItem item = OpenCapsuleImageMenu(view);
                Assert.NotNull(item.Command);
                Assert.False(item.Command.CanExecute(null));
                Assert.False(item.IsEnabled);
            });
            return Task.CompletedTask;
        });

    [Fact]
    public Task CapsuleMenuTracksImageTextImageTransitionsAndBusyState() => WithState(
        history => history.AddAsync(ClipboardSnapshot.FromImage(Image(40, 20), "test")),
        async (history, vm, view) =>
        {
            MenuItem? item = null;
            wpf.Run(() =>
            {
                Assert.True(vm.LatestRecord!.IsImage);
                Assert.Equal(40, LatestImage(vm)!.Record.ImageWidth);
                item = OpenCapsuleImageMenu(view);
                Assert.True(item.IsEnabled);
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBusy))!.SetValue(vm, true);
                Assert.False(item.Command.CanExecute(null));
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsBusy))!.SetValue(vm, false);
                Assert.True(item.Command.CanExecute(null));
            });
            await history.AddAsync(ClipboardSnapshot.FromText("最新的是文字"));
            wpf.Run(() =>
            {
                Refresh(vm);
                DrainDispatcher();
                Assert.False(vm.LatestRecord!.IsImage);
                Assert.Null(LatestImage(vm));
                Assert.False(item!.Command.CanExecute(null));
                Assert.False(item.IsEnabled);
                // A previously obtained command must also refuse execution after the latest content changes.
                item.Command.Execute(null);
                Assert.False(vm.IsBusy);
                Assert.DoesNotContain(Application.Current.Windows.OfType<ImageEditorWindow>(), editor => editor.IsLoaded);
            });
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(60, 30), "test"));
            wpf.Run(() =>
            {
                Refresh(vm);
                DrainDispatcher();
                Assert.Equal(60, LatestImage(vm)!.Record.ImageWidth);
                Assert.True(item!.Command.CanExecute(null));
                Assert.True(item.IsEnabled);
            });
            await history.ClearAsync();
            wpf.Run(() =>
            {
                Refresh(vm);
                DrainDispatcher();
                Assert.Null(LatestImage(vm));
                Assert.False(item!.IsEnabled);
            });
        });

    [Fact]
    public Task CapsuleMenuDoesNotFallBackToOlderImagesWhenLatestContentIsText() => WithState(
        async history =>
        {
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(40, 20), "test"));
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(60, 30), "test"));
            await history.AddAsync(ClipboardSnapshot.FromText("最新的是文字"));
        },
        (history, vm, view) =>
        {
            wpf.Run(() =>
            {
                Assert.False(vm.LatestRecord!.IsImage);
                Assert.Equal(2, vm.HistoryRecords.Count(record => record.IsImage));
                Assert.Null(LatestImage(vm));
                MenuItem item = OpenCapsuleImageMenu(view);
                Assert.False(item.Command.CanExecute(null));
                Assert.False(item.IsEnabled);
            });
            return Task.CompletedTask;
        });

    [Fact]
    public Task CapsuleMenuDisablesImageEditingWhenHistoryIsEmpty() => WithState(
        _ => Task.CompletedTask,
        (history, vm, view) =>
        {
            wpf.Run(() =>
            {
                Assert.Null(vm.LatestRecord);
                Assert.False(OpenCapsuleImageMenu(view).IsEnabled);
            });
            return Task.CompletedTask;
        });

    [Fact]
    public Task CapsuleCommandOpensTheLatestRecordWhenItIsAnImage() => WithState(
        async history =>
        {
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(40, 20), "test"));
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(60, 30), "test"));
        },
        async (history, vm, view) =>
        {
            var opened = new TaskCompletionSource<Size>(TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherTimer? timer = null;
            wpf.Run(() =>
            {
                MenuItem item = OpenCapsuleImageMenu(view);
                Assert.True(vm.LatestRecord!.IsImage);
                Assert.True(item.Command.CanExecute(null));
                Menu(view).IsOpen = false;
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    var editor = Application.Current.Windows.OfType<ImageEditorWindow>().FirstOrDefault(window => window.IsLoaded);
                    if (editor is null) return;
                    timer.Stop();
                    editor.Opacity = 0;
                    opened.TrySetResult(new Size(editor.ImageCanvas.Width, editor.ImageCanvas.Height));
                    editor.CloseForShutdown();
                };
                timer.Start();
                item.Command.Execute(null);
            });
            try { Assert.Equal(new Size(60, 30), await opened.Task.WaitAsync(TimeSpan.FromSeconds(10))); }
            finally { wpf.Run(() => timer?.Stop()); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PreviewEditButtonOpensTheImageBeingPreviewed(bool fromHistory) => WithState(
        async history =>
        {
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(240, 120), "test"));
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(320, 160), "test"));
        },
        async (history, vm, view) =>
        {
            var opened = new TaskCompletionSource<Size>(TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherTimer? timer = null;
            wpf.Run(() =>
            {
                view.HoverPreviewPopup.Child.Opacity = 0;
                Button target = view.LatestCopyButton;
                RecordViewModel record = vm.LatestRecord!;
                if (fromHistory)
                {
                    record = vm.HistoryRecords[1];
                    view.ShowHistoryPopup(ClipboardManager.Utilities.CapsulePopupPositioning.Calculate(
                        SystemParameters.WorkArea, new Rect(100, 100, 240, 40), view.MeasureHistoryPopup(300)));
                    view.HistoryPopupBorder.UpdateLayout();
                    target = VisualDescendants<Button>(view.HistoryRecords).Single(button => ReferenceEquals(button.Tag, record));
                    Assert.Single(target.ContextMenu.Items.OfType<MenuItem>());
                }
                target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = UIElement.MouseEnterEvent });
            });
            await Task.Delay(400);
            wpf.Run(() =>
            {
                Assert.True(view.IsHoverPreviewOpen);
                view.HoverPreviewSurface.UpdateLayout();
                Button edit = Assert.Single(VisualDescendants<Button>(view.HoverPreviewSurface),
                    button => System.Windows.Automation.AutomationProperties.GetName(button) == "编辑预览图片");
                Assert.Same((fromHistory ? vm.HistoryRecords[1] : vm.LatestRecord)!.EditImageCommand, edit.Command);
                Assert.True(edit.IsEnabled);
                Grid image = Assert.IsType<Grid>(VisualTreeHelper.GetParent(edit));
                Rect bounds = edit.TransformToAncestor(image).TransformBounds(new Rect(edit.RenderSize));
                Assert.True(new Rect(image.RenderSize).Contains(bounds));
                Assert.InRange(bounds.Top, 0, 8);
                Assert.InRange(image.ActualWidth - bounds.Right, 0, 8);
                string? directory = Environment.GetEnvironmentVariable("CLIPBOARD_WIDGET_QA_DIR");
                if (!string.IsNullOrWhiteSpace(directory))
                    HoverPreviewInteractionTests.Capture(view.HoverPreviewSurface,
                        Path.Combine(directory, $"image-edit-preview-{fromHistory}.png"));
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    var editor = Application.Current.Windows.OfType<ImageEditorWindow>().FirstOrDefault(window => window.IsLoaded);
                    if (editor is null) return;
                    timer.Stop();
                    editor.Opacity = 0;
                    opened.TrySetResult(new Size(editor.ImageCanvas.Width, editor.ImageCanvas.Height));
                    editor.CloseForShutdown();
                };
                timer.Start();
                typeof(Button).GetMethod("OnClick", Private)!.Invoke(edit, null);
            });
            try
            {
                Size size = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(fromHistory ? new Size(240, 120) : new Size(320, 160), size);
                wpf.Run(() => Assert.False(view.IsHoverPreviewOpen));
            }
            finally { wpf.Run(() => timer?.Stop()); }
        });

    [Fact]
    public Task InlineThumbnailsLoadWithoutHoverAndImageRowsFitOnFirstOpen() => WithState(
        async history =>
        {
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(160, 80), "test"));
            await history.AddAsync(ClipboardSnapshot.FromText("文字记录保持紧凑"));
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(40, 60), "test"));
        },
        async (history, vm, view) =>
        {
            ClipboardManager.Controls.AsyncThumbnail[] thumbnails = [];
            wpf.Run(() =>
            {
                var capsuleThumbnail = Assert.Single(VisualDescendants<ClipboardManager.Controls.AsyncThumbnail>(view.LatestCopyButton),
                    image => image.IsVisible);
                Assert.Equal(vm.LatestRecord!.ImagePath, capsuleThumbnail.FilePath);
                Assert.Same(vm.LatestRecord, view.LatestCopyButton.Tag);
                Assert.False(view.IsHoverPreviewOpen);
                view.HistoryPopup.Child.Opacity = 0;
                Size desired = view.MeasureHistoryPopup(300);
                view.ShowHistoryPopup(ClipboardManager.Utilities.CapsulePopupPositioning.Calculate(
                    SystemParameters.WorkArea, new Rect(100, 100, 240, 40), desired));
                DrainDispatcher();
                view.HistoryPopupBorder.UpdateLayout();
                var rows = Enumerable.Range(0, 3).Select(index =>
                    Assert.IsType<ListBoxItem>(view.HistoryRecords.ItemContainerGenerator.ContainerFromIndex(index))).ToArray();
                Assert.True(rows[0].ActualHeight > rows[1].ActualHeight);
                Assert.True(rows[2].ActualHeight > rows[1].ActualHeight);
                foreach (int index in new[] { 0, 2 })
                {
                    var image = Assert.Single(VisualDescendants<ClipboardManager.Controls.AsyncThumbnail>(rows[index]),
                        image => image.IsVisible);
                    Rect rowBounds = rows[index].TransformToAncestor(view.HistoryRecords)
                        .TransformBounds(new Rect(rows[index].RenderSize));
                    Assert.True(rowBounds.Top >= 0 && rowBounds.Bottom <= view.HistoryRecords.ActualHeight,
                        $"Image row clipped on first open: {rowBounds} in {view.HistoryRecords.ActualHeight}.");
                }
                Assert.DoesNotContain(VisualDescendants<ClipboardManager.Controls.AsyncThumbnail>(rows[1]), image => image.IsVisible);
                thumbnails = new[] { capsuleThumbnail }.Concat(new[] { 0, 2 }.SelectMany(index =>
                    VisualDescendants<ClipboardManager.Controls.AsyncThumbnail>(rows[index]).Where(image => image.IsVisible))).ToArray();
            });
            bool loaded = false;
            for (int attempt = 0; attempt < 100 && !loaded; attempt++)
            {
                wpf.Run(() => loaded = thumbnails.All(image => image.Source is BitmapSource));
                if (!loaded) await Task.Delay(20);
            }
            wpf.Run(() =>
            {
                Assert.True(loaded, "Inline images did not load without opening a hover preview.");
                // An Image has no intrinsic size before its asynchronous Source is assigned.
                view.UpdateLayout();
                view.HistoryPopupBorder.UpdateLayout();
                foreach (var thumbnail in thumbnails)
                {
                    Assert.True(Assert.IsAssignableFrom<BitmapSource>(thumbnail.Source).IsFrozen);
                    FrameworkElement container = ReferenceEquals(thumbnail, thumbnails[0]) ? view.Capsule
                        : Assert.IsType<ListBoxItem>(ItemsControl.ContainerFromElement(view.HistoryRecords, thumbnail));
                    Rect bounds = thumbnail.TransformToAncestor(container).TransformBounds(new Rect(thumbnail.RenderSize));
                    Assert.True(bounds.Width > 0 && bounds.Height > 0);
                    Assert.True(new Rect(container.RenderSize).Contains(bounds), $"Thumbnail clipped: {bounds} in {container.RenderSize}.");
                }
                view.CloseHistoryPopup();
            });
        });

    [Fact]
    public Task CapsuleThumbnailHonorsHiddenPreviewAndLatestRecordTransitions() => WithState(
        history => history.AddAsync(ClipboardSnapshot.FromImage(Image(40, 20), "test")),
        async (history, vm, view) =>
        {
            void AssertThumbnail(bool visible)
            {
                DrainDispatcher();
                view.UpdateLayout();
                var thumbnails = VisualDescendants<ClipboardManager.Controls.AsyncThumbnail>(view.LatestCopyButton).ToArray();
                Assert.Single(thumbnails);
                Assert.Equal(visible, thumbnails[0].IsVisible);
                Assert.Equal(visible ? vm.LatestRecord!.ImagePath : null, thumbnails[0].FilePath);
            }
            wpf.Run(() => AssertThumbnail(true));
            var settings = history.Settings;
            settings.HideCapsulePreview = true;
            await history.UpdateSettingsAsync(settings);
            wpf.Run(() => { Refresh(vm); AssertThumbnail(false); Assert.Equal("剪贴板", vm.CapsuleText); });
            settings.HideCapsulePreview = false;
            await history.UpdateSettingsAsync(settings);
            wpf.Run(() => { Refresh(vm); AssertThumbnail(true); });
            await history.AddAsync(ClipboardSnapshot.FromText("最新的是文字"));
            wpf.Run(() => { Refresh(vm); AssertThumbnail(false); });
            await history.AddAsync(ClipboardSnapshot.FromImage(Image(60, 30), "test"));
            wpf.Run(() => { Refresh(vm); AssertThumbnail(true); });
            await history.ClearAsync();
            wpf.Run(() => { Refresh(vm); AssertThumbnail(false); });
        });

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private async Task WithState(Func<HistoryService, Task> seed, Func<HistoryService, MainViewModel, MainView, Task> assert)
    {
        string root = Path.Combine(Path.GetTempPath(), "ClipboardManager.CapsuleImageTests", Guid.NewGuid().ToString("N"));
        var history = new HistoryService(new HistoryStore(root));
        Window? owner = null;
        MainViewModel? vm = null;
        MainView? view = null;
        try
        {
            await history.LoadAsync();
            await seed(history);
            wpf.Run(() =>
            {
                owner = new Window { Width = 260, Height = 80, Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
                vm = new MainViewModel(owner, history);
                Refresh(vm);
                typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsReady))!.SetValue(vm, true);
                view = new MainView { DataContext = vm };
                Menu(view).Opacity = 0;
                owner.Content = view;
                owner.Show();
                owner.UpdateLayout();
            });
            await assert(history, vm!, view!);
        }
        finally
        {
            if (vm is not null)
            {
                Task shutdown = Task.CompletedTask;
                wpf.Run(() => { Menu(view!).IsOpen = false; shutdown = vm.ShutdownAsync(); });
                await shutdown;
            }
            wpf.Run(() => owner?.Close());
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static MenuItem OpenCapsuleImageMenu(MainView view)
    {
        view.Capsule.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = UIElement.MouseRightButtonUpEvent });
        DrainDispatcher();
        MenuItem? item = Menu(view).Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, "编辑最新图片"));
        Assert.NotNull(item);
        return item;
    }
    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static ContextMenu Menu(MainView view) => (ContextMenu)typeof(MainView).GetField("_menu", Private)!.GetValue(view)!;
    private static void Refresh(MainViewModel vm) => typeof(MainViewModel).GetMethod("RefreshRecords", Private)!.Invoke(vm, null);
    private static RecordViewModel? LatestImage(MainViewModel vm) => vm.LatestImageRecord;
    private static BitmapSource Image(int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        image.Freeze();
        return image;
    }
}
