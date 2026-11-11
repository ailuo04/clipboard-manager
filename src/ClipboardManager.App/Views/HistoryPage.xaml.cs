using System.ComponentModel;
using ClipboardManager.App.Controls;
using ClipboardManager.App.ViewModels;
using ClipboardManager.Core.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace ClipboardManager.App.Views;

public sealed partial class HistoryPage : Page
{
    private readonly bool _favoritesOnly;
    private bool _activated;
    private bool _subscribed;

    public HistoryPage(HistoryViewModel viewModel, bool favoritesOnly)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _favoritesOnly = favoritesOnly;
        InitializeComponent();
        DetailsSidebar.Visibility = Visibility.Collapsed;
        DetailsColumn.Width = new GridLength(0);
        PageTitle.Text = favoritesOnly ? "收藏" : "历史";
        PageDescription.Text = favoritesOnly ? "把常用内容收好，需要时快速找回。" : "最近复制的文本和图片，清晰查看、快速搜索。";
        EmptyTitle.Text = favoritesOnly ? "收藏内容会出现在这里" : "复制内容后，历史会显示在这里";
        EmptyDescription.Text = favoritesOnly
            ? "在历史记录中点选收藏，即可建立自己的常用内容集。"
            : "复制文本或图片即可开始；你也可以将重要内容加入收藏。";
        DataContext = ViewModel;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    public HistoryViewModel ViewModel { get; }

    public void UpdateResponsiveLayout(double windowWidth, double hideDetailsBelow)
    {
        bool showDetails = windowWidth >= hideDetailsBelow;
        DetailsSidebar.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = showDetails ? new GridLength(340) : new GridLength(0);
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs args)
    {
        if (_activated) return;
        _activated = true;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _subscribed = true;
        if (_favoritesOnly) await ViewModel.SetFavoritesOnlyAsync(true);
        else await ViewModel.LoadInitialAsync();
        UpdateViewState();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribed) ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = false;
        ViewModel.Dispose();
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_activated && !string.Equals(sender.Text, ViewModel.SearchText, StringComparison.Ordinal))
            ViewModel.SearchText = sender.Text;
    }

    private async void OnKindFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_activated || KindFilterBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        HistoryKindFilter filter = tag switch
        {
            "Text" => HistoryKindFilter.Text,
            "Image" => HistoryKindFilter.Image,
            _ => HistoryKindFilter.All
        };
        await ViewModel.SetKindFilterAsync(filter);
        UpdateViewState();
    }

    private void OnRecordSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_activated) return;
        ViewModel.SelectedItem = RecordsList.SelectedItem as RecordItemViewModel;
        DetailsView.ShowRecord(ViewModel.SelectedItem);
    }

    private async void OnFavoriteClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: RecordItemViewModel item }) return;
        await ViewModel.ToggleFavoriteAsync(item);
        UpdateViewState();
    }

    private async void OnRetryClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.RetryAsync();
        UpdateViewState();
    }

    private async void OnLoadMoreClicked(object sender, RoutedEventArgs args)
    {
        await ViewModel.LoadNextPageAsync();
        UpdateViewState();
    }

    private async void OnClearHistoryClicked(object sender, RoutedEventArgs args)
    {
        if (!_activated) return;
        HistoryClearSummary summary = await ViewModel.GetClearSummaryAsync();
        var dialog = new ContentDialog
        {
            XamlRoot = PageGrid.XamlRoot,
            Title = "清空全部历史？",
            Content = $"将永久删除 {summary.TotalRecords} 条记录，其中包括 {summary.FavoriteRecords} 条收藏和 {summary.ImageRecords} 张图片。此操作无法撤销。",
            PrimaryButtonText = "清空历史",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearAllAsync();
            UpdateViewState();
        }
    }

    private async void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_activated) return;
        bool control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (SearchBox.FocusState != FocusState.Unfocused)
        {
            if (args.Key == VirtualKey.Down && ViewModel.Items.Count > 0)
            {
                RecordsList.SelectedIndex = 0;
                RecordsList.Focus(FocusState.Programmatic);
                args.Handled = true;
            }
            return;
        }

        int index = RecordsList.SelectedIndex;
        switch (args.Key)
        {
            case VirtualKey.Up when index > 0:
                RecordsList.SelectedIndex = index - 1;
                RecordsList.ScrollIntoView(RecordsList.SelectedItem);
                args.Handled = true;
                break;
            case VirtualKey.Down when index + 1 < ViewModel.Items.Count:
                RecordsList.SelectedIndex = index + 1;
                RecordsList.ScrollIntoView(RecordsList.SelectedItem);
                args.Handled = true;
                break;
            case VirtualKey.Enter:
                await ViewModel.CopySelectedAsync();
                args.Handled = true;
                break;
            case VirtualKey.C when control:
                await ViewModel.CopySelectedAsync();
                args.Handled = true;
                break;
            case VirtualKey.P when control:
                if (ViewModel.SelectedItem is { } item) await ViewModel.ToggleFavoriteAsync(item);
                args.Handled = true;
                break;
            case VirtualKey.Delete:
                if (await ConfirmDeleteSelectedAsync()) await ViewModel.DeleteSelectedAsync();
                args.Handled = true;
                break;
        }
        UpdateViewState();
    }

    private async Task<bool> ConfirmDeleteSelectedAsync()
    {
        if (ViewModel.SelectedItem is not { } item) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = PageGrid.XamlRoot,
            Title = "删除这条记录？",
            Content = item.Record.Kind == ClipboardContentKind.Image ? "此图片记录将从历史中删除。" : "此文本记录将从历史中删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(HistoryViewModel.IsLoading) or nameof(HistoryViewModel.IsEmpty)
            or nameof(HistoryViewModel.ErrorMessage) or nameof(HistoryViewModel.TotalCount)
            or nameof(HistoryViewModel.HasMore) or nameof(HistoryViewModel.SelectedItem))
            UpdateViewState();
    }

    private void UpdateViewState()
    {
        if (!_activated) return;
        RecordCountText.Text = ViewModel.TotalCount == 1 ? "1 条记录" : $"{ViewModel.TotalCount} 条记录";
        LoadingRing.IsActive = ViewModel.IsLoading;
        LoadingRing.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ErrorState.Visibility = ViewModel.ErrorMessage is not null ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = ViewModel.ErrorMessage ?? string.Empty;
        EmptyState.Visibility = !ViewModel.IsLoading && ViewModel.ErrorMessage is null && ViewModel.IsEmpty
            ? Visibility.Visible : Visibility.Collapsed;
        RecordsList.Visibility = ViewModel.ErrorMessage is null && !ViewModel.IsEmpty
            ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.Visibility = ViewModel.HasMore && !ViewModel.IsLoading
            ? Visibility.Visible : Visibility.Collapsed;
        DetailsView.ShowRecord(ViewModel.SelectedItem);
    }
}