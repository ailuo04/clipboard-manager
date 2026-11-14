using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Controls;
using ClipboardManager.Models;
using ClipboardManager.Services;
using ClipboardManager.Utilities;

namespace ClipboardManager.Views;

public partial class ImageEditorWindow : Window
{
    private enum EditorTool { Select, Text, Arrow, Rectangle }

    private readonly BitmapSource _image;
    private readonly Func<BitmapSource, Task> _saveAsync;
    private EditorTool _tool;
    private Color _color = Color.FromRgb(229, 72, 77);
    private double _fontSize;
    private string _fontFamily = "Microsoft YaHei UI";
    private Point? _textPointer;
    private Point? _textInsertionPoint;
    private int _editingTextIndex = -1;
    private bool _isComposing;
    private bool IsEditingText => TextEditFrame.Visibility == Visibility.Visible;
    private sealed record FontChoice(string FamilyName, string DisplayName);
    private double _arrowThickness;
    private ArrowEndpoint _draggingEndpoint;
    private RectangleCorner _draggingCorner;
    private Point _rectangleStart;
    private bool _drawingRectangle;
    private Point _lastPoint;
    private Point _arrowStart;
    private bool _initialized;
    private bool _updatingFields;
    private bool _moving;
    private bool _drawingArrow;
    private bool _saving;
    private bool _closed;
    private bool _closingForShutdown;

    public ImageEditorWindow(BitmapSource image, Func<BitmapSource, Task> saveAsync)
    {
        _image = image;
        _saveAsync = saveAsync;
        ImageProcessor.ValidateDimensions(image.PixelWidth, image.PixelHeight);
        InitializeComponent();
        ApplyEditorPalette();
        ThemeManager.ThemeChanged += OnEditorThemeChanged;
        ImageCanvas.SetImage(image);
        InitializeFonts();
        _fontSize = Math.Clamp(Math.Min(image.PixelWidth, image.PixelHeight) / 20.0, 16, 96);
        _arrowThickness = Math.Clamp(Math.Min(image.PixelWidth, image.PixelHeight) / 180.0, 3, 14);
        FontSizeBox.Text = _fontSize.ToString("0.##", CultureInfo.CurrentCulture);
        ArrowThicknessBox.Text = _arrowThickness.ToString("0.##", CultureInfo.CurrentCulture);
        ImageCanvas.SelectionChanged += OnSelectionChanged;
        ImageCanvas.Annotations.CollectionChanged += (_, _) => UpdateSaveState();
        TextCompositionManager.AddPreviewTextInputStartHandler(AnnotationTextBox, (_, _) => _isComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(AnnotationTextBox, (_, _) => _isComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(AnnotationTextBox, (_, _) => _isComposing = false);
        _initialized = true;
        ApplyColorText();
        SetTool(EditorTool.Text);
        ThemeManager.RegisterWindow(this);
        PreviewKeyDown += OnEditorKeyDown;
        Closing += (_, args) => { if (_saving && !_closingForShutdown) args.Cancel = true; };
        Closed += (_, _) => { _closed = true; ThemeManager.ThemeChanged -= OnEditorThemeChanged; };
        Loaded += (_, _) => { UpdatePreviewScale(); ImageCanvas.Focus(); };
        Rect workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
    }

    private void OnEditorThemeChanged(object? sender, EventArgs args) => ApplyEditorPalette();

    private void ApplyEditorPalette()
    {
        var colors = new Dictionary<string, (string Light, string Dark)>
        {
            ["EditorWindowFill"] = ("#F6F8FA", "#20262E"),
            ["EditorRibbonFill"] = ("#FFFFFF", "#252E38"),
            ["EditorWorkspaceFill"] = ("#EDF1F4", "#191F27"),
            ["EditorControlFill"] = ("#FFFFFF", "#303A45"),
            ["EditorToolFill"] = ("#F5F7F9", "#2B3540"),
            ["EditorControlBorder"] = ("#D5DDE3", "#566574"),
            ["EditorDividerBrush"] = ("#E1E7EC", "#414E5B"),
            ["EditorHoverFill"] = ("#EDF2F5", "#3A4855"),
            ["EditorHoverBorder"] = ("#A6B8BD", "#82979E"),
            ["EditorPressedFill"] = ("#DEE8EC", "#253C48"),
            ["EditorSelectedFill"] = ("#E3F2ED", "#234D42"),
            ["EditorSelectedHoverFill"] = ("#D6EDE5", "#2B5C4E"),
            ["EditorSelectedPressedFill"] = ("#C0E0D4", "#33695A"),
            ["EditorAccentBorder"] = ("#2E8979", "#61BDA4"),
            ["EditorAccentText"] = ("#186B5C", "#A1E5D0"),
            ["EditorDisabledFill"] = ("#F0F3F5", "#28323C"),
            ["EditorDisabledText"] = ("#7C8993", "#91A0AB"),
            ["EditorPrimaryFill"] = ("#137D68", "#23866F"),
            ["EditorPrimaryHoverFill"] = ("#0F705E", "#2C9C81"),
            ["EditorPrimaryPressedFill"] = ("#0C5D4F", "#1B6D5B"),
            ["EditorDangerText"] = ("#AD434D", "#F2A3AA"),
            ["EditorDangerHoverFill"] = ("#FBECEE", "#4B2F38"),
            ["EditorPopupFill"] = ("#FFFFFF", "#2B3540")
        };
        foreach (var (key, color) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ThemeManager.IsDark ? color.Dark : color.Light));
            brush.Freeze();
            Resources[key] = brush;
        }
    }

    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    private void OnSelectTool(object sender, RoutedEventArgs args) => SetTool(EditorTool.Select);
    private void OnTextTool(object sender, RoutedEventArgs args) => SetTool(EditorTool.Text);
    private void OnArrowTool(object sender, RoutedEventArgs args) => SetTool(EditorTool.Arrow);
    private void OnRectangleTool(object sender, RoutedEventArgs args) => SetTool(EditorTool.Rectangle);

    private void SetTool(EditorTool tool)
    {
        FinishTextEditing(false);
        _textPointer = _textInsertionPoint = null;
        CancelPointerAction();
        _tool = tool;
        if (tool != EditorTool.Select) ImageCanvas.SelectedIndex = -1;
        foreach ((Button button, EditorTool kind) in new[]
        {
            (SelectToolButton, EditorTool.Select), (TextToolButton, EditorTool.Text),
            (ArrowToolButton, EditorTool.Arrow), (RectangleToolButton, EditorTool.Rectangle)
        })
        {
            button.Tag = tool == kind ? "SelectedTool" : null;
            button.SetResourceReference(BackgroundProperty, tool == kind ? "EditorSelectedFill" : "EditorToolFill");
            button.SetResourceReference(ForegroundProperty, tool == kind ? "EditorAccentText" : "TextPrimaryBrush");
            button.SetResourceReference(BorderBrushProperty, tool == kind ? "EditorAccentBorder" : "EditorControlBorder");
        }
        ImageCanvas.Cursor = tool == EditorTool.Text ? Cursors.IBeam : tool is EditorTool.Arrow or EditorTool.Rectangle ? Cursors.Cross : Cursors.Arrow;
        SetStatus(tool switch
        {
            EditorTool.Text => "点击图片直接输入文字 · Enter 换行 · Esc 完成编辑",
            EditorTool.Arrow => "在图片上按住鼠标左键，拖动到箭头指向的位置后松开。",
            EditorTool.Rectangle => "在图片上拖动绘制矩形框；选中后拖动四角可缩放，拖动边框可移动。",
            _ => "拖动标注可移动 · 双击文字可修改 · 拖动形状控制点可调整大小和方向"
        });
        UpdateSaveState();
    }

    private void InitializeFonts()
    {
        var installed = Fonts.SystemFontFamilies.ToArray();
        FontChoice[] common =
        [
            new("Microsoft YaHei UI", "微软雅黑 UI"), new("Microsoft YaHei", "微软雅黑"),
            new("SimSun", "宋体 (SimSun)"), new("SimHei", "黑体 (SimHei)"), new("KaiTi", "楷体 (KaiTi)"),
            new("FangSong", "仿宋 (FangSong)"), new("Times New Roman", "Times New Roman"),
            new("Arial", "Arial"), new("Calibri", "Calibri"), new("Segoe UI", "Segoe UI"),
            new("Consolas", "Consolas"), new("Courier New", "Courier New")
        ];
        FontFamilyBox.ItemsSource = common.Where(choice => installed.Any(font =>
            font.Source.Equals(choice.FamilyName, StringComparison.OrdinalIgnoreCase)
            || font.FamilyNames.Values.Any(name => name.Equals(choice.FamilyName, StringComparison.OrdinalIgnoreCase)))).ToArray();
        FontFamilyBox.SelectedValue = _fontFamily;
    }

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_initialized || _updatingFields || FontFamilyBox.SelectedValue is not string family) return;
        _fontFamily = family;
        AnnotationTextBox.FontFamily = new FontFamily(family);
        if (ImageCanvas.SelectedAnnotation is TextImageAnnotation text)
            ImageCanvas.ReplaceSelection(ImageCanvas.FitText(text with { FontFamily = family }));
        UpdateTextPreview();
        UpdateSaveState();
    }

    internal void UpdateTextPointer(Point? position)
    {
        if (_tool != EditorTool.Text || _saving || IsEditingText) return;
        _textPointer = position;
        UpdateTextPreview();
    }

    private void OnImageMouseLeave(object sender, MouseEventArgs args) => UpdateTextPointer(null);

    private void UpdateTextPreview()
    {
        if (!_initialized || _tool != EditorTool.Text || IsEditingText) return;
        ImageCanvas.PreviewAnnotation = _textPointer is Point point
            ? ImageCanvas.FitText(new TextImageAnnotation(string.Empty, point, _color, _fontSize, _fontFamily))
            : null;
    }

    internal void PlaceText(Point position)
    {
        if (_tool != EditorTool.Text || _saving || !ApplyColorText() || !ApplySizeInputs()) return;
        FinishTextEditing(false);
        ImageCanvas.SelectedIndex = -1;
        var draft = ImageCanvas.FitText(new TextImageAnnotation(string.Empty, position, _color, _fontSize, _fontFamily));
        _textInsertionPoint = draft.Position;
        _textPointer = null;
        _updatingFields = true;
        AnnotationTextBox.Clear();
        _updatingFields = false;
        OpenTextEditor();
    }

    internal bool TryEditTextAt(Point point)
    {
        if (_saving) return false;
        int index = ImageCanvas.HitTestAnnotation(point);
        if (index < 0 || ImageCanvas.Annotations[index] is not TextImageAnnotation text) return false;
        SetTool(EditorTool.Select);
        ImageCanvas.SelectedIndex = index;
        _editingTextIndex = index;
        _textInsertionPoint = text.Position;
        _updatingFields = true;
        AnnotationTextBox.Text = text.Text;
        _updatingFields = false;
        OpenTextEditor();
        return true;
    }

    private void OpenTextEditor()
    {
        ImageCanvas.PreviewAnnotation = null;
        TextEditFrame.Visibility = Visibility.Visible;
        ImageCanvas.EditingTextIndex = _editingTextIndex;
        UpdateSaveState();
        AnnotationTextBox.Focus();
        AnnotationTextBox.CaretIndex = AnnotationTextBox.Text.Length;
        SetStatus("直接输入文字 · Enter 换行 · Esc 完成 · 拖动边框移动");
    }

    internal void FinishTextEditing(bool selectTool = true)
    {
        if (!IsEditingText) return;
        int index = _editingTextIndex;
        TextEditFrame.Visibility = Visibility.Collapsed;
        _editingTextIndex = -1;
        _isComposing = false;
        _textInsertionPoint = _textPointer = null;
        ImageCanvas.EditingTextIndex = -1;
        if (index >= 0 && index < ImageCanvas.Annotations.Count
            && ImageCanvas.Annotations[index] is TextImageAnnotation text && string.IsNullOrWhiteSpace(text.Text))
        {
            int selection = ImageCanvas.SelectedIndex;
            ImageCanvas.SelectedIndex = -1;
            ImageCanvas.Annotations.RemoveAt(index);
            ImageCanvas.SelectedIndex = selection == index ? -1 : selection > index ? selection - 1 : selection;
        }
        if (selectTool) SetTool(EditorTool.Select);
        else UpdateSaveState();
    }

    private void RefreshInlineEditor()
    {
        if (!IsEditingText || _textInsertionPoint is not Point position) return;
        var text = _editingTextIndex >= 0 && _editingTextIndex < ImageCanvas.Annotations.Count
            ? (TextImageAnnotation)ImageCanvas.Annotations[_editingTextIndex]
            : new TextImageAnnotation(AnnotationTextBox.Text, position, _color, _fontSize, _fontFamily);
        _textInsertionPoint = text.Position;
        double scale = Math.Max(0.01, ImageCanvas.DisplayScale);
        double padding = 4 / scale;
        double border = 1 / scale;
        TextImageAnnotation measured = string.IsNullOrWhiteSpace(text.Text)
            ? text with { Text = "在此输入文字", FontSize = Math.Max(text.FontSize, 12 / scale) } : text;
        Rect bounds = ImageAnnotationRenderer.GetBounds(measured);
        double bodyWidth = Math.Min(Math.Max(120 / scale, bounds.Width + 4 / scale), Math.Max(1, _image.PixelWidth - text.Position.X));
        double bodyHeight = Math.Max(24 / scale, bounds.Height + 2 / scale);
        TextEditFrame.BorderThickness = new Thickness(border);
        TextEditFrame.Padding = new Thickness(padding);
        TextEditFrame.Width = bodyWidth + 2 * (padding + border);
        TextEditFrame.Height = bodyHeight + 2 * (padding + border);
        Canvas.SetLeft(TextEditFrame, text.Position.X - padding - border);
        Canvas.SetTop(TextEditFrame, text.Position.Y - padding - border);
        AnnotationTextBox.FontFamily = new FontFamily(text.FontFamily);
        AnnotationTextBox.FontSize = text.FontSize;
        AnnotationTextBox.Foreground = new SolidColorBrush(text.Color);
        InlinePlaceholder.FontSize = Math.Max(text.FontSize, 12 / scale);
        InlinePlaceholder.Visibility = string.IsNullOrEmpty(AnnotationTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTextFrameMouseDown(object sender, MouseButtonEventArgs args)
    {
        Point local = args.GetPosition(TextEditFrame);
        double edge = 5 / Math.Max(0.01, ImageCanvas.DisplayScale);
        if (local.X > edge && local.X < TextEditFrame.ActualWidth - edge
            && local.Y > edge && local.Y < TextEditFrame.ActualHeight - edge) return;
        Point point = ImagePoint(args);
        FinishTextEditing();
        _lastPoint = point;
        _moving = ImageCanvas.SelectedAnnotation is not null;
        if (_moving) ImageCanvas.CaptureMouse();
        args.Handled = true;
    }

    private Point ImagePoint(MouseEventArgs args)
    {
        Point point = args.GetPosition(ImageCanvas);
        return new Point(Math.Clamp(point.X, 0, ImageCanvas.Width), Math.Clamp(point.Y, 0, ImageCanvas.Height));
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (_saving || !ApplyColorText() || !ApplySizeInputs()) return;
        Point point = ImagePoint(args);
        if (IsEditingText) FinishTextEditing();
        if (args.ClickCount == 2 && TryEditTextAt(point)) { args.Handled = true; return; }
        ImageCanvas.Focus();
        args.Handled = true;
        if (_tool == EditorTool.Text)
        {
            PlaceText(point);
        }
        else if (_tool == EditorTool.Rectangle)
        {
            ImageCanvas.SelectedIndex = -1;
            _rectangleStart = point;
            _drawingRectangle = true;
            ImageCanvas.CaptureMouse();
        }
        else if (_tool == EditorTool.Arrow)
        {
            ImageCanvas.SelectedIndex = -1;
            _arrowStart = point;
            _drawingArrow = true;
            ImageCanvas.CaptureMouse();
        }
        else
        {
            _draggingCorner = ImageCanvas.HitTestRectangleCorner(point);
            if (_draggingCorner != RectangleCorner.None)
            {
                ImageCanvas.CaptureMouse();
                return;
            }
            _draggingEndpoint = ImageCanvas.HitTestArrowEndpoint(point);
            if (_draggingEndpoint != ArrowEndpoint.None)
            {
                ImageCanvas.CaptureMouse();
                return;
            }
            ImageCanvas.SelectedIndex = ImageCanvas.HitTestAnnotation(point);
            if (ImageCanvas.SelectedAnnotation is null) return;
            _moving = true;
            _lastPoint = point;
            ImageCanvas.CaptureMouse();
        }
    }

    private void OnImageMouseMove(object sender, MouseEventArgs args)
    {
        if (_saving || IsEditingText) return;
        Point point = ImagePoint(args);
        if (_tool == EditorTool.Text)
        {
            UpdateTextPointer(point);
            return;
        }
        if (args.LeftButton != MouseButtonState.Pressed)
        {
            if (_tool == EditorTool.Select)
            {
                RectangleCorner corner = ImageCanvas.HitTestRectangleCorner(point);
                ImageCanvas.Cursor = corner is RectangleCorner.TopLeft or RectangleCorner.BottomRight ? Cursors.SizeNWSE
                    : corner is RectangleCorner.TopRight or RectangleCorner.BottomLeft ? Cursors.SizeNESW
                    : ImageCanvas.HitTestArrowEndpoint(point) != ArrowEndpoint.None ? Cursors.Cross
                    : ImageCanvas.HitTestAnnotation(point) >= 0 ? Cursors.SizeAll : Cursors.Arrow;
            }
            return;
        }
        if (_drawingArrow)
        {
            ImageCanvas.PreviewAnnotation = new ArrowImageAnnotation(_arrowStart, point, _color, _arrowThickness);
        }
        else if (_drawingRectangle)
        {
            ImageCanvas.PreviewAnnotation = new RectangleImageAnnotation(new Rect(_rectangleStart, point), _color, _arrowThickness);
        }
        else if (_draggingCorner != RectangleCorner.None)
        {
            _draggingCorner = ImageCanvas.ResizeRectangleCorner(_draggingCorner, point);
        }
        else if (_draggingEndpoint != ArrowEndpoint.None)
        {
            ImageCanvas.MoveArrowEndpoint(_draggingEndpoint, point);
        }
        else if (_moving)
        {
            ImageCanvas.MoveSelection(point - _lastPoint);
            _lastPoint = point;
        }
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (_drawingRectangle)
        {
            ImageCanvas.AddRectangle(_rectangleStart, ImagePoint(args), _color, _arrowThickness);
            SetTool(EditorTool.Select);
        }
        if (_drawingArrow)
        {
            Point end = ImagePoint(args);
            if ((end - _arrowStart).Length >= 3 / Math.Max(0.01, ImageCanvas.DisplayScale))
            {
                ImageCanvas.Annotations.Add(new ArrowImageAnnotation(_arrowStart, end, _color, _arrowThickness));
                ImageCanvas.SelectedIndex = ImageCanvas.Annotations.Count - 1;
            }
            SetTool(EditorTool.Select);
        }
        if (_draggingCorner != RectangleCorner.None)
            _draggingCorner = ImageCanvas.ResizeRectangleCorner(_draggingCorner, ImagePoint(args));
        if (_draggingEndpoint != ArrowEndpoint.None)
            ImageCanvas.MoveArrowEndpoint(_draggingEndpoint, ImagePoint(args));
        CancelPointerAction();
        args.Handled = true;
    }

    private void OnImageLostCapture(object sender, MouseEventArgs args)
    {
        _moving = false;
        _drawingArrow = false;
        _drawingRectangle = false;
        _draggingEndpoint = ArrowEndpoint.None;
        _draggingCorner = RectangleCorner.None;
        ImageCanvas.PreviewAnnotation = null;
        UpdateTextPreview();
    }

    private void CancelPointerAction()
    {
        _moving = false;
        _drawingArrow = false;
        _drawingRectangle = false;
        _draggingEndpoint = ArrowEndpoint.None;
        _draggingCorner = RectangleCorner.None;
        ImageCanvas.PreviewAnnotation = null;
        if (ImageCanvas.IsMouseCaptured) ImageCanvas.ReleaseMouseCapture();
        UpdateTextPreview();
    }

    private void OnSelectionChanged(object? sender, EventArgs args)
    {
        if (IsEditingText && ImageCanvas.SelectedIndex != _editingTextIndex) FinishTextEditing(false);
        _updatingFields = true;
        if (ImageCanvas.SelectedAnnotation is ImageAnnotation annotation)
        {
            _color = annotation.Color;
            ColorBox.Text = $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}";
            ColorSwatch.Background = new SolidColorBrush(_color);
            if (annotation is TextImageAnnotation text)
            {
                if (!IsEditingText) AnnotationTextBox.Text = text.Text;
                _fontFamily = text.FontFamily;
                FontFamilyBox.SelectedValue = _fontFamily;
                AnnotationTextBox.FontFamily = new FontFamily(_fontFamily);
                _fontSize = text.FontSize;
                FontSizeBox.Text = _fontSize.ToString("0.##", CultureInfo.CurrentCulture);
            }
            else if (annotation is ArrowImageAnnotation arrow)
            {
                _arrowThickness = arrow.Thickness;
                ArrowThicknessBox.Text = _arrowThickness.ToString("0.##", CultureInfo.CurrentCulture);
            }
            else if (annotation is RectangleImageAnnotation rectangle)
            {
                _arrowThickness = rectangle.Thickness;
                ArrowThicknessBox.Text = _arrowThickness.ToString("0.##", CultureInfo.CurrentCulture);
            }
        }
        _updatingFields = false;
        UpdateSaveState();
    }

    private void OnAnnotationTextChanged(object sender, TextChangedEventArgs args)
    {
        if (!_initialized || _updatingFields) return;
        if (IsEditingText && _textInsertionPoint is Point position)
        {
            var changed = ImageCanvas.FitText(new TextImageAnnotation(AnnotationTextBox.Text, position, _color, _fontSize, _fontFamily));
            if (_editingTextIndex >= 0)
                ImageCanvas.Annotations[_editingTextIndex] = changed;
            else if (!string.IsNullOrWhiteSpace(changed.Text))
            {
                _editingTextIndex = ImageCanvas.Annotations.Count;
                ImageCanvas.Annotations.Add(changed);
                ImageCanvas.EditingTextIndex = _editingTextIndex;
                ImageCanvas.SelectedIndex = _editingTextIndex;
            }
            _textInsertionPoint = changed.Position;
        }
        else if (ImageCanvas.SelectedAnnotation is TextImageAnnotation text)
            ImageCanvas.ReplaceSelection(ImageCanvas.FitText(text with { Text = AnnotationTextBox.Text }));
        UpdateSaveState();
    }

    private static bool TryReadSize(TextBox box, double minimum, double maximum, out double size) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out size)
        && double.IsFinite(size) && size >= minimum && size <= maximum;

    private void OnFontSizeChanged(object sender, TextChangedEventArgs args)
    {
        if (!_initialized || _updatingFields) return;
        if (TryReadSize(FontSizeBox, 6, 512, out double size))
        {
            _fontSize = size;
            ImageCanvas.ResizeSelectedText(size);
            UpdateTextPreview();
        }
        UpdateSaveState();
    }

    private void OnArrowThicknessChanged(object sender, TextChangedEventArgs args)
    {
        if (!_initialized || _updatingFields) return;
        if (TryReadSize(ArrowThicknessBox, 1, 64, out double size))
        {
            _arrowThickness = size;
            ImageCanvas.ResizeSelectedArrow(size);
            ImageCanvas.ResizeSelectedRectangle(size);
        }
        UpdateSaveState();
    }

    private void OnDecreaseFontSize(object sender, RoutedEventArgs args) => StepSize(FontSizeBox, _fontSize, -2, 6, 512);
    private void OnIncreaseFontSize(object sender, RoutedEventArgs args) => StepSize(FontSizeBox, _fontSize, 2, 6, 512);
    private void OnDecreaseArrowThickness(object sender, RoutedEventArgs args) => StepSize(ArrowThicknessBox, _arrowThickness, -1, 1, 64);
    private void OnIncreaseArrowThickness(object sender, RoutedEventArgs args) => StepSize(ArrowThicknessBox, _arrowThickness, 1, 1, 64);

    private static void StepSize(TextBox box, double current, double step, double minimum, double maximum) =>
        box.Text = Math.Clamp(current + step, minimum, maximum).ToString("0.##", CultureInfo.CurrentCulture);

    private void OnSizeLostFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (_initialized) ApplySizeInputs();
    }

    private bool SizeInputsAreValid =>
        (!FontSizePanel.IsEnabled || TryReadSize(FontSizeBox, 6, 512, out _))
        && (!ArrowThicknessPanel.IsEnabled || TryReadSize(ArrowThicknessBox, 1, 64, out _));

    private bool ApplySizeInputs()
    {
        if (FontSizePanel.IsEnabled && !TryReadSize(FontSizeBox, 6, 512, out _))
        {
            SetStatus("字号请输入 6–512 之间的数字（像素）。", true);
            return false;
        }
        if (ArrowThicknessPanel.IsEnabled && !TryReadSize(ArrowThicknessBox, 1, 64, out _))
        {
            SetStatus("线条粗细请输入 1–64 之间的数字（像素）。", true);
            return false;
        }
        return true;
    }

    private void OnColorTextChanged(object sender, TextChangedEventArgs args)
    {
        if (!_initialized || _updatingFields) return;
        if (TryReadColor(out Color color)) ApplyColor(color);
        UpdateSaveState();
    }

    private void OnColorLostFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (_initialized) ApplyColorText();
    }

    private bool TryReadColor(out Color color)
    {
        string value = ColorBox.Text.Trim().TrimStart('#');
        if (value.Length == 6 && uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            color = Color.FromRgb((byte)((rgb >> 16) & 0xff), (byte)((rgb >> 8) & 0xff), (byte)(rgb & 0xff));
            return true;
        }
        color = default;
        return false;
    }

    private bool ApplyColorText()
    {
        if (!TryReadColor(out Color color))
        {
            SetStatus("颜色格式应为 #RRGGBB，例如 #FF6600；也可点击“颜色…”选择。", true);
            return false;
        }
        ApplyColor(color);
        return true;
    }

    private void ApplyColor(Color color)
    {
        _color = color;
        ColorSwatch.Background = new SolidColorBrush(color);
        if (ImageCanvas.SelectedAnnotation is TextImageAnnotation text)
            ImageCanvas.ReplaceSelection(text with { Color = color });
        else if (ImageCanvas.SelectedAnnotation is ArrowImageAnnotation arrow)
            ImageCanvas.ReplaceSelection(arrow with { Color = color });
        else if (ImageCanvas.SelectedAnnotation is RectangleImageAnnotation rectangle)
            ImageCanvas.ReplaceSelection(rectangle with { Color = color });
        UpdateTextPreview();
    }

    private void OnPickColor(object sender, RoutedEventArgs args)
    {
        Color? color = AnnotationColorPicker.Pick(this, _color);
        if (color is not null)
        {
            ColorBox.Text = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
            SetStatus("颜色已更新。选中已有标注可修改其颜色。");
        }
    }

    private void OnDeleteSelection(object sender, RoutedEventArgs args)
    {
        FinishTextEditing(false);
        ImageCanvas.DeleteSelection();
    }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args) { if (_initialized) UpdatePreviewScale(); }

    private void UpdatePreviewScale()
    {
        ImageCanvas.DisplayScale = Math.Min(1, Math.Min(Viewport.ActualWidth / _image.PixelWidth, Viewport.ActualHeight / _image.PixelHeight));
        ImageCanvas.InvalidateVisual();
        UpdateTextPreview();
        RefreshInlineEditor();
        ImageInfoText.Text = $"{_image.PixelWidth} × {_image.PixelHeight} · 预览 {ImageCanvas.DisplayScale:P0} · 按原尺寸保存，保留原图";
    }

    private void UpdateSaveState()
    {
        bool shape = ImageCanvas.SelectedAnnotation is ArrowImageAnnotation or RectangleImageAnnotation
            || ImageCanvas.SelectedAnnotation is null && _tool is EditorTool.Arrow or EditorTool.Rectangle;
        FontPropertiesGroup.Visibility = shape ? Visibility.Collapsed : Visibility.Visible;
        ShapePropertiesGroup.Visibility = shape ? Visibility.Visible : Visibility.Collapsed;
        PropertiesGroupTitle.Text = shape ? "形状" : "字体";
        ColorLabel.Text = shape ? "线条颜色" : "文字颜色";
        RefreshInlineEditor();
        FontSizePanel.IsEnabled = !shape;
        FontFamilyBox.IsEnabled = !shape;
        ArrowThicknessPanel.IsEnabled = shape;
        SaveButton.IsEnabled = !_saving && TryReadColor(out _) && SizeInputsAreValid && ImageCanvas.Annotations.Any(annotation =>
            annotation is ArrowImageAnnotation or RectangleImageAnnotation || annotation is TextImageAnnotation text && !string.IsNullOrWhiteSpace(text.Text));
        DeleteButton.IsEnabled = !_saving && ImageCanvas.SelectedAnnotation is not null;
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorTextBrush" : "TextSecondaryBrush");
    }

    private async void OnSave(object sender, RoutedEventArgs args)
    {
        if (_saving || !SaveButton.IsEnabled || !ApplyColorText() || !ApplySizeInputs()) return;
        FinishTextEditing(false);
        CancelPointerAction();
        _saving = true;
        Toolbar.IsEnabled = Viewport.IsEnabled = CancelButton.IsEnabled = false;
        UpdateSaveState();
        SetStatus("正在保存并复制图片…");
        try
        {
            BitmapSource image = ImageAnnotationRenderer.Render(_image, ImageCanvas.Annotations);
            await _saveAsync(image);
            if (!_closed) { _saving = false; Close(); }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                AppLogger.LogException("保存标注图片失败", exception);
                SetStatus($"保存或复制失败：{exception.Message} 标注已保留，可重试。", true);
            }
        }
        finally
        {
            _saving = false;
            if (!_closed)
            {
                Toolbar.IsEnabled = Viewport.IsEnabled = CancelButton.IsEnabled = true;
                UpdateSaveState();
            }
        }
    }

    private void OnCancel(object sender, RoutedEventArgs args) => Close();

    private void OnEditorKeyDown(object sender, KeyEventArgs args)
    {
        if (_saving) return;
        if (IsEditingText && _isComposing) return;
        if (args.Key == Key.Escape)
        {
            if (IsEditingText)
            {
                FinishTextEditing();
                ImageCanvas.Focus();
                args.Handled = true;
                return;
            }
            if (_textInsertionPoint is not null || ImageCanvas.PreviewAnnotation is TextImageAnnotation)
            {
                _textPointer = _textInsertionPoint = null;
                UpdateTextPreview();
                SetStatus("已取消文字位置。可重新点击图片放置。");
            }
            else if (_drawingArrow || _drawingRectangle || _moving || _draggingEndpoint != ArrowEndpoint.None || _draggingCorner != RectangleCorner.None) CancelPointerAction();
            else Close();
            args.Handled = true;
        }
        else if (args.Key == Key.Delete && Keyboard.FocusedElement is not TextBoxBase)
        {
            ImageCanvas.DeleteSelection();
            args.Handled = true;
        }
    }
}
