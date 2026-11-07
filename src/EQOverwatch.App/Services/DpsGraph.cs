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

    /// <summary>The colors the lines use: the group first, then the players in order.</summary>
    public static string ColorOf(int index) => index < 0 ? GroupColor : PlayerColors[index % PlayerColors.Length];

    /// <summary>The height the graph needs for a plot area <paramref name="plotHeight"/> tall, legend included.</summary>
    public static double TotalHeight(DpsTimeline timeline, double width, double plotHeight, double pixelsPerDip) =>
        TopMargin + plotHeight + AxisHeight + (LegendLines(timeline, width, pixelsPerDip) * LegendLine) + 4;

    public static void Draw(
        DrawingContext dc, DpsTimeline timeline, Rect area, double plotHeight, Brush text, Brush muted, Brush grid, Brush groupBrush,
        double pixelsPerDip)
    {
        var plot = new Rect(area.X + LeftMargin, area.Y + TopMargin, Math.Max(10, area.Width - LeftMargin - RightMargin), Math.Max(10, plotHeight));
        double peak = NiceCeiling(timeline.Peak);
        int count = timeline.Count;
        double totalSeconds = count * timeline.BucketSeconds;

        // horizontal grid + y labels
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Bottom - (plot.Height * i / 4.0);
            dc.DrawRectangle(grid, null, new Rect(plot.X, y - 0.5, plot.Width, 1));
            Label(dc, FormatRate(peak * i / 4.0), muted, 11, plot.X - LeftMargin + 2, y - 8, TextAlignment.Right, LeftMargin - 8, pixelsPerDip);
        }

        // x labels
        for (int i = 0; i <= 4; i++)
        {
            double x = plot.X + (plot.Width * i / 4.0);
            string time = FormatTime(totalSeconds * i / 4.0);
            double boxWidth = 60;
            double left = i == 0 ? x : i == 4 ? x - boxWidth : x - (boxWidth / 2);
            Label(dc, time, muted, 11, left, plot.Bottom + 4, i == 0 ? TextAlignment.Left : i == 4 ? TextAlignment.Right : TextAlignment.Center, boxWidth, pixelsPerDip);
        }

        for (int p = 0; p < timeline.Players.Count; p++)
        {
            DrawLine(dc, timeline.Players[p].Dps, plot, peak, SolidOf(ColorOf(p)), 1.6);
        }

        DrawLine(dc, timeline.Group, plot, peak, groupBrush, 2.6);

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

            dc.DrawRoundedRectangle(i == 0 ? groupBrush : SolidOf(ColorOf(i - 1)), null, new Rect(lx, ly + 5, 12, 6), 2, 2);
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

    private static List<(string Name, int Index)> Legend(DpsTimeline timeline)
    {
        var list = new List<(string, int)> { ("Group", -1) };
        for (int i = 0; i < timeline.Players.Count; i++)
        {
            list.Add((timeline.Players[i].Name, i));
        }

        return list;
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
