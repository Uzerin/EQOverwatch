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
/// number appears at the top of its lane with its character's name under it and pushes the older ones down, so they never overlap;
/// each fades out over <see cref="Seconds"/> seconds, and one pushed past the bottom is removed. Crits are bold and larger; misses and
/// resists are small gray words. At most <see cref="MaxShown"/> numbers are up in each lane.
/// </summary>
public partial class FloatingTextOverlayWindow : OverlayWindowBase
{
    public const double Seconds = 3;
    public const int MaxShown = 16;

    /// <summary>How long the older numbers take to slide down when a new one arrives.</summary>
    public static readonly TimeSpan PushTime = TimeSpan.FromMilliseconds(120);

    private const double Gap = 2;

    /// <summary>Hits this close together, from one character on one target, merge into one number when merging is on.</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMilliseconds(300);

    private static readonly Brush WordBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)));

    private readonly Dictionary<string, Merged> _merged = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StackPanel> _left = new();
    private readonly List<StackPanel> _right = new();

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
            ReplaceFirst(merged.Stack, Number(mergedText, merged.Critical, look));
            merged.Stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(merged.Stack, LeftFor(merged.Stack.DesiredSize.Width, look.Right));
            Restack(look.Right ? _right : _left);
            return;
        }

        List<StackPanel> lane = look.Right ? _right : _left;
        while (lane.Count >= MaxShown)
        {
            Remove(lane, lane[^1]);
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
        Canvas.SetLeft(stack, LeftFor(stack.DesiredSize.Width, look.Right));
        Canvas.SetTop(stack, 0);
        Lane.Children.Add(stack);
        lane.Insert(0, stack);
        Restack(lane);
        if (!word && mergeKey.Length > 0)
        {
            _merged[key] = new Merged { Stack = stack, Prefix = prefix, Total = amount, Count = 1, Critical = critical, Last = now };
        }

        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromSeconds(Seconds))) { EasingFunction = new PowerEase { EasingMode = EasingMode.EaseIn, Power = 3 } };
        fade.Completed += (_, _) => Remove(lane, stack);
        stack.BeginAnimation(OpacityProperty, fade);
        if (_merged.Count > 64)
        {
            foreach (string old in _merged.Where(p => now - p.Value.Last > MergeWindow).Select(p => p.Key).ToList())
            {
                _merged.Remove(old);
            }
        }
    }

    private static readonly Brush BannerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4A)));
    private string _bannerKey = string.Empty;

    /// <summary>
    /// Shows the stun and lost-control banners (UI thread), centered; each is hidden for the last quarter of every second it shows, so
    /// it flashes. An empty list clears them.
    /// </summary>
    public void SetBanners(IReadOnlyList<(string Text, DateTime Started)> banners, DateTime now, FontFamily font)
    {
        string key = string.Join("|", banners.Select(b => b.Text));
        if (key != _bannerKey)
        {
            _bannerKey = key;
            Banners.Children.Clear();
            foreach ((string text, _) in banners)
            {
                Banners.Children.Add(new OutlinedText(text, 20, true, BannerBrush, font, FontWeights.Bold)
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
        }

        for (int i = 0; i < banners.Count && i < Banners.Children.Count; i++)
        {
            double phase = (now - banners[i].Started).TotalSeconds % 1.0;
            Banners.Children[i].Opacity = phase < 0.75 ? 1 : 0;
        }
    }

    /// <summary>Lays a lane out newest first from the top, sliding each number to its new place; drops any pushed past the bottom.</summary>
    private void Restack(List<StackPanel> lane)
    {
        double y = 0;
        foreach (StackPanel item in lane.ToList())
        {
            double height = item.DesiredSize.Height;
            if (y > 0 && y + height > Lane.ActualHeight)
            {
                Remove(lane, item);
                continue;
            }

            double from = (double)item.GetValue(Canvas.TopProperty);
            if (double.IsNaN(from) || Math.Abs(from - y) < 0.5)
            {
                item.BeginAnimation(Canvas.TopProperty, null);
                Canvas.SetTop(item, y);
            }
            else
            {
                item.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(from, y, new Duration(PushTime)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            }

            y += height + Gap;
        }
    }

    /// <summary>Puts <paramref name="element"/> in place of the panel's first child. WPF will not put an element into a slot that still holds one, so the old one comes out first.</summary>
    public static void ReplaceFirst(Panel panel, UIElement element)
    {
        if (panel.Children.Count > 0)
        {
            panel.Children.RemoveAt(0);
        }

        panel.Children.Insert(0, element);
    }

    private void Remove(List<StackPanel> lane, StackPanel item)
    {
        lane.Remove(item);
        Lane.Children.Remove(item);
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
