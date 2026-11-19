using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EQOverwatch.App.Controls;
using EQOverwatch.App.Services;

namespace EQOverwatch.App.Overlays;

/// <summary>How one floating number is drawn: its color, size, font, lane, and whether it may merge with the one before it.</summary>
public readonly record struct FloatingLook(Color Color, double Size, FontFamily Font, FontWeight Weight, bool Right, bool Merge);

/// <summary>
/// Floating combat text in two lanes: what your character deals or casts on the left, what it takes or receives on the right. Each new
/// number appears at the top of its lane with its character's name under it, slides down and fades out over <see cref="Seconds"/>
/// seconds. Crits are bold and larger; misses and resists are small gray words. At most <see cref="MaxShown"/> numbers are up at once.
/// </summary>
public partial class FloatingTextOverlayWindow : OverlayWindowBase
{
    public const double Seconds = 3;
    public const int MaxShown = 16;

    /// <summary>Hits this close together, from one character on one target, merge into one number when merging is on.</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(300);

    private static readonly Brush WordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)));

    private readonly Dictionary<string, Merged> _merged = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Merged
    {
        public required StackPanel Stack;
        public required string Prefix;
        public int Total;
        public int Count;
        public bool Critical;
        public DateTime Last;
    }

    public FloatingTextOverlayWindow(OverlayKind kind)
    {
        InitializeComponent();
        OverlayName = kind.Title();
        Title = "EQ Overwatch " + kind.Title();
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>Shows one number (UI thread). <paramref name="mergeKey"/> names who hit whom; <paramref name="amount"/> is 0 for a word.</summary>
    public void Add(string text, string character, bool critical, FloatingLook look, string mergeKey = "", int amount = 0, string prefix = "")
    {
        if (Lane.ActualHeight < 10 || Lane.ActualWidth < 10)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        string key = (look.Right ? "R|" : "L|") + mergeKey;
        if (look.Merge && amount > 0 && mergeKey.Length > 0 && _merged.TryGetValue(key, out Merged? merged)
            && now - merged.Last <= MergeWindow && Lane.Children.Contains(merged.Stack))
        {
            merged.Total += amount;
            merged.Count++;
            merged.Critical |= critical;
            merged.Last = now;
            string mergedText = $"{merged.Prefix}{merged.Total.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} x{merged.Count}";
            merged.Stack.Children[0] = Number(mergedText, merged.Critical, look);
            merged.Stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(merged.Stack, LeftFor(merged.Stack.DesiredSize.Width, look.Right));
            return;
        }

        while (Lane.Children.Count >= MaxShown)
        {
            Lane.Children.RemoveAt(0);
        }

        bool word = amount == 0;
        var stack = new StackPanel { IsHitTestVisible = false };
        if (word)
        {
            stack.Children.Add(new OutlinedText(text, Math.Max(9, look.Size * 0.7), false, WordBrush, look.Font, look.Weight) { HorizontalAlignment = HorizontalAlignment.Center });
        }
        else
        {
            stack.Children.Add(Number(text, critical, look));
            stack.Children.Add(new OutlinedText(character, Math.Max(9, look.Size * 0.55), false, Brushes.Gainsboro, look.Font, look.Weight) { HorizontalAlignment = HorizontalAlignment.Center });
        }

        stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double height = stack.DesiredSize.Height;
        Canvas.SetLeft(stack, LeftFor(stack.DesiredSize.Width, look.Right));
        Canvas.SetTop(stack, 0);
        Lane.Children.Add(stack);
        if (!word && mergeKey.Length > 0)
        {
            _merged[key] = new Merged { Stack = stack, Prefix = prefix, Total = amount, Count = 1, Critical = critical, Last = now };
        }

        var duration = new Duration(TimeSpan.FromSeconds(Seconds));
        var fall = new DoubleAnimation(0, Math.Max(0, Lane.ActualHeight - height), duration);
        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2 } };
        fade.Completed += (_, _) => Lane.Children.Remove(stack);
        stack.BeginAnimation(Canvas.TopProperty, fall);
        stack.BeginAnimation(OpacityProperty, fade);
        if (_merged.Count > 64)
        {
            foreach (string old in _merged.Where(p => now - p.Value.Last > MergeWindow).Select(p => p.Key).ToList())
            {
                _merged.Remove(old);
            }
        }
    }

    private static OutlinedText Number(string text, bool critical, FloatingLook look)
    {
        var brush = new SolidColorBrush(look.Color);
        brush.Freeze();
        return new OutlinedText(text, critical ? look.Size * 1.3 : look.Size, critical, brush, look.Font, look.Weight) { HorizontalAlignment = HorizontalAlignment.Center };
    }

    /// <summary>Centers a number in its lane (the left or right half), keeping it inside the window.</summary>
    private double LeftFor(double width, bool right)
    {
        double center = Lane.ActualWidth * (right ? 0.75 : 0.25);
        return Math.Clamp(center - width / 2, 0, Math.Max(0, Lane.ActualWidth - width));
    }
}
