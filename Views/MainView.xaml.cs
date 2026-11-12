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
        MenuButton.Click += (_, _) => OpenMenu(MenuButton, PlacementMode.Bottom);
        HistoryToggleButton.Click += (_, _) => HistoryToggleRequested?.Invoke(this, EventArgs.Empty);
        MoreButton.Click += (_, _) => ToggleMoreMenu();
        MoreToggleButton.Click += (_, _) => { CloseMoreMenu(); ToggleRequested?.Invoke(this, EventArgs.Empty); };
        MorePauseButton.Click += (_, _) => CloseMoreMenu();
        MoreSettingsButton.Click += (_, _) => CloseMoreMenu();
        MoreUpdateButton.Click += (_, _) => CloseMoreMenu();
        MoreClearButton.Click += (_, _) => CloseMoreMenu();
        MoreExitButton.Click += (_, _) => { CloseMoreMenu(); ExitRequested?.Invoke(this, EventArgs.Empty); };
        MoreMenu.PreviewKeyDown += OnMoreMenuPreviewKeyDown;
        HistoryPopupBorder.PreviewMouseDown += OnHistoryPopupPreviewMouseDown;
        _menu.Closed += (_, _) => _menuOpening = false;
        HistoryRecords.PreviewKeyDown += OnHistoryRecordsPreviewKeyDown;
        HistoryPopup.Closed += (_, _) => { CloseMoreMenu(); HistoryToggleButton.IsChecked = false; };
        Capsule.MouseRightButtonUp += (_, args) =>
        {
            args.Handled = true;
            OpenMenu(Capsule, PlacementMode.MousePoint);
        };
    }

    /// <summary>Raised when the menu asks to expand or collapse the widget.</summary>
    public event EventHandler? ToggleRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler? HistoryToggleRequested;

    public event EventHandler? RecordDragCompleted;

    public bool IsRecordDragInProgress => _recordDragInProgress;

    public bool IsMenuOpen => IsContextMenuOpen || IsMoreMenuOpen;
    public bool IsContextMenuOpen => _menu.IsOpen || _menuOpening;

    public bool IsMoreMenuOpen => MoreMenu.Visibility == Visibility.Visible;

    public bool IsHistoryPopupOpen => HistoryPopup.IsOpen;

    public bool IsHistoryPopupPointerOver => HistoryPopupBorder.IsMouseOver;


    public bool IsHistoryTogglePointerOver => HistoryToggleButton.IsMouseOver;

    public void CloseMoreMenu() => MoreMenu.Visibility = Visibility.Collapsed;

    private void ToggleMoreMenu()
    {
        MoreMenu.Visibility = IsMoreMenuOpen ? Visibility.Collapsed : Visibility.Visible;
        if (IsMoreMenuOpen) MoreToggleButton.Focus();
    }

    private void OnMoreMenuPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (!ShouldCloseMoreMenuOnEscape(IsMoreMenuOpen, args.Key)) return;
        CloseMoreMenu();
        args.Handled = true;
    }

    private static bool ShouldCloseMoreMenuOnEscape(bool isMoreMenuOpen, Key key) =>
        isMoreMenuOpen && key == Key.Escape;
    private static bool ShouldCloseMoreMenuOnMouseDown(bool isMoreMenuOpen, bool isClickInsideMoreMenu, bool isClickingToggle) =>
        isMoreMenuOpen && !isClickInsideMoreMenu && !isClickingToggle;

    private void OnHistoryPopupPreviewMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.OriginalSource is not DependencyObject source) return;
        if (ShouldCloseMoreMenuOnMouseDown(IsMoreMenuOpen,
                MoreMenu.IsAncestorOf(source), MoreButton.IsAncestorOf(source)))
            CloseMoreMenu();
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
        HistoryPopupBorder.Width = width;
        HistoryPopupBorder.MaxHeight = 360;
        HistoryRecords.MaxHeight = 280;
        HistoryPopupBorder.Measure(new Size(width, 360));
        return HistoryPopupBorder.DesiredSize;
    }

    internal void ShowHistoryPopup(CapsulePopupLayout layout)
    {
        HistoryPopup.DataContext = DataContext;
        HistoryPopup.Placement = layout.Placement;
        HistoryPopup.HorizontalOffset = layout.HorizontalOffset;
        HistoryPopup.VerticalOffset = layout.Placement == PlacementMode.Bottom ? 4 : -4;
        HistoryPopupBorder.Width = layout.Width;
        HistoryPopupBorder.MaxHeight = layout.MaxHeight;
        HistoryRecords.MaxHeight = Math.Min(280, Math.Max(0, layout.MaxHeight - 82));
        HistoryToggleButton.IsChecked = true;
        HistoryPopup.IsOpen = true;
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
    private void BuildMenu()
    {
        var toggle = new MenuItem();
        toggle.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding(nameof(MainViewModel.WidgetToggleLabel)));
        toggle.Click += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu.Items.Add(toggle);
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
