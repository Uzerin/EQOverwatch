using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.Services;

/// <summary>
/// Draws the DPS-over-time graph: the group's line (thick) and one line per top player. Shared by the Encounter DPS tab's Graph
/// tab and the Discord picture, so both look the same.
/// </summary>
internal static class DpsGraph
{
    private const double LeftMargin = 52;
    private const double RightMargin = 12;
    private const double TopMargin = 8;
    private const double AxisHeight = 24;
    private const double LegendLine = 20;
    private static readonly string[] PlayerColors = { "#5CC98A", "#E5845C", "#6AA9F0", "#E7C547", "#C084E6" };
    private const string GroupColor = "#FFFFFF";
    private const string DeathColor = "#E5484D";
    private const string CastColor = "#E7C547";
    private const string IncomingColor = "#E5845C";
    private const string HealingColor = "#5CC98A";
    private const string BossHpColor = "#B8BCC8";
    private const string BurnColor = "#33E5845C";
    private const string ReplayColor = "#9AA0FF";

    /// <summary>The height the incoming-damage graph needs for a plot area <paramref name="plotHeight"/> tall.</summary>
    public static double IncomingHeight(double plotHeight) => TopMargin + plotHeight + AxisHeight + 4;

    /// <summary>The plot area inside <paramref name="area"/>.</summary>
    public static Rect PlotRect(Rect area, double plotHeight) =>
        new(area.X + LeftMargin, area.Y + TopMargin, Math.Max(10, area.Width - LeftMargin - RightMargin), Math.Max(10, plotHeight));

    /// <summary>The boss casts drawn within a few pixels of <paramref name="x"/>, for a tooltip.</summary>
    public static IReadOnlyList<TimelineMark> CastsNear(DpsTimeline timeline, Rect plot, double x)
    {
        double total = Math.Max(1, timeline.Span);
        return (timeline.Casts ?? new ValueList<TimelineMark>(Array.Empty<TimelineMark>()))
            .Where(c => Math.Abs(plot.X + (plot.Width * Math.Clamp(c.Seconds / total, 0, 1)) - x) <= 4).ToList();
    }

    /// <summary>
    /// The damage the raid took per second, under the DPS graph, with the same time axis and the deaths marked.
    /// </summary>
    public static void DrawIncoming(DrawingContext dc, DpsTimeline timeline, Rect area, double plotHeight, Brush muted, Brush grid, double pixelsPerDip)
    {
        Rect plot = PlotRect(area, plotHeight);
        IReadOnlyList<double> values = timeline.Incoming ?? (IReadOnlyList<double>)Array.Empty<double>();
        IReadOnlyList<double> healing = timeline.Healing ?? (IReadOnlyList<double>)Array.Empty<double>();
        double peak = NiceCeiling(Math.Max(values.Count == 0 ? 0 : values.Max(), healing.Count == 0 ? 0 : healing.Max()));
        Axes(dc, plot, peak, timeline.Span, muted, grid, pixelsPerDip);
        DrawLine(dc, healing, plot, peak, SolidOf(HealingColor), 1.8);
        DrawLine(dc, values, plot, peak, SolidOf(IncomingColor), 2.2);
        DrawDeaths(dc, timeline, plot, pixelsPerDip, labels: false);
        if (healing.Count > 0)
        {
            // a small key in the top right: which line is which
            double x = plot.Right - 190;
            dc.DrawRoundedRectangle(SolidOf(IncomingColor), null, new Rect(x, plot.Top + 6, 12, 4), 2, 2);
            Label(dc, "Damage taken", muted, 11, x + 16, plot.Top, TextAlignment.Left, 85, pixelsPerDip);
            dc.DrawRoundedRectangle(SolidOf(HealingColor), null, new Rect(x + 104, plot.Top + 6, 12, 4), 2, 2);
            Label(dc, "Healing", muted, 11, x + 120, plot.Top, TextAlignment.Left, 70, pixelsPerDip);
        }
    }

    private static void Axes(DrawingContext dc, Rect plot, double peak, double totalSeconds, Brush muted, Brush grid, double pixelsPerDip)
    {
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Bottom - (plot.Height * i / 4.0);
            dc.DrawRectangle(grid, null, new Rect(plot.X, y - 0.5, plot.Width, 1));
            Label(dc, FormatRate(peak * i / 4.0), muted, 11, plot.X - LeftMargin + 2, y - 8, TextAlignment.Right, LeftMargin - 8, pixelsPerDip);
        }

        // Time: a small tick at the top and bottom edge every 30 seconds (every minute past 10 minutes), and the end time at the right.
        int step = totalSeconds < 600 ? 30 : 60;
        Pen tick = new(muted, 1);
        tick.Freeze();
        void Tick(double x)
        {
            dc.DrawLine(tick, new Point(x, plot.Top), new Point(x, plot.Top + 5));
            dc.DrawLine(tick, new Point(x, plot.Bottom - 5), new Point(x, plot.Bottom));
        }

        const double boxWidth = 60;
        Label(dc, FormatTime(0), muted, 11, plot.X, plot.Bottom + 4, TextAlignment.Left, boxWidth, pixelsPerDip);
        double endLabelLeft = plot.Right - Measure(FormatTime(totalSeconds), 11, pixelsPerDip) - 8;
        double lastRight = plot.X + Measure(FormatTime(0), 11, pixelsPerDip);
        for (int t = step; t < totalSeconds; t += step)
        {
            double x = plot.X + (plot.Width * t / totalSeconds);
            Tick(x);
            string time = FormatTime(t);
            double half = Measure(time, 11, pixelsPerDip) / 2;
            if (x - half > lastRight + 6 && x + half < endLabelLeft)
            {
                Label(dc, time, muted, 11, x - (boxWidth / 2), plot.Bottom + 4, TextAlignment.Center, boxWidth, pixelsPerDip);
                lastRight = x + half;
            }
        }

        Tick(plot.Right);
        Label(dc, FormatTime(totalSeconds), muted, 11, plot.Right - boxWidth, plot.Bottom + 4, TextAlignment.Right, boxWidth, pixelsPerDip);
    }

    private static double MarkX(DpsTimeline timeline, Rect plot, double seconds) =>
        plot.X + (plot.Width * Math.Clamp(seconds / Math.Max(1, timeline.Span), 0, 1));

    // A red dashed line where someone died, with the name at the top (staggered so close deaths stay readable).
    private static void DrawDeaths(DrawingContext dc, DpsTimeline timeline, Rect plot, double pixelsPerDip, bool labels)
    {
        if (timeline.Deaths is not { Count: > 0 } deaths)
        {
            return;
        }

        Brush red = SolidOf(DeathColor);
        var pen = new Pen(red, 1.2) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        pen.Freeze();
        for (int i = 0; i < deaths.Count; i++)
        {
            double x = MarkX(timeline, plot, deaths[i].Seconds);
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            if (labels)
            {
                double y = plot.Top + 2 + ((i % 4) * 13);
                bool left = x > plot.Right - 90;
                Label(dc, deaths[i].Label, red, 10, left ? x - 92 : x + 3, y, left ? TextAlignment.Right : TextAlignment.Left, 89, pixelsPerDip);
            }
        }
    }

    // A small tick at the foot of the plot each time the boss began a cast.
    private static void DrawCasts(DrawingContext dc, DpsTimeline timeline, Rect plot)
    {
        if (timeline.Casts is not { Count: > 0 } casts)
        {
            return;
        }

        Brush amber = SolidOf(CastColor);
        foreach (TimelineMark cast in casts)
        {
            double x = MarkX(timeline, plot, cast.Seconds);
            dc.DrawRectangle(amber, null, new Rect(x - 1, plot.Bottom - 9, 2, 9));
        }
    }

    /// <summary>The colors the lines use: the group first, then the players in order.</summary>
    public static string ColorOf(int index) => index < 0 ? GroupColor : PlayerColors[index % PlayerColors.Length];

    /// <summary>The height the graph needs for a plot area <paramref name="plotHeight"/> tall, legend included.</summary>
    public static double TotalHeight(DpsTimeline timeline, double width, double plotHeight, double pixelsPerDip) =>
        TopMargin + plotHeight + AxisHeight + (LegendLines(timeline, width, pixelsPerDip) * LegendLine) + 4;

    public static void Draw(
        DrawingContext dc, DpsTimeline timeline, Rect area, double plotHeight, Brush text, Brush muted, Brush grid, Brush groupBrush,
        double pixelsPerDip, double? cursorSeconds = null)
    {
        Rect plot = PlotRect(area, plotHeight);
        double peak = NiceCeiling(timeline.Peak);
        DrawBurns(dc, timeline, plot);
        Axes(dc, plot, peak, timeline.Span, muted, grid, pixelsPerDip);
        DrawCasts(dc, timeline, plot);
        DrawBossHp(dc, timeline, plot);

        for (int p = 0; p < timeline.Players.Count; p++)
        {
            DrawLine(dc, timeline.Players[p].Dps, plot, peak, SolidOf(ColorOf(p)), 1.6);
        }

        DrawLine(dc, timeline.Group, plot, peak, groupBrush, 2.6);
        DrawDeaths(dc, timeline, plot, pixelsPerDip, labels: true);
        if (cursorSeconds is { } cursor && cursor < timeline.Span)
        {
            var pen = new Pen(SolidOf(ReplayColor), 1.5);
            pen.Freeze();
            double x = MarkX(timeline, plot, cursor);
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        // legend
        double lx = area.X + LeftMargin;
        double ly = plot.Bottom + AxisHeight;
        double maxX = area.Right - RightMargin;
        var entries = Legend(timeline);
        for (int i = 0; i < entries.Count; i++)
        {
            double w = EntryWidth(entries[i].Name, pixelsPerDip);
            if (lx + w > maxX && lx > area.X + LeftMargin)
            {
                lx = area.X + LeftMargin;
                ly += LegendLine;
            }

            Brush key = entries[i].Index == BossHpIndex ? SolidOf(BossHpColor) : i == 0 ? groupBrush : SolidOf(ColorOf(i - 1));
            dc.DrawRoundedRectangle(key, null, new Rect(lx, ly + 5, 12, entries[i].Index == BossHpIndex ? 3 : 6), 2, 2);
            Label(dc, entries[i].Name, text, 12, lx + 17, ly + 1, TextAlignment.Left, w, pixelsPerDip);
            lx += w + 14;
        }
    }

    private static void DrawLine(DrawingContext dc, IReadOnlyList<double> values, Rect plot, double peak, Brush brush, double thickness)
    {
        if (values.Count == 0 || peak <= 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            for (int i = 0; i < values.Count; i++)
            {
                double x = plot.X + (values.Count == 1 ? 0 : plot.Width * i / (values.Count - 1));
                double y = plot.Bottom - (plot.Height * Math.Clamp(values[i] / peak, 0, 1));
                if (i == 0)
                {
                    ctx.BeginFigure(new Point(x, y), false, false);
                }
                else
                {
                    ctx.LineTo(new Point(x, y), true, true);
                }
            }
        }

        geometry.Freeze();
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private const int BossHpIndex = -2;

    private static List<(string Name, int Index)> Legend(DpsTimeline timeline)
    {
        var list = new List<(string, int)> { ("Group", -1) };
        for (int i = 0; i < timeline.Players.Count; i++)
        {
            list.Add((timeline.Players[i].Name, i));
        }

        if (timeline.BossHp is { Count: > 0 })
        {
            list.Add(("Boss HP (est.)", BossHpIndex));
        }

        return list;
    }

    // Light shading behind the stretches where the group's DPS ran 50% or more above the fight's average.
    private static void DrawBurns(DrawingContext dc, DpsTimeline timeline, Rect plot)
    {
        Brush shade = SolidOf(BurnColor);
        double total = Math.Max(1, timeline.Span);
        foreach ((int from, int to) in timeline.Burns())
        {
            double x1 = plot.X + (plot.Width * Math.Clamp(from * timeline.BucketSeconds / total, 0, 1));
            double x2 = plot.X + (plot.Width * Math.Clamp((to + 1) * timeline.BucketSeconds / total, 0, 1));
            dc.DrawRectangle(shade, null, new Rect(x1, plot.Top, Math.Max(1, x2 - x1), plot.Height));
        }
    }

    // The boss's estimated health, dashed, from full at the top of the plot to empty at the bottom.
    private static void DrawBossHp(DrawingContext dc, DpsTimeline timeline, Rect plot)
    {
        if (timeline.BossHp is not { Count: > 0 } hp)
        {
            return;
        }

        var geometry = new StreamGeometry();
        double total = Math.Max(1, timeline.Span);
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(plot.X, plot.Top), false, false);
            for (int i = 0; i < hp.Count; i++)
            {
                double x = plot.X + (plot.Width * Math.Clamp(Math.Min(total, (i + 1) * timeline.BucketSeconds) / total, 0, 1));
                ctx.LineTo(new Point(x, plot.Bottom - (plot.Height * hp[i])), true, true);
            }
        }

        geometry.Freeze();
        var pen = new Pen(SolidOf(BossHpColor), 1.6) { DashStyle = new DashStyle(new double[] { 5, 3 }, 0), LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static double EntryWidth(string name, double ppd) => Math.Min(190, Measure(name, 12, ppd) + 4);

    private static int LegendLines(DpsTimeline timeline, double width, double ppd)
    {
        double max = width - LeftMargin - RightMargin;
        double x = 0;
        int lines = 1;
        foreach ((string name, _) in Legend(timeline))
        {
            double w = 17 + EntryWidth(name, ppd);
            if (x + w > max && x > 0)
            {
                lines++;
                x = 0;
            }

            x += w + 14;
        }

        return lines;
    }

    /// <summary>The next "round" number at or above <paramref name="value"/> (1, 2, 2.5, 5 times a power of ten).</summary>
    internal static double NiceCeiling(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double step in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            if (value <= step * magnitude)
            {
                return step * magnitude;
            }
        }

        return 10 * magnitude;
    }

    internal static string FormatRate(double value) =>
        value >= 1000 ? (value / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + "k" : value.ToString("0", CultureInfo.InvariantCulture);

    internal static string FormatTime(double seconds)
    {
        int s = (int)Math.Round(seconds);
        return $"{s / 60}:{s % 60:00}";
    }

    private static Brush SolidOf(string hex)
    {
        Color color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static double Measure(string text, double size, double ppd) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Black, ppd)
            .WidthIncludingTrailingWhitespace;

    private static void Label(DrawingContext dc, string text, Brush brush, double size, double x, double y, TextAlignment alignment, double width, double ppd)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, ppd)
        {
            MaxTextWidth = Math.Max(1, width),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };

        // x is the left edge of the box the text is aligned within
        dc.DrawText(formatted, new Point(x, y));
    }
}
