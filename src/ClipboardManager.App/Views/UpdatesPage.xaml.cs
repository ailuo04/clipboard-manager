using ClipboardManager.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClipboardManager.App.Views;

public sealed partial class UpdatesPage : Page
{
    private bool _suppressAutoCheckToggle;

    public UpdatesPage(UpdatesViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _suppressAutoCheckToggle = true;
        InitializeComponent();
        AutoCheckToggle.IsOn = ViewModel.AutoCheckUpdates;
        _suppressAutoCheckToggle = false;
        DataContext = ViewModel;
        Unloaded += OnPageUnloaded;
    }

    public UpdatesViewModel ViewModel { get; }

    public void UpdateResponsiveLayout(double windowWidth, double hideDetailsBelow)
    {
        bool showDetails = windowWidth >= hideDetailsBelow;
        DetailsSidebar.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = showDetails ? new GridLength(310) : new GridLength(0);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs args)
    {
        Unloaded -= OnPageUnloaded;
        ViewModel.Dispose();
    }

    private async void OnCheckUpdatesClicked(object sender, RoutedEventArgs args)
        => await ViewModel.CheckForUpdatesAsync();

    private async void OnDownloadAndUpdateClicked(object sender, RoutedEventArgs args)
        => await ViewModel.DownloadAndUpdateAsync();

    private void OnCancelDownloadClicked(object sender, RoutedEventArgs args)
        => ViewModel.CancelDownload();

    private async void OnAutoCheckToggled(object sender, RoutedEventArgs args)
    {
        if (_suppressAutoCheckToggle || sender is not ToggleSwitch toggle) return;
        await ViewModel.SetAutoCheckUpdatesAsync(toggle.IsOn);
        if (toggle.IsOn != ViewModel.AutoCheckUpdates) toggle.IsOn = ViewModel.AutoCheckUpdates;
    }
}
