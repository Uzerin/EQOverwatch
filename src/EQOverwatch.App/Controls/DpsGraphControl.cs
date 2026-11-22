using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.Controls;

/// <summary>Draws a fight's DPS-over-time graph (the group plus the top players) in the app's current theme.</summary>
public sealed class DpsGraphControl : FrameworkElement
{
    public static readonly DependencyProperty TimelineProperty = DependencyProperty.Register(
        nameof(Timeline), typeof(DpsTimeline), typeof(DpsGraphControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public DpsTimeline? Timeline
    {
        get => (DpsTimeline?)GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    public static readonly DependencyProperty IncomingProperty = DependencyProperty.Register(
        nameof(Incoming), typeof(bool), typeof(DpsGraphControl), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>True to draw the damage the raid took per second instead of the DPS lines.</summary>
    public bool Incoming
    {
        get => (bool)GetValue(IncomingProperty);
        set => SetValue(IncomingProperty, value);
    }

    private Rect _plot;

    public DpsGraphControl()
    {
        ToolTipService.SetInitialShowDelay(this, 0);
    }

    // The boss's casts under the mouse show as a tooltip.
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Incoming || Timeline is not { } timeline)
        {
            return;
        }

        Point p = e.GetPosition(this);
        IReadOnlyList<TimelineMark> near = p.Y >= _plot.Top && p.Y <= _plot.Bottom + 4 ? DpsGraph.CastsNear(timeline, _plot, p.X) : Array.Empty<TimelineMark>();
        string? tip = near.Count == 0 ? null
            : string.Join("\n", near.Select(c => $"{DpsGraph.FormatTime(c.Seconds)}  {c.Label}"));
        if (!Equals(ToolTip as string, tip))
        {
            ToolTip = tip;
        }
    }

    private Brush Themed(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Timeline is not { } timeline || ActualWidth < 120 || ActualHeight < 100)
        {
            return;
        }

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Brush text = Themed("TextPrimaryBrush", Brushes.White);
        Brush muted = Themed("TextMutedBrush", Brushes.Gray);
        Brush grid = Themed("BorderSoftBrush", Brushes.DimGray);

        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        if (Incoming)
        {
            double incomingPlot = Math.Max(40, ActualHeight - DpsGraph.IncomingHeight(0));
            DpsGraph.DrawIncoming(drawingContext, timeline, area, incomingPlot, muted, grid, ppd);
            return;
        }

        // the plot takes whatever the legend leaves
        double legendAndAxis = DpsGraph.TotalHeight(timeline, ActualWidth, 0, ppd);
        double plot = Math.Max(60, ActualHeight - legendAndAxis - 4);
        _plot = DpsGraph.PlotRect(area, plot);
        DpsGraph.Draw(drawingContext, timeline, area, plot, text, muted, grid, text, ppd);
    }
}
