namespace EQOverwatch.Core.Settings;

/// <summary>A rectangle in device-independent units (e.g. the WPF virtual screen).</summary>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>Keeps a restored overlay on screen even if monitors were unplugged or the resolution changed.</summary>
public static class OverlayPlacement
{
    /// <summary>
    /// Returns <paramref name="settings"/> with size and position forced fully inside <paramref name="virtualScreen"/>.
    /// Non-finite values fall back to <paramref name="defaults"/>; the size never drops below the minimums (unless the
    /// screen itself is smaller). Visibility and lock state are preserved.
    /// </summary>
    public static OverlaySettings Clamp(
        OverlaySettings settings,
        ScreenRect virtualScreen,
        double minWidth,
        double minHeight,
        OverlaySettings defaults)
    {
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
        {
            return settings;
        }

        double width = Math.Clamp(Finite(settings.Width, defaults.Width, positive: true),
            Math.Min(minWidth, virtualScreen.Width), virtualScreen.Width);
        double height = Math.Clamp(Finite(settings.Height, defaults.Height, positive: true),
            Math.Min(minHeight, virtualScreen.Height), virtualScreen.Height);

        double left = Math.Clamp(Finite(settings.Left, defaults.Left), virtualScreen.Left, virtualScreen.Right - width);
        double top = Math.Clamp(Finite(settings.Top, defaults.Top), virtualScreen.Top, virtualScreen.Bottom - height);

        return settings with { Left = left, Top = top, Width = width, Height = height };
    }

    private static double Finite(double value, double fallback, bool positive = false) =>
        double.IsFinite(value) && (!positive || value > 0) ? value : fallback;
}
