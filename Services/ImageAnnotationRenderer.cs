using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipboardManager.Models;

namespace ClipboardManager.Services;

public static class ImageAnnotationRenderer
{
    public static BitmapSource Render(BitmapSource source, IEnumerable<ImageAnnotation> annotations)
    {
        ImageProcessor.ValidateDimensions(source.PixelWidth, source.PixelHeight);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, source.PixelWidth, source.PixelHeight)));
            drawing.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            foreach (ImageAnnotation annotation in annotations) Draw(drawing, annotation);
            drawing.Pop();
        }
        var result = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }

    public static void Draw(DrawingContext drawing, ImageAnnotation annotation)
    {
        var brush = new SolidColorBrush(annotation.Color);
        if (annotation is TextImageAnnotation text)
        {
            drawing.DrawText(FormatText(text), text.Position);
        }
        else if (annotation is RectangleImageAnnotation rectangle && rectangle.Bounds.Width > 0 && rectangle.Bounds.Height > 0)
        {
            drawing.DrawRectangle(null, new Pen(brush, rectangle.Thickness), rectangle.Bounds);
        }
        else if (annotation is ArrowImageAnnotation arrow && (arrow.End - arrow.Start).Length > 0.01)
        {
            (Point shaftEnd, StreamGeometry head) = ArrowHead(arrow);
            var pen = new Pen(brush, arrow.Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            drawing.DrawLine(pen, arrow.Start, shaftEnd);
            drawing.DrawGeometry(brush, null, head);
        }
    }

    public static Rect GetBounds(ImageAnnotation annotation)
    {
        if (annotation is TextImageAnnotation text)
        {
            FormattedText formatted = FormatText(text);
            return new Rect(text.Position, new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height));
        }
        if (annotation is RectangleImageAnnotation rectangle)
        {
            Rect bounds = rectangle.Bounds;
            if (!bounds.IsEmpty) bounds.Inflate(rectangle.Thickness / 2, rectangle.Thickness / 2);
            return bounds;
        }
        if (annotation is ArrowImageAnnotation arrow)
        {
            var bounds = new Rect(arrow.Start, arrow.End);
            if ((arrow.End - arrow.Start).Length > 0.01) bounds.Union(ArrowHead(arrow).Head.Bounds);
            bounds.Inflate(arrow.Thickness / 2, arrow.Thickness / 2);
            return bounds;
        }
        return Rect.Empty;
    }

    public static bool HitTest(ImageAnnotation annotation, Point point, double tolerance = 6)
    {
        if (annotation is RectangleImageAnnotation rectangle)
            return new RectangleGeometry(rectangle.Bounds).StrokeContains(new Pen(Brushes.Black, rectangle.Thickness + 2 * tolerance), point);
        if (annotation is not ArrowImageAnnotation arrow)
            return GetBounds(annotation).Contains(point);
        if ((arrow.End - arrow.Start).Length <= 0.01) return false;
        var line = new LineGeometry(arrow.Start, arrow.End);
        return line.StrokeContains(new Pen(Brushes.Black, arrow.Thickness + 2 * tolerance), point)
            || ArrowHead(arrow).Head.FillContains(point);
    }

    private static FormattedText FormatText(TextImageAnnotation text) => new(
        text.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(text.FontFamily), text.FontSize, new SolidColorBrush(text.Color), 1);

    private static (Point ShaftEnd, StreamGeometry Head) ArrowHead(ArrowImageAnnotation arrow)
    {
        Vector direction = arrow.End - arrow.Start;
        double length = direction.Length;
        direction.Normalize();
        double headLength = Math.Min(Math.Max(12, arrow.Thickness * 3.5), length * 0.65);
        Point back = arrow.End - direction * headLength;
        var perpendicular = new Vector(-direction.Y, direction.X) * (headLength * 0.46);
        var head = new StreamGeometry();
        using (StreamGeometryContext geometry = head.Open())
        {
            geometry.BeginFigure(arrow.End, true, true);
            geometry.LineTo(back + perpendicular, true, false);
            geometry.LineTo(back - perpendicular, true, false);
        }
        head.Freeze();
        return (back + direction * (arrow.Thickness / 2), head);
    }
}
