using System.Windows;
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

        // the plot takes whatever the legend leaves
        double legendAndAxis = DpsGraph.TotalHeight(timeline, ActualWidth, 0, ppd);
        double plot = Math.Max(60, ActualHeight - legendAndAxis - 4);
        DpsGraph.Draw(drawingContext, timeline, new Rect(0, 0, ActualWidth, ActualHeight), plot, text, muted, grid, text, ppd);
    }
}
