using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.Controls;

/// <summary>
/// Draws the Spell Casts timeline: one horizontal lane per spell, a tick for every cast, with the fight's time along the top.
/// Hover a tick to see who cast it and when. Sized to its content so a ScrollViewer can scroll it.
/// </summary>
public sealed class CastTimelineControl : FrameworkElement
{
    private const double LabelWidth = 190;
    private const double LaneHeight = 26;
    private const double AxisHeight = 26;
    private const double RightPad = 24;

    public static readonly DependencyProperty LanesProperty = DependencyProperty.Register(
        nameof(Lanes), typeof(IReadOnlyList<CastLane>), typeof(CastTimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DurationSecondsProperty = DependencyProperty.Register(
        nameof(DurationSeconds), typeof(double), typeof(CastTimelineControl),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<CastLane>? Lanes
    {
        get => (IReadOnlyList<CastLane>?)GetValue(LanesProperty);
        set => SetValue(LanesProperty, value);
    }

    public double DurationSeconds
    {
        get => (double)GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }

    private double Duration => Math.Max(1.0, DurationSeconds);

    /// <summary>Pixels per second: the whole fight is at least ~900 px wide and never absurdly stretched.</summary>
    private double PixelsPerSecond => Math.Clamp(900.0 / Duration, 1.5, 12.0);

    protected override Size MeasureOverride(Size availableSize)
    {
        int lanes = Lanes?.Count ?? 0;
        return new Size(LabelWidth + (Duration * PixelsPerSecond) + RightPad, AxisHeight + (Math.Max(1, lanes) * LaneHeight) + 4);
    }

    protected override void OnRender(DrawingContext dc)
    {
        Brush text = Find("TextPrimaryBrush", Brushes.White);
        Brush muted = Find("TextMutedBrush", Brushes.Gray);
        Brush grid = Find("BorderSoftBrush", Brushes.DimGray);
        Brush accent = Find("AccentBrush", Brushes.DeepSkyBlue);
        Brush stripe = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        var failed = new SolidColorBrush(Color.FromRgb(0xE0, 0x48, 0x3E));

        IReadOnlyList<CastLane> lanes = Lanes ?? Array.Empty<CastLane>();
        double pps = PixelsPerSecond;
        double width = LabelWidth + (Duration * pps);
        var gridPen = new Pen(grid, 1);

        // time axis: a tick every 15, 30 or 60 s (or 5 min on very long fights), labelled m:ss
        double step = Duration <= 120 ? 15 : Duration <= 480 ? 30 : Duration <= 1800 ? 60 : 300;
        for (double t = 0; t <= Duration + 0.001; t += step)
        {
            double x = LabelWidth + (t * pps);
            dc.DrawLine(gridPen, new Point(x, AxisHeight - 4), new Point(x, AxisHeight + (Math.Max(1, lanes.Count) * LaneHeight)));
            Draw(dc, SpellCastReport.FormatOffset(t), muted, 11, x + 3, 4, TextAlignment.Left, 60);
        }

        if (lanes.Count == 0)
        {
            Draw(dc, "No player spell casts were seen in this fight.", muted, 13, 8, AxisHeight + 4, TextAlignment.Left, 500);
            return;
        }

        for (int i = 0; i < lanes.Count; i++)
        {
            CastLane lane = lanes[i];
            double top = AxisHeight + (i * LaneHeight);
            if (i % 2 == 1)
            {
                dc.DrawRectangle(stripe, null, new Rect(0, top, width, LaneHeight));
            }

            Draw(dc, $"{lane.Spell} ({lane.Marks.Count})", text, 13, 6, top + 4, TextAlignment.Left, LabelWidth - 12);
            foreach (CastMark mark in lane.Marks)
            {
                double x = LabelWidth + (mark.Offset * pps);
                dc.DrawRoundedRectangle(mark.Failed ? failed : accent, null, new Rect(x - 1.5, top + 5, 3, LaneHeight - 10), 1, 1);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        ToolTip = Describe(e.GetPosition(this));
    }

    private string? Describe(Point point)
    {
        IReadOnlyList<CastLane>? lanes = Lanes;
        int index = (int)Math.Floor((point.Y - AxisHeight) / LaneHeight);
        if (lanes is null || index < 0 || index >= lanes.Count || point.X < LabelWidth)
        {
            return null;
        }

        double pps = PixelsPerSecond;
        CastMark? nearest = null;
        double best = 5.0 / pps; // within five pixels
        foreach (CastMark mark in lanes[index].Marks)
        {
            double distance = Math.Abs(mark.Offset - ((point.X - LabelWidth) / pps));
            if (distance <= best)
            {
                best = distance;
                nearest = mark;
            }
        }

        return nearest is null
            ? null
            : $"{lanes[index].Spell}: {nearest.Caster} at {SpellCastReport.FormatOffset(nearest.Offset)}{(nearest.Failed ? " (fizzled or interrupted)" : string.Empty)}";
    }

    private Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void Draw(DrawingContext dc, string text, Brush brush, double size, double x, double y, TextAlignment alignment, double maxWidth)
    {
        var formatted = new FormattedText(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };
        dc.DrawText(formatted, new Point(x, y));
    }
}
