using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.ViewModels;

namespace ClipboardManager;

public sealed class DetailsWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();

    public DetailsWindow(Window owner, RecordViewModel item, ClipboardReader clipboard, IntPtr clipboardOwner)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner;
        Title = item.IsImage ? "图片详情" : "文本详情";
        Width = 800;
        Height = 660;
        MinWidth = 500;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.RegisterWindow(this);
        var view = ViewLoader.Load<Grid>("Views/DetailsView.xaml");
        Content = view;
        DataContext = item;
        var copy = (Button)view.FindName("CopyButton");
        var status = (TextBlock)view.FindName("StatusLabel");
        var image = (Image)view.FindName("FullImage");
        var imageStatus = (TextBlock)view.FindName("ImageStatus");
        var fit = (CheckBox)view.FindName("FitCheckBox");
        var slider = (Slider)view.FindName("ZoomSlider");
        var zoomLabel = (TextBlock)view.FindName("ZoomLabel");
        var scroll = (ScrollViewer)view.FindName("ImageScroll");
        ((Button)view.FindName("CloseButton")).Click += (_, _) => Close();
        copy.Click += async (_, _) =>
        {
            copy.IsEnabled = false;
            try
            {
                if (item.IsImage) await clipboard.WriteImageAsync(item.ImagePath!, clipboardOwner, _lifetime.Token);
                else await clipboard.WriteTextAsync(item.Record.Text!, clipboardOwner, _lifetime.Token);
                status.Text = "已复制到系统剪贴板";
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                status.Text = "复制失败：" + exception.Message;
                status.ToolTip = status.Text;
                AppLogger.LogException("从详情复制记录失败", exception);
            }
            finally { copy.IsEnabled = true; }
        };
        void UpdateZoom()
        {
            bool fitWindow = fit.IsChecked == true;
            slider.IsEnabled = !fitWindow;
            scroll.HorizontalScrollBarVisibility = fitWindow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            scroll.VerticalScrollBarVisibility = fitWindow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            if (image.Source is BitmapSource source)
            {
                image.Width = fitWindow ? double.NaN : source.PixelWidth * slider.Value;
                image.Height = fitWindow ? double.NaN : source.PixelHeight * slider.Value;
            }
            zoomLabel.Text = fitWindow ? "适应" : $"{slider.Value:P0}";
        }
        fit.Checked += (_, _) => UpdateZoom();
        fit.Unchecked += (_, _) => UpdateZoom();
        slider.ValueChanged += (_, _) => UpdateZoom();
        Loaded += async (_, _) =>
        {
            if (!item.IsImage) return;
            try
            {
                image.Source = await ImageLoader.LoadAsync(item.ImagePath!, cancellationToken: _lifetime.Token);
                imageStatus.Text = string.Empty;
                UpdateZoom();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                imageStatus.Text = "原图加载失败：" + exception.Message;
                AppLogger.LogException("加载原图失败", exception);
            }
        };
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); image.Source = null; };
    }
}
