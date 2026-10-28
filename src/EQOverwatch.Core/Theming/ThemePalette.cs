namespace EQOverwatch.Core.Theming;

public enum ThemeColorKey
{
    WindowBg,
    CardBg,
    Accent,
    TextPrimary,
    BuffsBar,
    DebuffsBar,
    SongsBar,
    RaidTriggersBar,
    ChChainBar,
}

public static class ThemeColorKeyExtensions
{
    public static IReadOnlyList<ThemeColorKey> All { get; } = Enum.GetValues<ThemeColorKey>();

    public static string Label(this ThemeColorKey key) => key switch
    {
        ThemeColorKey.WindowBg => "Window background",
        ThemeColorKey.CardBg => "Card background",
        ThemeColorKey.Accent => "Accent",
        ThemeColorKey.TextPrimary => "Text",
        ThemeColorKey.BuffsBar => "Buffs bars",
        ThemeColorKey.DebuffsBar => "Debuffs bars",
        ThemeColorKey.SongsBar => "Songs bars",
        ThemeColorKey.RaidTriggersBar => "Raid Triggers bars",
        ThemeColorKey.ChChainBar => "CH Chain bars",
        _ => key.ToString(),
    };
}

/// <summary>The nine configurable colors (as hex strings) that define a theme.</summary>
public sealed record ThemePalette(
    string WindowBg,
    string CardBg,
    string Accent,
    string TextPrimary,
    string BuffsBar,
    string DebuffsBar,
    string SongsBar,
    string RaidTriggersBar,
    string ChChainBar = "#EC4899")
{
    public string Get(ThemeColorKey key) => key switch
    {
        ThemeColorKey.WindowBg => WindowBg,
        ThemeColorKey.CardBg => CardBg,
        ThemeColorKey.Accent => Accent,
        ThemeColorKey.TextPrimary => TextPrimary,
        ThemeColorKey.BuffsBar => BuffsBar,
        ThemeColorKey.DebuffsBar => DebuffsBar,
        ThemeColorKey.SongsBar => SongsBar,
        ThemeColorKey.RaidTriggersBar => RaidTriggersBar,
        ThemeColorKey.ChChainBar => ChChainBar,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    public ThemePalette With(ThemeColorKey key, string hex) => key switch
    {
        ThemeColorKey.WindowBg => this with { WindowBg = hex },
        ThemeColorKey.CardBg => this with { CardBg = hex },
        ThemeColorKey.Accent => this with { Accent = hex },
        ThemeColorKey.TextPrimary => this with { TextPrimary = hex },
        ThemeColorKey.BuffsBar => this with { BuffsBar = hex },
        ThemeColorKey.DebuffsBar => this with { DebuffsBar = hex },
        ThemeColorKey.SongsBar => this with { SongsBar = hex },
        ThemeColorKey.RaidTriggersBar => this with { RaidTriggersBar = hex },
        ThemeColorKey.ChChainBar => this with { ChChainBar = hex },
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };
}
