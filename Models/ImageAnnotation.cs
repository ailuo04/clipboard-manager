using System.Windows;
using System.Windows.Media;

namespace ClipboardManager.Models;

public abstract record ImageAnnotation(Color Color);
public sealed record TextImageAnnotation(string Text, Point Position, Color Color, double FontSize = 28, string FontFamily = "Microsoft YaHei UI") : ImageAnnotation(Color);
public sealed record ArrowImageAnnotation(Point Start, Point End, Color Color, double Thickness = 4) : ImageAnnotation(Color);
public sealed record RectangleImageAnnotation(Rect Bounds, Color Color, double Thickness = 4) : ImageAnnotation(Color);
