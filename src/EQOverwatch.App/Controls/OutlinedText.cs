using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EQOverwatch.App.Controls;

/// <summary>Text drawn with a black outline, so it reads over any background (floating combat text).</summary>
public sealed class OutlinedText : FrameworkElement
{
    private static readonly Typeface Normal = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.ExtraBold, FontStretches.Normal);
    private static readonly Pen Outline = CreatePen();

    private readonly Geometry _geometry;
    private readonly Brush _fill;

    public OutlinedText(string text, double size, bool bold, Brush fill)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Bold : Normal, size, fill, 1.0);
        _geometry = formatted.BuildGeometry(new Point(1.5, 1.5));
        _geometry.Freeze();
        _fill = fill;
        Width = formatted.WidthIncludingTrailingWhitespace + 3;
        Height = formatted.Height + 3;
        IsHitTestVisible = false;
    }

    private static Pen CreatePen()
    {
        var pen = new Pen(Brushes.Black, 3) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawGeometry(null, Outline, _geometry);
        drawingContext.DrawGeometry(_fill, null, _geometry);
    }
}
