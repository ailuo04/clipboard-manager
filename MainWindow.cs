using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ClipboardManager.Services;
using ClipboardManager.Utilities;
using ClipboardManager.ViewModels;

namespace ClipboardManager;

public sealed class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;

    public MainWindow(HistoryService? history = null)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "剪贴板";
        Width = 900;
        Height = 760;
        MinWidth = 500;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = ViewLoader.Load<Grid>("Views/MainView.xaml");
        _viewModel = new MainViewModel(this, history ?? new HistoryService(new HistoryStore()));
        DataContext = _viewModel;
        SizeChanged += (_, _) => _viewModel.UpdateWindowSize();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        await _viewModel.InitializeAsync();
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_viewModel.IsClosing) return;
        IsEnabled = false;
        try { await _viewModel.ShutdownAsync(); }
        catch (Exception exception) { AppLogger.LogException("关闭应用和释放监听资源失败", exception); }
        finally { _allowClose = true; Close(); }
    }
}
