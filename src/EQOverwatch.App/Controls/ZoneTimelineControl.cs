using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EQOverwatch.Core.Session;

namespace EQOverwatch.App.Controls;

/// <summary>
/// The Session tab's zone timeline: one bar across the width, a colored block per zone stretch (time away shows as a gap), the zone's name
/// on the block when it fits and the hours along the bottom. Hover a block for the zone and its times.
/// </summary>
public sealed class ZoneTimelineControl : FrameworkElement
{
    private const double BarHeight = 30;
    private const double AxisHeight = 18;

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x1D, 0x9E, 0x75), Color.FromRgb(0x7F, 0x77, 0xDD), Color.FromRgb(0xD8, 0x5A, 0x30), Color.FromRgb(0x37, 0x8A, 0xDD),
        Color.FromRgb(0xD4, 0x53, 0x7E), Color.FromRgb(0xBA, 0x75, 0x17), Color.FromRgb(0x63, 0x99, 0x22), Color.FromRgb(0x88, 0x87, 0x80),
    };

    public static readonly DependencyProperty StretchesProperty = DependencyProperty.Register(
        nameof(Stretches), typeof(IReadOnlyList<ZoneStretch>), typeof(ZoneTimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ZoneStretch>? Stretches
    {
        get => (IReadOnlyList<ZoneStretch>?)GetValue(StretchesProperty);
        set => SetValue(StretchesProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, BarHeight + AxisHeight + 2);

    private (DateTime Start, DateTime End) Range(IReadOnlyList<ZoneStretch> stretches) =>
        (stretches.Min(s => s.Start), stretches.Max(s => s.End));

    protected override void OnRender(DrawingContext dc)
    {
        IReadOnlyList<ZoneStretch> stretches = Stretches ?? Array.Empty<ZoneStretch>();
        Brush muted = Find("TextMutedBrush", Brushes.Gray);
        Brush grid = Find("BorderSoftBrush", Brushes.DimGray);
        double width = ActualWidth;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, BarHeight + AxisHeight));
        dc.DrawRectangle(null, new Pen(grid, 1), new Rect(0.5, 0.5, Math.Max(0, width - 1), BarHeight));
        if (stretches.Count == 0 || width < 20)
        {
            return;
        }

        (DateTime start, DateTime end) = Range(stretches);
        double total = Math.Max(60, (end - start).TotalSeconds);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var zones = stretches.Select(s => s.Zone).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (ZoneStretch s in stretches)
        {
            double x = (s.Start - start).TotalSeconds / total * width;
            double w = Math.Max(1, s.Seconds / total * width);
            Color color = s.Zone.Length == 0 ? Color.FromRgb(0x5F, 0x5E, 0x5A) : Palette[zones.IndexOf(s.Zone) % Palette.Length];
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, 1, w, BarHeight - 1));
            var label = new FormattedText(s.Zone.Length > 0 ? s.Zone : "?", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, Brushes.White, dpi);
            if (label.Width + 8 <= w)
            {
                dc.DrawText(label, new Point(x + 4, (BarHeight - label.Height) / 2));
            }
        }

        // Hour marks along the bottom.
        DateTime hour = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0).AddHours(1);
        double hours = total / 3600;
        int step = hours > 12 ? 3 : hours > 6 ? 2 : 1;
        for (; hour < end; hour = hour.AddHours(step))
        {
            double x = (hour - start).TotalSeconds / total * width;
            dc.DrawLine(new Pen(grid, 1), new Point(x, BarHeight), new Point(x, BarHeight + 4));
            var text = new FormattedText(hour.ToString("HH:mm", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, muted, dpi);
            dc.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, width - text.Width)), BarHeight + 3));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        IReadOnlyList<ZoneStretch> stretches = Stretches ?? Array.Empty<ZoneStretch>();
        if (stretches.Count == 0)
        {
            ToolTip = null;
            return;
        }

        (DateTime start, DateTime end) = Range(stretches);
        double total = Math.Max(60, (end - start).TotalSeconds);
        DateTime at = start.AddSeconds(e.GetPosition(this).X / Math.Max(1, ActualWidth) * total);
        ZoneStretch? hit = stretches.FirstOrDefault(s => s.Start <= at && s.End >= at);
        ToolTip = hit is null
            ? "Away"
            : $"{(hit.Zone.Length > 0 ? hit.Zone : "Before the first zone change")}: {hit.Start:HH:mm}–{hit.End:HH:mm}";
    }

    private Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
