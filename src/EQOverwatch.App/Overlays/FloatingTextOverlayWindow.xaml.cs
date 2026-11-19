using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EQOverwatch.App.Controls;
using EQOverwatch.App.Services;

namespace EQOverwatch.App.Overlays;

/// <summary>
/// Floating combat text: each new number appears at the top with its character's name under it, slides down and fades out over
/// <see cref="Seconds"/> seconds. Crits are bold and larger. At most <see cref="MaxShown"/> numbers are up at once.
/// </summary>
public partial class FloatingTextOverlayWindow : OverlayWindowBase
{
    public const double Seconds = 3;
    public const int MaxShown = 12;

    public FloatingTextOverlayWindow(OverlayKind kind)
    {
        InitializeComponent();
        OverlayName = kind.Title();
        Title = "EQ Overwatch " + kind.Title();
    }

    /// <summary>Shows one number (UI thread).</summary>
    public void Add(string number, string character, Color color, bool critical, double size)
    {
        if (Lane.ActualHeight < 10 || Lane.ActualWidth < 10)
        {
            return;
        }

        while (Lane.Children.Count >= MaxShown)
        {
            Lane.Children.RemoveAt(0);
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var stack = new StackPanel { IsHitTestVisible = false };
        var num = new OutlinedText(number, critical ? size * 1.3 : size, critical, brush) { HorizontalAlignment = HorizontalAlignment.Center };
        var name = new OutlinedText(character, Math.Max(9, size * 0.55), false, Brushes.Gainsboro) { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(num);
        stack.Children.Add(name);
        stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = stack.DesiredSize.Width;
        double height = stack.DesiredSize.Height;
        Canvas.SetLeft(stack, Math.Max(0, (Lane.ActualWidth - width) / 2));
        Canvas.SetTop(stack, 0);
        Lane.Children.Add(stack);

        var duration = new Duration(TimeSpan.FromSeconds(Seconds));
        var fall = new DoubleAnimation(0, Math.Max(0, Lane.ActualHeight - height), duration);
        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2 } };
        fade.Completed += (_, _) => Lane.Children.Remove(stack);
        stack.BeginAnimation(Canvas.TopProperty, fall);
        stack.BeginAnimation(OpacityProperty, fade);
    }
}
