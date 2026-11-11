using ClipboardManager.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClipboardManager.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = ViewModel;
        Unloaded += OnPageUnloaded;
    }

    public SettingsViewModel ViewModel { get; }
    private void OnPageUnloaded(object sender, RoutedEventArgs args) => ViewModel.Cancel();

    private async void OnSaveClicked(object sender, RoutedEventArgs args)
    {
        bool saved = await ViewModel.SaveAsync();
        if (saved || !ViewModel.NeedsCapacityConfirmation) return;

        var dialog = new ContentDialog
        {
            XamlRoot = PageGrid.XamlRoot,
            Title = "确认降低历史容量？",
            Content = ViewModel.CapacityWarning,
            PrimaryButtonText = "继续并保存",
            CloseButtonText = "返回修改",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.SaveAsync(confirmCapacityChange: true);
    }

    private void OnCancelClicked(object sender, RoutedEventArgs args) => ViewModel.Cancel();

    public void UpdateResponsiveLayout(double windowWidth)
    {
        SettingsHelpColumn.Width = windowWidth < 1050 ? new GridLength(0) : new GridLength(300);
        SettingsHelp.Visibility = windowWidth < 1050 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnOpenDataDirectoryClicked(object sender, RoutedEventArgs args)
        => await ViewModel.OpenDataDirectoryAsync();
}
