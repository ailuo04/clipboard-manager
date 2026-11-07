using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.ViewModels;

namespace ClipboardManager;

internal sealed class DetailsOverlay : OverlayView
{
    private readonly Func<string, CancellationToken, Task> _copyTextAsync;
    private readonly Func<string, CancellationToken, Task> _copyImageAsync;
    private readonly Func<string, CancellationToken, Task<BitmapSource>> _loadImageAsync;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Button _copy;
    private readonly TextBlock _status;
    private readonly Image _image;
    private readonly TextBlock _imageStatus;
    private readonly CheckBox _fit;
    private readonly Slider _slider;
    private readonly TextBlock _zoomLabel;
    private readonly ScrollViewer _scroll;
    private bool _imageLoadStarted;
    private bool _dismissed;

    internal DetailsOverlay(RecordViewModel item, ClipboardReader clipboard, IntPtr clipboardOwner)
        : this(item,
            (text, token) => clipboard.WriteTextAsync(text, clipboardOwner, token),
            (path, token) => clipboard.WriteImageAsync(path, clipboardOwner, token),
            async (path, token) => (BitmapSource)await ImageLoader.LoadAsync(path, cancellationToken: token))
    {
        ArgumentNullException.ThrowIfNull(clipboard);
    }

    internal DetailsOverlay(
        RecordViewModel item,
        Func<string, CancellationToken, Task> copyTextAsync,
        Func<string, CancellationToken, Task> copyImageAsync,
        Func<string, CancellationToken, Task<BitmapSource>> loadImageAsync)
    {
        ArgumentNullException.ThrowIfNull(item);
        _copyTextAsync = copyTextAsync ?? throw new ArgumentNullException(nameof(copyTextAsync));
        _copyImageAsync = copyImageAsync ?? throw new ArgumentNullException(nameof(copyImageAsync));
        _loadImageAsync = loadImageAsync ?? throw new ArgumentNullException(nameof(loadImageAsync));

        var view = new Views.DetailsView();
        Content = view;
        DataContext = item;
        _copy = view.CopyButton;
        _status = view.StatusLabel;
        _image = view.FullImage;
        _imageStatus = view.ImageStatus;
        _fit = view.FitCheckBox;
        _slider = view.ZoomSlider;
        _zoomLabel = view.ZoomLabel;
        _scroll = view.ImageScroll;

        view.CloseDetailsButton.Click += (_, _) => Complete(null);
        _copy.Click += async (_, _) => await CopyAsync(item);
        _fit.Checked += (_, _) => UpdateZoom();
        _fit.Unchecked += (_, _) => UpdateZoom();
        _slider.ValueChanged += (_, _) => UpdateZoom();
        _scroll.SizeChanged += (_, _) => UpdateZoom();
        Loaded += OnLoaded;
        UpdateZoom();
    }

    internal override void OnDismissed(bool? result)
    {
        if (_dismissed) return;
        _dismissed = true;
        Loaded -= OnLoaded;
        try { _lifetime.Cancel(); }
        finally
        {
            _image.Source = null;
            _lifetime.Dispose();
        }
    }

    private async Task CopyAsync(RecordViewModel item)
    {
        if (_dismissed) return;
        _copy.IsEnabled = false;
        try
        {
            if (item.IsImage) await _copyImageAsync(item.ImagePath!, _lifetime.Token);
            else await _copyTextAsync(item.Record.Text!, _lifetime.Token);
            if (!_dismissed) _status.Text = "已复制到系统剪贴板";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_dismissed)
            {
                _status.Text = "复制失败：" + exception.Message;
                _status.ToolTip = _status.Text;
                AppLogger.LogException("从详情复制记录失败", exception);
            }
        }
        finally
        {
            if (!_dismissed) _copy.IsEnabled = true;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_dismissed || _imageLoadStarted || DataContext is not RecordViewModel { IsImage: true } item) return;
        _imageLoadStarted = true;
        try
        {
            BitmapSource loaded = await _loadImageAsync(item.ImagePath!, _lifetime.Token);
            if (_dismissed) return;
            _image.Source = loaded;
            _imageStatus.Text = string.Empty;
            UpdateZoom();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_dismissed) return;
            _imageStatus.Text = "原图加载失败：" + exception.Message;
            AppLogger.LogException("加载原图失败", exception);
        }
    }

    private void UpdateZoom()
    {
        if (_dismissed) return;
        bool fitWindow = _fit.IsChecked == true;
        _slider.IsEnabled = !fitWindow;
        _scroll.HorizontalScrollBarVisibility = fitWindow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = fitWindow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        if (_image.Source is BitmapSource source)
        {
            if (fitWindow)
            {
                double availableWidth = Math.Max(0, _scroll.ViewportWidth - _scroll.Padding.Left - _scroll.Padding.Right);
                double availableHeight = Math.Max(0, _scroll.ViewportHeight - _scroll.Padding.Top - _scroll.Padding.Bottom);
                double scale = Math.Min(1, Math.Min(availableWidth / source.PixelWidth, availableHeight / source.PixelHeight));
                _image.Width = source.PixelWidth * scale;
                _image.Height = source.PixelHeight * scale;
            }
            else
            {
                _image.Width = source.PixelWidth * _slider.Value;
                _image.Height = source.PixelHeight * _slider.Value;
            }
        }
        _zoomLabel.Text = fitWindow ? "适应" : $"{_slider.Value:P0}";
    }
}
