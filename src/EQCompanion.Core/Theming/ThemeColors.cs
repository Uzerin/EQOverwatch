using System.Globalization;

namespace EQCompanion.Core.Theming;

/// <summary>An sRGB color with alpha. Kept free of WPF types so theme logic is testable anywhere.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255);

public static class ThemeColors
{
    public static readonly Rgba White = new(255, 255, 255);
    public static readonly Rgba Black = new(0, 0, 0);

    /// <summary>Accepts #RGB, #RRGGBB and #AARRGGBB (the # is optional, case-insensitive).</summary>
    public static bool TryParse(string? hex, out Rgba color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        ReadOnlySpan<char> digits = hex.AsSpan().Trim();
        if (digits.Length > 0 && digits[0] == '#')
        {
            digits = digits[1..];
        }

        foreach (char c in digits)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        switch (digits.Length)
        {
            case 3:
                color = new Rgba(Pair(digits[0], digits[0]), Pair(digits[1], digits[1]), Pair(digits[2], digits[2]));
                return true;
            case 6:
                color = new Rgba(Pair(digits[0], digits[1]), Pair(digits[2], digits[3]), Pair(digits[4], digits[5]));
                return true;
            case 8:
                color = new Rgba(
                    Pair(digits[2], digits[3]), Pair(digits[4], digits[5]), Pair(digits[6], digits[7]), Pair(digits[0], digits[1]));
                return true;
            default:
                return false;
        }
    }

    /// <summary>"#RRGGBB" for opaque colors, "#AARRGGBB" otherwise; upper-case.</summary>
    public static bool TryNormalize(string? hex, out string normalized)
    {
        if (TryParse(hex, out Rgba color))
        {
            normalized = ToHex(color);
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static string ToHex(Rgba color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Always "#AARRGGBB" (what WPF's color converter expects).</summary>
    public static string ToArgbHex(Rgba color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Linear mix: 0 gives <paramref name="from"/>, 1 gives <paramref name="to"/>. Alpha is mixed too.</summary>
    public static Rgba Blend(Rgba from, Rgba to, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        return new Rgba(Mix(from.R, to.R, t), Mix(from.G, to.G, t), Mix(from.B, to.B, t), Mix(from.A, to.A, t));
    }

    public static Rgba WithAlpha(Rgba color, byte alpha) => color with { A = alpha };

    public static Rgba Lighten(Rgba color, double amount) => Blend(color, White, amount);

    public static Rgba Darken(Rgba color, double amount) => Blend(color, Black, amount);

    private static byte Pair(char high, char low) =>
        byte.Parse(new string(new[] { high, low }), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    private static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
}
