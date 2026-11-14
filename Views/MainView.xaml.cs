using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Controls;
using ClipboardManager.Services;
using ClipboardManager.ViewModels;
using ClipboardManager.Utilities;

namespace ClipboardManager.Views;

public partial class MainView : Grid
{
    public static readonly RecordHoverTemplateSelector RecordHoverSelector = CreateRecordHoverSelector();

    private static RecordHoverTemplateSelector CreateRecordHoverSelector()
    {
        var selector = new RecordHoverTemplateSelector();
        return selector;
    }

    private readonly ContextMenu _menu = new();
    private bool _menuOpening;
    private bool _recordContextMenuOpen;
    private bool _recordDragInProgress;
    private Button? _dragCandidate;
    private Button? _draggedButtonAwaitingRelease;
    private Point _dragOrigin;

    public MainView()
    {
        InitializeComponent();
        RecordHoverSelector.TextTemplate = (DataTemplate)FindResource("HoverPreviewText");
        RecordHoverSelector.ImageTemplate = (DataTemplate)FindResource("HoverPreviewImage");
        BuildMenu();
        HistoryToggleButton.Click += (_, _) => HistoryToggleRequested?.Invoke(this, EventArgs.Empty);
        MoreButton.Click += (_, _) => ToggleMoreMenu();
        _menu.Closed += (_, _) => _menuOpening = false;
        HistoryRecords.PreviewKeyDown += OnHistoryRecordsPreviewKeyDown;
        HistoryPopup.Closed += (_, _) => { CloseMoreMenu(); HistoryToggleButton.IsChecked = false; };
        Capsule.MouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;
            OpenMenu(Capsule, PlacementMode.MousePoint);
        };
    }

    public event EventHandler? ExitRequested;

    public event EventHandler? HistoryToggleRequested;

    public event EventHandler? RecordDragCompleted;

    public bool IsRecordDragInProgress => _recordDragInProgress;

    public bool IsContextMenuOpen => _menu.IsOpen || _menuOpening || _recordContextMenuOpen;

    public bool IsMoreMenuOpen => _menu.IsOpen && ReferenceEquals(_menu.PlacementTarget, MoreButton);

    public bool IsHistoryPopupOpen => HistoryPopup.IsOpen;

    public bool IsHistoryPopupPointerOver => HistoryPopupBorder.IsMouseOver;


    public bool IsHistoryTogglePointerOver => HistoryToggleButton.IsMouseOver;

    public void CloseMoreMenu()
    {
        if (!IsMoreMenuOpen) return;
        _menu.IsOpen = false;
        _menuOpening = false;
    }

    private void ToggleMoreMenu()
    {
        if (IsMoreMenuOpen) CloseMoreMenu();
        else OpenMenu(MoreButton, PlacementMode.Top);
    }

    public void CloseHistoryPopup()
    {
        CloseMoreMenu();
        HistoryPopup.IsOpen = false;
        HistoryToggleButton.IsChecked = false;
    }

    internal Size MeasureHistoryPopup(double width)
    {
        HistoryPopup.DataContext = DataContext;
        HistoryPopupBorder.DataContext = DataContext;
        HistoryRecords.GetBindingExpression(ItemsControl.ItemsSourceProperty)?.UpdateTarget();
        HistoryPopupBorder.Width = width;
        HistoryPopupBorder.MaxHeight = 360;

        // Closed virtualized popups may report only their chrome before rows are realized.
        // Size the viewport from the fixed row extent, independently of that stale DesiredSize.
        double innerWidth = Math.Max(0, width - HistoryPopupBorder.BorderThickness.Left - HistoryPopupBorder.BorderThickness.Right);
        HistoryHeader.Measure(new Size(innerWidth, double.PositiveInfinity));
        HistoryFooter.Measure(new Size(innerWidth, double.PositiveInfinity));
        double rowExtent = (double)FindResource("HistoryRowHeight") + 1; // Item's bottom margin.
        double viewport = Math.Min(280, Math.Max(44,
            HistoryRecords.Items.Count * rowExtent + HistoryRecords.Padding.Top + HistoryRecords.Padding.Bottom));
        double height = viewport + HistoryHeader.DesiredSize.Height + HistoryFooter.DesiredSize.Height
            + HistoryPopupBorder.BorderThickness.Top + HistoryPopupBorder.BorderThickness.Bottom;
        HistoryPopupBorder.Height = Math.Min(360, Math.Ceiling(height));
        return new Size(width, HistoryPopupBorder.Height);
    }

    internal void ShowHistoryPopup(CapsulePopupLayout layout)
    {
        HistoryPopup.DataContext = DataContext;
        HistoryPopup.Placement = layout.Placement;
        HistoryPopup.HorizontalOffset = layout.HorizontalOffset;
        HistoryPopup.VerticalOffset = layout.Placement == PlacementMode.Bottom ? 4 : -4;
        HistoryPopupBorder.Width = layout.Width;
        HistoryPopupBorder.Height = layout.MaxHeight;
        HistoryPopupBorder.MaxHeight = layout.MaxHeight;
        HistoryToggleButton.IsChecked = true;
        HistoryPopup.IsOpen = true;
    }

    internal void FocusHistoryRecords()
    {
        if (!HistoryPopup.IsOpen) return;
        if (HistoryRecords.Items.Count == 0) { HistoryRecords.Focus(); return; }
        if (HistoryRecords.SelectedIndex < 0) HistoryRecords.SelectedIndex = 0;
        HistoryRecords.ScrollIntoView(HistoryRecords.SelectedItem);
        HistoryRecords.UpdateLayout();
        if (HistoryRecords.ItemContainerGenerator.ContainerFromItem(HistoryRecords.SelectedItem) is ListBoxItem item)
            item.Focus();
        else HistoryRecords.Focus();
    }

    private void OnHistoryRecordsPreviewKeyDown(object sender, KeyEventArgs args)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (args.Key == Key.Escape && modifiers == ModifierKeys.None)
        {
            CloseHistoryPopup();
            args.Handled = true;
        }
        else if ((args.Key == Key.Enter && modifiers == ModifierKeys.None)
            || (args.Key == Key.C && modifiers == ModifierKeys.Control))
        {
            if (HistoryRecords.SelectedItem is RecordViewModel item && item.CopyCommand.CanExecute(null))
            {
                args.Handled = true;
                item.CopyCommand.Execute(null);
            }
        }
    }

    private void OnHoverPreviewToolTipOpening(object sender, ToolTipEventArgs args)
    {
        if (sender is not FrameworkElement target
            || ToolTipService.GetToolTip(target) is not ToolTip tooltip
            || !ReferenceEquals(tooltip.ContentTemplateSelector, RecordHoverSelector))
            return;

        ConfigureHoverPreviewPlacement(tooltip, target);
    }

    internal void ConfigureHoverPreviewPlacement(ToolTip tooltip, FrameworkElement target)
    {
        tooltip.Placement = PlacementMode.Custom;
        Point screenOrigin = target.PointToScreen(new Point());
        Matrix? transformFromDevice = PresentationSource.FromVisual(target)?.CompositionTarget?.TransformFromDevice;
        Point targetOrigin = transformFromDevice.HasValue
            ? transformFromDevice.Value.Transform(screenOrigin)
            : screenOrigin;
        Window? owner = Window.GetWindow(this);
        Rect workArea = owner is null ? SystemParameters.WorkArea : OverlayWindowPresenter.GetWorkArea(owner);
        var preferredTarget = new Rect(targetOrigin, target.RenderSize);
        HoverPreviewLayout preferredLayout = HoverPreviewPositioning.Calculate(workArea, preferredTarget, new Size(424, 360));
        tooltip.MaxWidth = preferredLayout.Width;
        tooltip.MaxHeight = preferredLayout.Height;
        tooltip.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            var targetBounds = new Rect(targetOrigin, targetSize);
            HoverPreviewLayout layout = HoverPreviewPositioning.Calculate(workArea, targetBounds, popupSize);
            var offset = new Point(layout.Left - targetBounds.Left, layout.Top - targetBounds.Top);
            return [new CustomPopupPlacement(offset, PopupPrimaryAxis.None)];
        };
    }
    private void BeginRecordDrag() => _recordDragInProgress = true;

    private void CompleteRecordDrag()
    {
        if (!_recordDragInProgress) return;
        _recordDragInProgress = false;
        RecordDragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private static void ReleaseMouseCaptureForRecordDrag(Button button)
    {
        if (button.IsMouseCaptured) button.ReleaseMouseCapture();
    }
    private void OnRecordDragMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        _draggedButtonAwaitingRelease = null;
        if (sender is not Button button || GetRecordForDrag(button.DataContext) is null)
        {
            _dragCandidate = null;
            return;
        }

        _dragCandidate = button;
        _dragOrigin = args.GetPosition(button);
    }

    private void OnRecordDragMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (ReferenceEquals(sender, _draggedButtonAwaitingRelease))
        {
            _draggedButtonAwaitingRelease = null;
            args.Handled = true;
        }
        if (ReferenceEquals(sender, _dragCandidate)) _dragCandidate = null;
    }

    private async void OnRecordDragMouseMove(object sender, MouseEventArgs args)
    {
        if (sender is not Button button || !ReferenceEquals(button, _dragCandidate)
            || args.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = args.GetPosition(button);
        if (!RecordDragData.HasExceededThreshold(_dragOrigin, current,
                SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance))
            return;

        _dragCandidate = null;
        _draggedButtonAwaitingRelease = button;
        args.Handled = true;
        BeginRecordDrag();
        try
        {
            RecordViewModel? record = GetRecordForDrag(button.DataContext);
            if (record is null) return;
            DataObject? data = await CreateDragDataAsync(record);
            if (data is null || !button.IsLoaded || Mouse.LeftButton != MouseButtonState.Pressed) return;
            ReleaseMouseCaptureForRecordDrag(button);
            DragDrop.DoDragDrop(button, data, DragDropEffects.Copy);
        }
        catch (Exception exception)
        {
            AppLogger.LogException("拖放剪贴板记录失败", exception);
        }
        finally
        {
            CompleteRecordDrag();
        }
    }

    private static RecordViewModel? GetRecordForDrag(object? dataContext) => dataContext switch
    {
        RecordViewModel record => record,
        MainViewModel owner => owner.LatestRecord,
        _ => null
    };

    private static async Task<DataObject?> CreateDragDataAsync(RecordViewModel record)
    {
        if (!record.IsImage) return RecordDragData.CreateText(record.Record.Text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(record.ImagePath)) return null;

        BitmapSource? bitmap = null;
        try { bitmap = await ImageLoader.LoadAsync(record.ImagePath, maxEdge: 0); }
        catch (Exception exception) { AppLogger.LogException("拖放图片时读取图像失败", exception); }
        return RecordDragData.CreateImage(record.ImagePath, bitmap);
    }
    private void OnRecordContextMenuOpening(object sender, ContextMenuEventArgs args)
    {
        if (sender is not Button button || button.DataContext is not RecordViewModel { IsImage: true } record)
        {
            args.Handled = true;
            return;
        }
        if (button.ContextMenu is null) return;
        button.ContextMenu.DataContext = record;
        _recordContextMenuOpen = true;
    }

    private void OnRecordContextMenuClosed(object sender, RoutedEventArgs args) => _recordContextMenuOpen = false;

    private void BuildMenu()
    {
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu.Items.Add(CommandItem(nameof(MainViewModel.EditLatestImageCommand), "编辑最新图片"));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(CommandItem(nameof(MainViewModel.PauseCommand), headerPath: nameof(MainViewModel.PauseToolTip)));
        _menu.Items.Add(new Separator());
        _menu.Items.Add(CommandItem(nameof(MainViewModel.SettingsCommand), "设置"));
        _menu.Items.Add(CommandItem(nameof(MainViewModel.UpdateCommand), "检查更新"));
        MenuItem clear = CommandItem(nameof(MainViewModel.ClearCommand), "清空全部历史记录…");
        clear.SetResourceReference(Control.ForegroundProperty, "DangerTextBrush");
        _menu.Items.Add(clear);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exit);
    }

    private static MenuItem CommandItem(string commandPath, string? header = null, string? headerPath = null)
    {
        var item = new MenuItem { Header = header };
        item.SetBinding(MenuItem.CommandProperty, new Binding(commandPath));
        if (headerPath is not null) item.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding(headerPath));
        return item;
    }

    private void OpenMenu(UIElement target, PlacementMode placement)
    {
        // The menu lives outside the visual tree, so it needs the view model handed to it.
        // Mark it before opening the popup HWND: the owner can deactivate during this transition.
        _menuOpening = true;
        _menu.DataContext = DataContext;
        _menu.PlacementTarget = target;
        _menu.Placement = placement;
        _menu.IsOpen = true;
    }
}
