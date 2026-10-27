using System.Globalization;

namespace EQCompanion.App.Presentation;

/// <summary>Text formatting shared by the overlay view models. Invariant culture keeps output deterministic.</summary>
public static class OverlayFormat
{
    /// <summary>950 → "950", 1234 → "1.2K", 123456 → "123K", 1234567 → "1.23M", 123456789 → "123.5M".</summary>
    public static string Compact(long value)
    {
        if (value < 0)
        {
            return "-" + Compact(-value);
        }

        if (value < 1_000)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        if (value < 999_500)
        {
            double thousands = value / 1_000.0;
            return thousands.ToString(thousands < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + "K";
        }

        double millions = value / 1_000_000.0;
        return millions.ToString(millions < 100 ? "0.##" : "0.#", CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>Under a minute: "7.3s". A minute or more: "mm:ss" (rounded up, so it never shows 00:00 while running).</summary>
    public static string Remaining(double seconds)
    {
        if (seconds <= 0)
        {
            return "0.0s";
        }

        if (seconds < 59.95)
        {
            return seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        return Duration((int)Math.Ceiling(seconds));
    }

    /// <summary>Encounter length as "mm:ss" (minutes keep counting past 59).</summary>
    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }
}
