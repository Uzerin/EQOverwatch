namespace EQOverwatch.Core.Theming;

/// <summary>
/// Expands the nine configurable colors into every brush the UI uses (hover, pressed, borders, overlay backdrops, ...).
/// The result maps a resource key to an "#AARRGGBB" color; the WPF side turns each into a SolidColorBrush.
/// </summary>
public static class ThemeDerived
{
    /// <summary>Every resource key <see cref="Build"/> produces.</summary>
    public static IReadOnlyList<string> BrushKeys { get; } = new[]
    {
        "WindowBgBrush", "CardBgBrush", "AccentBrush", "AccentHoverBrush", "AccentPressedBrush",
        "TextPrimaryBrush", "TextMutedBrush", "BorderSoftBrush", "InputBgBrush", "HoverBgBrush", "SelectedBgBrush",
        "BuffsBarBrush", "DebuffsBarBrush", "SongsBarBrush", "RaidTriggersBarBrush", "ChChainBarBrush",
        "UnlockedBackdropBrush", "TitleStripBrush", "DashedBorderBrush", "OverlayTextBrush", "OverlayMutedTextBrush",
        "AlertTextBrush", "BarFillBrush", "DpsFillBrush",
    };

    public static IReadOnlyDictionary<string, string> Build(ThemePalette palette)
    {
        ThemePalette p = ThemePresets.Sanitize(palette);

        Rgba window = Parse(p.WindowBg);
        Rgba card = Parse(p.CardBg);
        Rgba accent = Parse(p.Accent);
        Rgba text = Parse(p.TextPrimary);
        Rgba muted = ThemeColors.Blend(text, window, 0.45);

        var colors = new Dictionary<string, Rgba>
        {
            ["WindowBgBrush"] = window,
            ["CardBgBrush"] = card,
            ["AccentBrush"] = accent,
            ["AccentHoverBrush"] = ThemeColors.Lighten(accent, 0.12),
            ["AccentPressedBrush"] = ThemeColors.Darken(accent, 0.15),
            ["TextPrimaryBrush"] = text,
            ["TextMutedBrush"] = muted,
            ["BorderSoftBrush"] = ThemeColors.Blend(card, text, 0.14),
            ["InputBgBrush"] = ThemeColors.Blend(card, window, 0.5),
            ["HoverBgBrush"] = ThemeColors.Blend(card, accent, 0.18),
            ["SelectedBgBrush"] = ThemeColors.Blend(card, accent, 0.38),

            ["BuffsBarBrush"] = Parse(p.BuffsBar),
            ["DebuffsBarBrush"] = Parse(p.DebuffsBar),
            ["SongsBarBrush"] = Parse(p.SongsBar),
            ["RaidTriggersBarBrush"] = Parse(p.RaidTriggersBar),
            ["ChChainBarBrush"] = Parse(p.ChChainBar),

            // Overlay windows
            ["UnlockedBackdropBrush"] = ThemeColors.WithAlpha(window, 0xAA),
            ["TitleStripBrush"] = ThemeColors.WithAlpha(ThemeColors.Blend(window, accent, 0.5), 0xCC),
            ["DashedBorderBrush"] = ThemeColors.WithAlpha(accent, 0xCC),
            ["OverlayTextBrush"] = text,
            ["OverlayMutedTextBrush"] = muted,
            ["AlertTextBrush"] = Parse(p.RaidTriggersBar),
            ["BarFillBrush"] = accent,
            ["DpsFillBrush"] = ThemeColors.WithAlpha(accent, 0xCC),
        };

        return colors.ToDictionary(pair => pair.Key, pair => ThemeColors.ToArgbHex(pair.Value));
    }

    private static Rgba Parse(string hex) =>
        ThemeColors.TryParse(hex, out Rgba color) ? color : ThemeColors.Black;
}
