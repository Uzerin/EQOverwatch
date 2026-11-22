using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.Controls;

/// <summary>The Tank Timeline: one lane per player the boss meleed, with a bar for each turn it spent on them.</summary>
public sealed class TankTimelineControl : FrameworkElement
{
    private const double LabelWidth = 120;
    private const double LaneHeight = 22;
    private const double AxisHeight = 20;
    private const int MaxLanes = 8;
    private static readonly string[] Colors = { "#E5845C", "#6AA9F0", "#5CC98A", "#E7C547", "#C084E6", "#4FC6C6", "#E06C9F", "#A0A0A0" };

    public static readonly DependencyProperty SpansProperty = DependencyProperty.Register(
        nameof(Spans), typeof(IReadOnlyList<TankSpanSnapshot>), typeof(TankTimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(TankTimelineControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TankSpanSnapshot>? Spans
    {
        get => (IReadOnlyList<TankSpanSnapshot>?)GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>The players given a lane: the most time tanked first, at most <see cref="MaxLanes"/>.</summary>
    public static IReadOnlyList<string> Lanes(IReadOnlyList<TankSpanSnapshot> spans) =>
        spans.GroupBy(s => s.Name, StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(s => s.End - s.Start + 1))
            .Take(MaxLanes).Select(g => g.Key).ToList();

    /// <summary>The height the control needs for these turns.</summary>
    public static double HeightFor(IReadOnlyList<TankSpanSnapshot> spans) => (Math.Max(1, Lanes(spans).Count) * LaneHeight) + AxisHeight + 6;

    protected override void OnRender(DrawingContext dc)
    {
        if (Spans is not { Count: > 0 } spans || ActualWidth < LabelWidth + 60)
        {
            return;
        }

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Brush text = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
        Brush muted = TryFindResource("TextMutedBrush") as Brush ?? Brushes.Gray;
        Brush grid = TryFindResource("BorderSoftBrush") as Brush ?? Brushes.DimGray;
        IReadOnlyList<string> lanes = Lanes(spans);
        double total = Math.Max(1, Math.Max(Duration, spans.Max(s => s.End)));
        double left = LabelWidth;
        double width = ActualWidth - LabelWidth - 12;
        double bottom = lanes.Count * LaneHeight;

        for (int i = 0; i <= 4; i++)
        {
            double x = left + (width * i / 4.0);
            dc.DrawRectangle(grid, null, new Rect(x - 0.5, 0, 1, bottom));
            var label = new FormattedText(DpsGraph.FormatTime(total * i / 4.0), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, muted, ppd);
            dc.DrawText(label, new Point(Math.Clamp(x - (label.Width / 2), left, left + width - label.Width), bottom + 3));
        }

        for (int lane = 0; lane < lanes.Count; lane++)
        {
            double y = lane * LaneHeight;
            var name = new FormattedText(lanes[lane], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, text, ppd)
            {
                MaxTextWidth = LabelWidth - 8,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(name, new Point(0, y + 3));
            var fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Colors[lane % Colors.Length]));
            fill.Freeze();
            foreach (TankSpanSnapshot span in spans.Where(s => s.Name == lanes[lane]))
            {
                double x0 = left + (width * span.Start / total);
                double x1 = left + (width * Math.Max(span.End, span.Start + 1) / total);
                dc.DrawRoundedRectangle(fill, null, new Rect(x0, y + 4, Math.Max(2, x1 - x0), LaneHeight - 8), 3, 3);
            }
        }
    }
}
