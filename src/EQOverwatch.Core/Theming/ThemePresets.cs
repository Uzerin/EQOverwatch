namespace EQOverwatch.Core.Theming;

/// <summary>The three built-in dark themes and the logic that turns a <see cref="ThemeConfig"/> into the palette in effect.</summary>
public static class ThemePresets
{
    public const string ObsidianSlateName = "ObsidianSlate";
    public const string VeliousFrostName = "VeliousFrost";
    public const string KunarkEmberName = "KunarkEmber";
    public const string CustomName = "Custom";

    /// <summary>Indigo accent on near-black blue.</summary>
    public static readonly ThemePalette ObsidianSlate = new(
        WindowBg: "#0F111A", CardBg: "#181B28", Accent: "#6366F1", TextPrimary: "#E5E7EB",
        BuffsBar: "#10B981", DebuffsBar: "#EF4444", SongsBar: "#A855F7", RaidTriggersBar: "#F59E0B", ChChainBar: "#EC4899");

    /// <summary>Ice-cyan accent on deep navy.</summary>
    public static readonly ThemePalette VeliousFrost = new(
        WindowBg: "#0B1520", CardBg: "#132334", Accent: "#0EA5E9", TextPrimary: "#E0F2FE",
        BuffsBar: "#2DD4BF", DebuffsBar: "#F43F5E", SongsBar: "#818CF8", RaidTriggersBar: "#FACC15", ChChainBar: "#F97316");

    /// <summary>Ember-orange accent on warm charcoal.</summary>
    public static readonly ThemePalette KunarkEmber = new(
        WindowBg: "#161110", CardBg: "#241B19", Accent: "#EA580C", TextPrimary: "#F5E9E4",
        BuffsBar: "#22C55E", DebuffsBar: "#DC2626", SongsBar: "#C084FC", RaidTriggersBar: "#FBBF24", ChChainBar: "#38BDF8");

    /// <summary>Preset names in display order (Custom last).</summary>
    public static IReadOnlyList<string> Names { get; } =
        new[] { ObsidianSlateName, VeliousFrostName, KunarkEmberName, CustomName };

    public static string DisplayName(string presetName) => Canonical(presetName) switch
    {
        ObsidianSlateName => "Obsidian Slate",
        VeliousFrostName => "Velious Frost",
        KunarkEmberName => "Kunark Ember",
        _ => "Custom",
    };

    /// <summary>The matching preset name (case-insensitive); anything unknown falls back to Obsidian Slate.</summary>
    public static string Canonical(string? presetName) =>
        Names.FirstOrDefault(n => string.Equals(n, presetName?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? ObsidianSlateName;

    /// <summary>The built-in palette for <paramref name="presetName"/>, or null for "Custom".</summary>
    public static ThemePalette? GetPreset(string? presetName) => Canonical(presetName) switch
    {
        ObsidianSlateName => ObsidianSlate,
        VeliousFrostName => VeliousFrost,
        KunarkEmberName => KunarkEmber,
        _ => null,
    };

    /// <summary>The palette to apply: the chosen preset, or the config's custom colors (invalid values fall back to Obsidian Slate's).</summary>
    public static ThemePalette Resolve(ThemeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return GetPreset(config.PresetName) ?? Sanitize(config.GetCustomPalette());
    }

    /// <summary>Normalizes every color to upper-case hex and replaces unparsable ones with Obsidian Slate's.</summary>
    public static ThemePalette Sanitize(ThemePalette palette)
    {
        ThemePalette result = palette;
        foreach (ThemeColorKey key in ThemeColorKeyExtensions.All)
        {
            string value = ThemeColors.TryNormalize(palette.Get(key), out string normalized)
                ? normalized
                : ObsidianSlate.Get(key);
            result = result.With(key, value);
        }

        return result;
    }

    /// <summary>Makes a loaded config safe: a known preset name and valid custom colors.</summary>
    public static ThemeConfig Sanitize(ThemeConfig config)
    {
        config.PresetName = Canonical(config.PresetName);
        config.SetCustomPalette(Sanitize(config.GetCustomPalette()));
        return config;
    }
}
