using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EQOverwatch.Core.History;

namespace EQOverwatch.App.Controls;

/// <summary>A character's raid boss DPS by week: a line through each week with fights, with the weeks along the bottom. Hover a point for its week.</summary>
public sealed class DpsTrendControl : FrameworkElement
{
    public static readonly DependencyProperty TrendProperty = DependencyProperty.Register(
        nameof(Trend), typeof(RaidDpsTrend), typeof(DpsTrendControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public RaidDpsTrend? Trend
    {
        get => (RaidDpsTrend?)GetValue(TrendProperty);
        set => SetValue(TrendProperty, value);
    }

    private static readonly Brush LineBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6F, 0xDC, 0x8C)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)));
    private static readonly Pen GridPen = FrozenPen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Pen LinePen = FrozenPen(new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round });

    private const double Left = 44;
    private const double Bottom = 18;
    private const double Top = 8;

    private readonly List<(Point At, RaidDpsWeek Week)> _points = new();
    private readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };

    public DpsTrendControl()
    {
        _tip.PlacementTarget = this;
    }

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
        new(double.IsInfinity(availableSize.Width) ? 560 : availableSize.Width, 150);

    protected override void OnRender(DrawingContext dc)
    {
        _points.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // so the whole area takes the mouse
        if (Trend is not { Weeks.Count: >= 2 } trend)
        {
            return;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        double width = Math.Max(10, ActualWidth - Left - 10);
        double height = Math.Max(10, ActualHeight - Bottom - Top);
        DateTime first = trend.Weeks[^1].WeekStart.AddDays(-7 * (RaidDpsTrend.WeeksShown - 1));
        double low = trend.Weeks.Min(w => w.Dps);
        double high = trend.Weeks.Max(w => w.Dps);
        double step = NiceStep((high - low) / 3);
        double bottomValue = Math.Max(0, Math.Floor(low / step) * step - step / 2);
        double topValue = Math.Ceiling(high / step) * step + step / 2;

        double X(DateTime week) => Left + width * (week - first).TotalDays / (7.0 * (RaidDpsTrend.WeeksShown - 1));
        double Y(double dps) => Top + height * (topValue - dps) / (topValue - bottomValue);

        for (double v = Math.Ceiling(bottomValue / step) * step; v <= topValue; v += step)
        {
            double y = Y(v);
            dc.DrawLine(GridPen, new Point(Left, y), new Point(Left + width, y));
            var label = new FormattedText(v.ToString("N0", culture), culture, FlowDirection.LeftToRight, typeface, 10, TextBrush, dpi);
            dc.DrawText(label, new Point(Left - 6 - label.Width, y - label.Height / 2));
        }

        for (int i = 0; i < RaidDpsTrend.WeeksShown; i += 2)
        {
            DateTime week = first.AddDays(7 * i);
            var label = new FormattedText(week.ToString("MMM d", culture), culture, FlowDirection.LeftToRight, typeface, 10, TextBrush, dpi);
            dc.DrawText(label, new Point(Math.Clamp(X(week) - label.Width / 2, 0, Math.Max(0, ActualWidth - label.Width)), Top + height + 3));
        }

        var line = new StreamGeometry();
        using (StreamGeometryContext ctx = line.Open())
        {
            for (int i = 0; i < trend.Weeks.Count; i++)
            {
                var at = new Point(X(trend.Weeks[i].WeekStart), Y(trend.Weeks[i].Dps));
                _points.Add((at, trend.Weeks[i]));
                if (i == 0)
                {
                    ctx.BeginFigure(at, false, false);
                }
                else
                {
                    ctx.LineTo(at, true, true);
                }
            }
        }

        line.Freeze();
        dc.DrawGeometry(null, LinePen, line);
        foreach ((Point at, _) in _points)
        {
            dc.DrawEllipse(LineBrush, null, at, 3.5, 3.5);
        }
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0)
        {
            return 100;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double fraction = raw / magnitude;
        return (fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10) * magnitude;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point mouse = e.GetPosition(this);
        (Point At, RaidDpsWeek Week)? near = _points.Count == 0 ? null : _points.MinBy(p => Math.Abs(p.At.X - mouse.X));
        if (near is { } hit && Math.Abs(hit.At.X - mouse.X) <= 20)
        {
            CultureInfo culture = CultureInfo.CurrentCulture;
            _tip.IsOpen = false; // reopening moves it to the mouse
            _tip.Content = $"Week of {hit.Week.WeekStart.ToString("MMM d", culture)}\n{hit.Week.Dps.ToString("N0", culture)} DPS · {hit.Week.Fights.ToString(culture)} fight{(hit.Week.Fights == 1 ? string.Empty : "s")}";
            _tip.IsOpen = true;
        }
        else
        {
            _tip.Content = null;
            _tip.IsOpen = false;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.IsOpen = false;
    }
}
