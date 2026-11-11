using ClipboardManager.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ClipboardManager.App.Views;

public sealed partial class ShellPage : Page
{
    public ShellViewModel ViewModel { get; } = new();

    public ShellPage()
    {
        InitializeComponent();
        DetailsSidebar.Visibility = Visibility.Collapsed;
        DetailsColumn.Width = new GridLength(0);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ShellPageState state) ViewModel.Show(state);
    }

    public void UpdateResponsiveLayout(double windowWidth, double hideDetailsBelow)
    {
        bool showDetails = windowWidth >= hideDetailsBelow;
        DetailsSidebar.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = showDetails ? new GridLength(280) : new GridLength(0);
    }
}