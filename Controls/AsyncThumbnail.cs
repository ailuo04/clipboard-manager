using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager.Controls;

public sealed class AsyncThumbnail : Image
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(AsyncThumbnail), new PropertyMetadata(null, OnPathChanged));
    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(AsyncThumbnail), new PropertyMetadata("正在加载图片…"));
    private CancellationTokenSource? _loadCancellation;

    public AsyncThumbnail()
    {
        Loaded += (_, _) => Load();
        Unloaded += (_, _) => Cancel();
    }

    public string? FilePath { get => (string?)GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public string StatusText { get => (string)GetValue(StatusTextProperty); private set => SetValue(StatusTextProperty, value); }

    private static void OnPathChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var image = (AsyncThumbnail)target;
        if (image.IsLoaded) image.Load();
    }

    private void Cancel()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        Source = null;
    }

    private async void Load()
    {
        Cancel();
        if (string.IsNullOrEmpty(FilePath)) return;
        var cancellation = _loadCancellation = new CancellationTokenSource();
        StatusText = "正在加载图片…";
        try
        {
            var image = await ImageLoader.LoadAsync(FilePath, 640, cancellation.Token);
            if (_loadCancellation != cancellation) return;
            Source = image;
            StatusText = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_loadCancellation != cancellation) return;
            StatusText = "图片加载失败";
            ToolTip = exception.Message;
            AppLogger.LogException("加载历史缩略图失败", exception);
        }
    }
}
