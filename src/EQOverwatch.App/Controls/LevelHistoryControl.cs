using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EQOverwatch.Core.Leveling;

namespace EQOverwatch.App.Controls;

/// <summary>A small chart of a character's level over time: a step line from its first level-up seen to its last, with the dates.</summary>
public sealed class LevelHistoryControl : FrameworkElement
{
    public static readonly DependencyProperty LevelsProperty = DependencyProperty.Register(
        nameof(Levels), typeof(IReadOnlyList<LevelEvent>), typeof(LevelHistoryControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<LevelEvent>? Levels
    {
        get => (IReadOnlyList<LevelEvent>?)GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    private static readonly Brush LineBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6F, 0xDC, 0x8C)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)));
    private static readonly Pen GridPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1));

    private static Brush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Pen p)
    {
        p.Freeze();
        return p;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 420 : availableSize.Width, 90);

    protected override void OnRender(DrawingContext dc)
    {
        IReadOnlyList<LevelEvent>? levels = Levels;
        if (levels is null || levels.Count < 2)
        {
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        const double left = 30;
        const double bottom = 16;
        double width = Math.Max(10, ActualWidth - left - 6);
        double height = Math.Max(10, ActualHeight - bottom - 4);
        long first = levels[0].Time;
        long last = Math.Max(first + 1, levels[^1].Time);
        int lowest = levels.Min(l => l.Level);
        int highest = Math.Max(lowest + 1, levels.Max(l => l.Level));

        double X(long t) => left + (width * (t - first) / (last - first));
        double Y(int level) => 4 + (height * (highest - level) / (double)(highest - lowest));

        dc.DrawLine(GridPen, new Point(left, Y(highest)), new Point(left + width, Y(highest)));
        dc.DrawLine(GridPen, new Point(left, Y(lowest)), new Point(left + width, Y(lowest)));
        void Label(string text, double x, double y)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, TextBrush, dpi);
            dc.DrawText(formatted, new Point(x, y));
        }

        Label(highest.ToString(CultureInfo.CurrentCulture), 2, Y(highest) - 7);
        Label(lowest.ToString(CultureInfo.CurrentCulture), 2, Y(lowest) - 7);
        Label(DateTimeOffset.FromUnixTimeSeconds(first).LocalDateTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture), left, height + 6);
        string end = DateTimeOffset.FromUnixTimeSeconds(last).LocalDateTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        Label(end, Math.Max(left, left + width - (end.Length * 5.2)), height + 6);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(X(levels[0].Time), Y(levels[0].Level)), false, false);
            for (int i = 1; i < levels.Count; i++)
            {
                g.LineTo(new Point(X(levels[i].Time), Y(levels[i - 1].Level)), true, false);
                g.LineTo(new Point(X(levels[i].Time), Y(levels[i].Level)), true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, 2), geometry);
    }
}
