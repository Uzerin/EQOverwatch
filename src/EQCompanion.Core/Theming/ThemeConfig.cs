namespace EQCompanion.Core.Theming;

/// <summary>
/// The persisted theme choice. <see cref="PresetName"/> selects a built-in preset or "Custom"; the hex fields hold the custom
/// palette and are kept while a preset is active, so switching back to Custom restores the user's colors.
/// </summary>
public sealed class ThemeConfig
{
    public string PresetName { get; set; } = ThemePresets.ObsidianSlateName;

    public string WindowBgHex { get; set; } = ThemePresets.ObsidianSlate.WindowBg;
    public string CardBgHex { get; set; } = ThemePresets.ObsidianSlate.CardBg;
    public string AccentHex { get; set; } = ThemePresets.ObsidianSlate.Accent;
    public string TextPrimaryHex { get; set; } = ThemePresets.ObsidianSlate.TextPrimary;
    public string BuffsBarHex { get; set; } = ThemePresets.ObsidianSlate.BuffsBar;
    public string DebuffsBarHex { get; set; } = ThemePresets.ObsidianSlate.DebuffsBar;
    public string SongsBarHex { get; set; } = ThemePresets.ObsidianSlate.SongsBar;
    public string RaidTriggersBarHex { get; set; } = ThemePresets.ObsidianSlate.RaidTriggersBar;
    public string ChChainBarHex { get; set; } = ThemePresets.ObsidianSlate.ChChainBar;

    public ThemePalette GetCustomPalette() => new(
        WindowBgHex, CardBgHex, AccentHex, TextPrimaryHex, BuffsBarHex, DebuffsBarHex, SongsBarHex, RaidTriggersBarHex, ChChainBarHex);

    public void SetCustomPalette(ThemePalette palette)
    {
        WindowBgHex = palette.WindowBg;
        CardBgHex = palette.CardBg;
        AccentHex = palette.Accent;
        TextPrimaryHex = palette.TextPrimary;
        BuffsBarHex = palette.BuffsBar;
        DebuffsBarHex = palette.DebuffsBar;
        SongsBarHex = palette.SongsBar;
        RaidTriggersBarHex = palette.RaidTriggersBar;
        ChChainBarHex = palette.ChChainBar;
    }

    public ThemeConfig Clone() => (ThemeConfig)MemberwiseClone();
}
