using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Models;
using ClipboardManager.Services;

namespace ClipboardManager.Controls;

public enum ArrowEndpoint { None, Start, End }
public enum RectangleCorner { None, TopLeft, TopRight, BottomRight, BottomLeft }

/// <summary>All annotation coordinates are original-image pixels, independent of the preview scale.</summary>
public sealed class ImageAnnotationCanvas : FrameworkElement
{
    private int _selectedIndex = -1;
    private int _editingTextIndex = -1;
    private BitmapSource? _image;
    private ImageAnnotation? _previewAnnotation;

    public ImageAnnotationCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Annotations.CollectionChanged += (_, _) => InvalidateVisual();
    }

    public ObservableCollection<ImageAnnotation> Annotations { get; } = [];
    public event EventHandler? SelectionChanged;
    public double DisplayScale { get; set; } = 1;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value >= 0 && value < Annotations.Count ? value : -1;
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ImageAnnotation? SelectedAnnotation => _selectedIndex >= 0 && _selectedIndex < Annotations.Count
        ? Annotations[_selectedIndex] : null;

    public int EditingTextIndex
    {
        get => _editingTextIndex;
        set { _editingTextIndex = value; InvalidateVisual(); }
    }

    public ImageAnnotation? PreviewAnnotation
    {
        get => _previewAnnotation;
        set { _previewAnnotation = value; InvalidateVisual(); }
    }

    public void SetImage(BitmapSource image)
    {
        _image = image;
        Width = image.PixelWidth;
        Height = image.PixelHeight;
        InvalidateVisual();
    }

    public int HitTestAnnotation(Point point)
    {
        for (int index = Annotations.Count - 1; index >= 0; index--)
            if (ImageAnnotationRenderer.HitTest(Annotations[index], point, 6 / Math.Max(0.01, DisplayScale))) return index;
        return -1;
    }

    public TextImageAnnotation FitText(TextImageAnnotation text)
    {
        Rect bounds = ImageAnnotationRenderer.GetBounds(PlacementText(text));
        return text with { Position = new Point(
            Math.Clamp(text.Position.X, 0, Math.Max(0, Width - bounds.Width)),
            Math.Clamp(text.Position.Y, 0, Math.Max(0, Height - bounds.Height))) };
    }

    public void AddText(string text, Point position, Color color, double fontSize, string fontFamily = "Microsoft YaHei UI")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Annotations.Add(FitText(new TextImageAnnotation(text, position, color, fontSize, fontFamily)));
        SelectedIndex = Annotations.Count - 1;
    }

    private TextImageAnnotation PlacementText(TextImageAnnotation text) => string.IsNullOrWhiteSpace(text.Text)
        ? text with { Text = "输入文字", Color = Colors.DimGray, FontSize = Math.Max(text.FontSize, 12 / Math.Max(0.01, DisplayScale)) }
        : text;

    public bool AddRectangle(Point start, Point end, Color color, double thickness)
    {
        start = ClampPoint(start);
        end = ClampPoint(end);
        var bounds = new Rect(start, end);
        double minimum = 3 / Math.Max(0.01, DisplayScale);
        if (bounds.Width < minimum || bounds.Height < minimum) return false;
        Annotations.Add(new RectangleImageAnnotation(bounds, color, thickness));
        SelectedIndex = Annotations.Count - 1;
        return true;
    }

    public RectangleCorner HitTestRectangleCorner(Point point)
    {
        if (SelectedAnnotation is not RectangleImageAnnotation rectangle) return RectangleCorner.None;
        double nearest = 8 / Math.Max(0.01, DisplayScale);
        RectangleCorner hit = RectangleCorner.None;
        foreach (var handle in RectangleHandles(rectangle.Bounds))
        {
            double distance = (point - handle.Position).Length;
            if (distance > nearest) continue;
            nearest = distance;
            hit = handle.Corner;
        }
        return hit;
    }

    public RectangleCorner ResizeRectangleCorner(RectangleCorner corner, Point position)
    {
        if (SelectedAnnotation is not RectangleImageAnnotation rectangle || corner == RectangleCorner.None
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return corner;
        Point anchor = corner switch
        {
            RectangleCorner.TopLeft => rectangle.Bounds.BottomRight,
            RectangleCorner.TopRight => rectangle.Bounds.BottomLeft,
            RectangleCorner.BottomRight => rectangle.Bounds.TopLeft,
            _ => rectangle.Bounds.TopRight
        };
        position = ClampPoint(position);
        var bounds = new Rect(anchor, position);
        double minimum = 3 / Math.Max(0.01, DisplayScale);
        if (bounds.Width < minimum || bounds.Height < minimum) return corner;
        ReplaceSelection(rectangle with { Bounds = bounds });
        return position.X < anchor.X
            ? position.Y < anchor.Y ? RectangleCorner.TopLeft : RectangleCorner.BottomLeft
            : position.Y < anchor.Y ? RectangleCorner.TopRight : RectangleCorner.BottomRight;
    }

    public void ResizeSelectedRectangle(double thickness)
    {
        if (SelectedAnnotation is RectangleImageAnnotation rectangle && double.IsFinite(thickness))
            ReplaceSelection(rectangle with { Thickness = Math.Clamp(thickness, 1, 64) });
    }

    private Point ClampPoint(Point point) => new(Math.Clamp(point.X, 0, Width), Math.Clamp(point.Y, 0, Height));

    private static (RectangleCorner Corner, Point Position)[] RectangleHandles(Rect bounds) =>
    [
        (RectangleCorner.TopLeft, bounds.TopLeft), (RectangleCorner.TopRight, bounds.TopRight),
        (RectangleCorner.BottomRight, bounds.BottomRight), (RectangleCorner.BottomLeft, bounds.BottomLeft)
    ];

    public void ReplaceSelection(ImageAnnotation annotation)
    {
        if (SelectedAnnotation is null) return;
        Annotations[SelectedIndex] = annotation;
    }

    public void ResizeSelectedText(double fontSize)
    {
        if (SelectedAnnotation is not TextImageAnnotation text || !double.IsFinite(fontSize)) return;
        ReplaceSelection(FitText(text with { FontSize = Math.Clamp(fontSize, 6, 512) }));
    }

    public void ResizeSelectedArrow(double thickness)
    {
        if (SelectedAnnotation is ArrowImageAnnotation arrow && double.IsFinite(thickness))
            ReplaceSelection(arrow with { Thickness = Math.Clamp(thickness, 1, 64) });
    }

    public ArrowEndpoint HitTestArrowEndpoint(Point point)
    {
        if (SelectedAnnotation is not ArrowImageAnnotation arrow) return ArrowEndpoint.None;
        double startDistance = (point - arrow.Start).Length;
        double endDistance = (point - arrow.End).Length;
        double radius = 8 / Math.Max(0.01, DisplayScale);
        if (endDistance <= radius && endDistance <= startDistance) return ArrowEndpoint.End;
        return startDistance <= radius ? ArrowEndpoint.Start : ArrowEndpoint.None;
    }

    public void MoveArrowEndpoint(ArrowEndpoint endpoint, Point position)
    {
        if (SelectedAnnotation is not ArrowImageAnnotation arrow || endpoint == ArrowEndpoint.None
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        position = new Point(Math.Clamp(position.X, 0, Width), Math.Clamp(position.Y, 0, Height));
        var changed = endpoint == ArrowEndpoint.Start ? arrow with { Start = position } : arrow with { End = position };
        if ((changed.End - changed.Start).Length < 3 / Math.Max(0.01, DisplayScale)) return;
        ReplaceSelection(changed);
    }

    public void MoveSelection(Vector delta)
    {
        if (SelectedAnnotation is TextImageAnnotation text)
        {
            Rect bounds = ImageAnnotationRenderer.GetBounds(text);
            ReplaceSelection(text with { Position = new Point(
                Math.Clamp(text.Position.X + delta.X, 0, Math.Max(0, Width - bounds.Width)),
                Math.Clamp(text.Position.Y + delta.Y, 0, Math.Max(0, Height - bounds.Height))) });
        }
        else if (SelectedAnnotation is RectangleImageAnnotation rectangle)
        {
            delta.X = Math.Clamp(delta.X, -rectangle.Bounds.Left, Width - rectangle.Bounds.Right);
            delta.Y = Math.Clamp(delta.Y, -rectangle.Bounds.Top, Height - rectangle.Bounds.Bottom);
            ReplaceSelection(rectangle with { Bounds = new Rect(rectangle.Bounds.TopLeft + delta, rectangle.Bounds.Size) });
        }
        else if (SelectedAnnotation is ArrowImageAnnotation arrow)
        {
            delta.X = Math.Clamp(delta.X, -Math.Min(arrow.Start.X, arrow.End.X), Width - Math.Max(arrow.Start.X, arrow.End.X));
            delta.Y = Math.Clamp(delta.Y, -Math.Min(arrow.Start.Y, arrow.End.Y), Height - Math.Max(arrow.Start.Y, arrow.End.Y));
            ReplaceSelection(arrow with { Start = arrow.Start + delta, End = arrow.End + delta });
        }
    }

    public void DeleteSelection()
    {
        if (SelectedAnnotation is null) return;
        int index = SelectedIndex;
        SelectedIndex = -1;
        Annotations.RemoveAt(index);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (_image is null) return;
        drawing.DrawImage(_image, new Rect(0, 0, Width, Height));
        for (int index = 0; index < Annotations.Count; index++)
            if (index != EditingTextIndex) ImageAnnotationRenderer.Draw(drawing, Annotations[index]);
        if (PreviewAnnotation is TextImageAnnotation preview)
        {
            TextImageAnnotation visible = PlacementText(preview);
            double scale = Math.Max(0.01, DisplayScale);
            Rect bounds = ImageAnnotationRenderer.GetBounds(visible);
            Rect frame = bounds;
            frame.Inflate(4 / scale, 4 / scale);
            if (string.IsNullOrWhiteSpace(preview.Text))
                drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), null, frame);
            ImageAnnotationRenderer.Draw(drawing, visible);
            drawing.DrawRectangle(null, new Pen(Brushes.White, 3 / scale), frame);
            drawing.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1 / scale) { DashStyle = DashStyles.Dash }, frame);
            Point caretTop = bounds.TopLeft;
            Point caretBottom = new(bounds.Left, bounds.Bottom);
            drawing.DrawLine(new Pen(Brushes.White, 3 / scale), caretTop, caretBottom);
            drawing.DrawLine(new Pen(Brushes.DodgerBlue, 1.5 / scale), caretTop, caretBottom);
        }
        else if (PreviewAnnotation is not null) ImageAnnotationRenderer.Draw(drawing, PreviewAnnotation);
        if (SelectedAnnotation is not null && SelectedIndex != EditingTextIndex)
        {
            double scale = Math.Max(0.01, DisplayScale);
            Rect bounds = ImageAnnotationRenderer.GetBounds(SelectedAnnotation);
            bounds.Inflate(4 / scale, 4 / scale);
            var pen = new Pen(Brushes.DodgerBlue, 1 / scale) { DashStyle = DashStyles.Dash };
            drawing.DrawRectangle(null, new Pen(Brushes.White, 3 / scale), bounds);
            drawing.DrawRectangle(null, pen, bounds);
            if (SelectedAnnotation is RectangleImageAnnotation rectangle)
            {
                var handlePen = new Pen(Brushes.DodgerBlue, 1.5 / scale);
                foreach (var handle in RectangleHandles(rectangle.Bounds))
                    drawing.DrawRectangle(Brushes.White, handlePen,
                        new Rect(handle.Position.X - 4 / scale, handle.Position.Y - 4 / scale, 8 / scale, 8 / scale));
            }
            else if (SelectedAnnotation is ArrowImageAnnotation arrow)
            {
                var handlePen = new Pen(Brushes.DodgerBlue, 1.5 / scale);
                drawing.DrawEllipse(Brushes.White, handlePen, arrow.Start, 5 / scale, 5 / scale);
                drawing.DrawEllipse(Brushes.White, handlePen, arrow.End, 5 / scale, 5 / scale);
            }
        }
    }
}
