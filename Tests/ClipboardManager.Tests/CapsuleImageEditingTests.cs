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
