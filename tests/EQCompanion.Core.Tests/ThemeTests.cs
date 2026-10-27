using EQCompanion.Core.Models;
using EQCompanion.Core.Settings;
using EQCompanion.Core.Theming;
using Xunit;

namespace EQCompanion.Core.Tests;

public sealed class ThemeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ───────────────────────── presets ─────────────────────────

    [Fact]
    public void The_three_built_in_presets_have_the_specified_colors()
    {
        ThemePalette obsidian = ThemePresets.ObsidianSlate;
        Assert.Equal(("#0F111A", "#181B28", "#6366F1"), (obsidian.WindowBg, obsidian.CardBg, obsidian.Accent));
        Assert.Equal(("#10B981", "#EF4444", "#A855F7", "#F59E0B"),
            (obsidian.BuffsBar, obsidian.DebuffsBar, obsidian.SongsBar, obsidian.RaidTriggersBar));

        ThemePalette velious = ThemePresets.VeliousFrost;
        Assert.Equal(("#0B1520", "#132334", "#0EA5E9"), (velious.WindowBg, velious.CardBg, velious.Accent));
        Assert.Equal(("#2DD4BF", "#F43F5E", "#818CF8", "#FACC15"),
            (velious.BuffsBar, velious.DebuffsBar, velious.SongsBar, velious.RaidTriggersBar));

        ThemePalette kunark = ThemePresets.KunarkEmber;
        Assert.Equal(("#161110", "#241B19", "#EA580C"), (kunark.WindowBg, kunark.CardBg, kunark.Accent));
        Assert.Equal(("#22C55E", "#DC2626", "#C084FC", "#FBBF24"),
            (kunark.BuffsBar, kunark.DebuffsBar, kunark.SongsBar, kunark.RaidTriggersBar));
    }

    [Fact]
    public void Preset_names_and_display_names()
    {
        Assert.Equal(new[] { "ObsidianSlate", "VeliousFrost", "KunarkEmber", "Custom" }, ThemePresets.Names);
        Assert.Equal(new[] { "Obsidian Slate", "Velious Frost", "Kunark Ember", "Custom" }, ThemePresets.Names.Select(ThemePresets.DisplayName));
    }

    [Theory]
    [InlineData("ObsidianSlate", "#0F111A")]
    [InlineData("veliousfrost", "#0B1520")]
    [InlineData("  KunarkEmber ", "#161110")]
    [InlineData("nonsense", "#0F111A")] // unknown names fall back to Obsidian Slate
    [InlineData(null, "#0F111A")]
    public void Resolve_picks_the_preset_by_name_ignoring_case(string? name, string expectedWindowBg) =>
        Assert.Equal(expectedWindowBg, ThemePresets.Resolve(new ThemeConfig { PresetName = name! }).WindowBg);

    [Fact]
    public void Custom_uses_the_configs_own_colors_and_fixes_invalid_ones()
    {
        var config = new ThemeConfig
        {
            PresetName = "Custom",
            WindowBgHex = "#102030",
            CardBgHex = "abc",              // shorthand without #
            AccentHex = "not a color",      // invalid: falls back to Obsidian's accent
            TextPrimaryHex = "#80FFFFFF",   // alpha is allowed
            BuffsBarHex = "#112233",
        };

        ThemePalette palette = ThemePresets.Resolve(config);

        Assert.Equal("#102030", palette.WindowBg);
        Assert.Equal("#AABBCC", palette.CardBg);
        Assert.Equal(ThemePresets.ObsidianSlate.Accent, palette.Accent);
        Assert.Equal("#80FFFFFF", palette.TextPrimary);
        Assert.Equal("#112233", palette.BuffsBar);
    }

    [Fact]
    public void Custom_colors_are_kept_while_a_preset_is_active()
    {
        var config = new ThemeConfig { PresetName = "Custom" };
        config.SetCustomPalette(ThemePresets.ObsidianSlate.With(ThemeColorKey.Accent, "#FF00FF"));

        config.PresetName = "VeliousFrost";
        Assert.Equal(ThemePresets.VeliousFrost, ThemePresets.Resolve(config));
        Assert.Equal("#FF00FF", config.AccentHex);

        config.PresetName = "Custom";
        Assert.Equal("#FF00FF", ThemePresets.Resolve(config).Accent);
    }

    [Fact]
    public void Palette_accessors_cover_every_key()
    {
        ThemePalette palette = ThemePresets.KunarkEmber;
        foreach (ThemeColorKey key in ThemeColorKeyExtensions.All)
        {
            Assert.Equal("#123456", palette.With(key, "#123456").Get(key));
            Assert.False(string.IsNullOrWhiteSpace(key.Label()));
        }

        Assert.Equal(9, ThemeColorKeyExtensions.All.Count);
    }

    // ───────────────────────── colors ─────────────────────────

    [Theory]
    [InlineData("#0F111A", 0x0F, 0x11, 0x1A, 255)]
    [InlineData("0f111a", 0x0F, 0x11, 0x1A, 255)]
    [InlineData("#FFF", 255, 255, 255, 255)]
    [InlineData("#abc", 0xAA, 0xBB, 0xCC, 255)]
    [InlineData("#80112233", 0x11, 0x22, 0x33, 0x80)]
    public void Valid_hex_forms_parse(string hex, int r, int g, int b, int a)
    {
        Assert.True(ThemeColors.TryParse(hex, out Rgba color));
        Assert.Equal(new Rgba((byte)r, (byte)g, (byte)b, (byte)a), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    [InlineData("red")]
    public void Invalid_hex_forms_are_rejected(string hex) => Assert.False(ThemeColors.TryParse(hex, out _));

    [Fact]
    public void Colors_normalize_to_upper_case_hex()
    {
        Assert.True(ThemeColors.TryNormalize("#0f111a", out string opaque));
        Assert.Equal("#0F111A", opaque);
        Assert.True(ThemeColors.TryNormalize("80ffffff", out string translucent));
        Assert.Equal("#80FFFFFF", translucent);
        Assert.Equal("#FF0F111A", ThemeColors.ToArgbHex(new Rgba(0x0F, 0x11, 0x1A)));
        Assert.False(ThemeColors.TryNormalize("nope", out _));
    }

    [Fact]
    public void Blending_and_shading_behave()
    {
        var black = new Rgba(0, 0, 0);
        var white = new Rgba(255, 255, 255);

        Assert.Equal(black, ThemeColors.Blend(black, white, 0));
        Assert.Equal(white, ThemeColors.Blend(black, white, 1));
        Assert.Equal(new Rgba(128, 128, 128), ThemeColors.Blend(black, white, 0.5));
        Assert.Equal(white, ThemeColors.Blend(black, white, 5)); // clamped

        var accent = new Rgba(0x63, 0x66, 0xF1);
        Rgba lighter = ThemeColors.Lighten(accent, 0.2);
        Rgba darker = ThemeColors.Darken(accent, 0.2);
        Assert.True(lighter.R > accent.R && lighter.G > accent.G && lighter.B > accent.B);
        Assert.True(darker.R < accent.R && darker.G < accent.G && darker.B < accent.B);
        Assert.Equal(0xAA, ThemeColors.WithAlpha(accent, 0xAA).A);
    }

    [Fact]
    public void Derived_brushes_cover_every_key_and_follow_the_palette()
    {
        IReadOnlyDictionary<string, string> brushes = ThemeDerived.Build(ThemePresets.ObsidianSlate);

        Assert.Equal(ThemeDerived.BrushKeys.OrderBy(k => k), brushes.Keys.OrderBy(k => k));
        Assert.Equal("#FF0F111A", brushes["WindowBgBrush"]);
        Assert.Equal("#FF181B28", brushes["CardBgBrush"]);
        Assert.Equal("#FF6366F1", brushes["AccentBrush"]);
        Assert.Equal("#FF10B981", brushes["BuffsBarBrush"]);
        Assert.Equal("#FFEF4444", brushes["DebuffsBarBrush"]);
        Assert.Equal("#FFA855F7", brushes["SongsBarBrush"]);
        Assert.Equal("#FFF59E0B", brushes["RaidTriggersBarBrush"]);
        Assert.Equal("#AA0F111A", brushes["UnlockedBackdropBrush"]);         // overlay backdrop: the window color, translucent
        Assert.Equal(brushes["RaidTriggersBarBrush"], brushes["AlertTextBrush"]);
        Assert.Equal("#CC6366F1", brushes["DpsFillBrush"]);
        Assert.All(brushes.Values, v => Assert.True(ThemeColors.TryParse(v, out _), v));

        // A different theme produces different brushes everywhere that matters.
        IReadOnlyDictionary<string, string> frost = ThemeDerived.Build(ThemePresets.VeliousFrost);
        Assert.Equal("#FF0EA5E9", frost["AccentBrush"]);
        Assert.NotEqual(brushes["HoverBgBrush"], frost["HoverBgBrush"]);
    }

    [Fact]
    public void Hover_and_pressed_shades_are_lighter_and_darker_than_the_accent()
    {
        IReadOnlyDictionary<string, string> brushes = ThemeDerived.Build(ThemePresets.KunarkEmber);
        ThemeColors.TryParse(brushes["AccentBrush"], out Rgba accent);
        ThemeColors.TryParse(brushes["AccentHoverBrush"], out Rgba hover);
        ThemeColors.TryParse(brushes["AccentPressedBrush"], out Rgba pressed);

        Assert.True(hover.R + hover.G + hover.B > accent.R + accent.G + accent.B);
        Assert.True(pressed.R + pressed.G + pressed.B < accent.R + accent.G + accent.B);
    }

    [Fact]
    public void Derived_brushes_survive_an_invalid_palette()
    {
        ThemePalette broken = ThemePresets.ObsidianSlate with { Accent = "???", WindowBg = string.Empty };

        IReadOnlyDictionary<string, string> brushes = ThemeDerived.Build(broken);

        Assert.Equal("#FF6366F1", brushes["AccentBrush"]); // Obsidian's value stands in
        Assert.Equal("#FF0F111A", brushes["WindowBgBrush"]);
    }

    // ───────────────────────── persistence ─────────────────────────

    [Fact]
    public void The_theme_round_trips_through_settings_json()
    {
        var settings = new AppSettings();
        settings.Theme.PresetName = "Custom";
        settings.Theme.SetCustomPalette(new ThemePalette(
            "#101010", "#202020", "#30A0F0", "#EEEEEE", "#11AA11", "#AA1111", "#AA11AA", "#AAAA11"));
        new AppSettingsService(SettingsPath).Save(settings);

        AppSettings loaded = new AppSettingsService(SettingsPath).Load();

        Assert.Equal("Custom", loaded.Theme.PresetName);
        Assert.Equal("#30A0F0", loaded.Theme.AccentHex);
        Assert.Equal("#AAAA11", loaded.Theme.RaidTriggersBarHex);
        Assert.Equal("#101010", ThemePresets.Resolve(loaded.Theme).WindowBg);
    }

    [Fact]
    public void A_preset_selection_is_saved_along_with_the_remembered_custom_colors()
    {
        var settings = new AppSettings();
        settings.Theme.SetCustomPalette(ThemePresets.ObsidianSlate.With(ThemeColorKey.Accent, "#FF00FF"));
        settings.Theme.PresetName = "KunarkEmber";
        new AppSettingsService(SettingsPath).Save(settings);

        ThemeConfig loaded = new AppSettingsService(SettingsPath).Load().Theme;

        Assert.Equal("KunarkEmber", loaded.PresetName);
        Assert.Equal("#FF00FF", loaded.AccentHex);
        Assert.Equal(ThemePresets.KunarkEmber, ThemePresets.Resolve(loaded));
    }

    [Fact]
    public void A_missing_or_damaged_theme_loads_as_obsidian_slate()
    {
        Directory.CreateDirectory(_dir);

        File.WriteAllText(SettingsPath, "{ }");
        Assert.Equal("ObsidianSlate", new AppSettingsService(SettingsPath).Load().Theme.PresetName);

        File.WriteAllText(SettingsPath, """{ "Theme": null }""");
        Assert.Equal("ObsidianSlate", new AppSettingsService(SettingsPath).Load().Theme.PresetName);

        File.WriteAllText(SettingsPath, """{ "Theme": { "PresetName": "Hotdog", "AccentHex": "zzz", "WindowBgHex": "#abcdef" } }""");
        ThemeConfig loaded = new AppSettingsService(SettingsPath).Load().Theme;
        Assert.Equal("ObsidianSlate", loaded.PresetName);
        Assert.Equal(ThemePresets.ObsidianSlate.Accent, loaded.AccentHex);
        Assert.Equal("#ABCDEF", loaded.WindowBgHex);
    }

    // ───────────────────────── the other Phase 6 settings ─────────────────────────

    [Fact]
    public void All_five_overlays_and_the_raid_boss_rules_round_trip_and_enums_are_saved_by_name()
    {
        var settings = new AppSettings
        {
            BuffsOverlay = new OverlaySettings { Left = 1, Top = 2, Width = 300, Height = 200, IsLocked = true },
            DebuffsOverlay = new OverlaySettings { Left = 3, Top = 4, Width = 310, Height = 210, IsVisible = false },
            SongsOverlay = new OverlaySettings { Left = 5, Top = 6, Width = 320, Height = 220 },
            RaidTriggersOverlay = new OverlaySettings { Left = 7, Top = 8, Width = 330, Height = 230, IsLocked = true },
            DpsOverlay = new OverlaySettings { Left = 9, Top = 10, Width = 340, Height = 240 },
        };
        settings.RaidBossTimers.Add(new RaidBossTimerRule
        {
            MobName = "Tormax", SpellId = 1234, SpellName = "Dragon Roar", MatchText = "is blasted", RecastSeconds = 30,
            OverlayCategory = TimerOverlayCategory.Debuffs, WarningTtsText = "Roar!", WarningTiming = RaidWarningTiming.FiveSecondsRemaining,
        });
        settings.Triggers.Add(new TriggerRule { Name = "x", Pattern = "x", OverlayCategory = TimerOverlayCategory.Songs });
        new AppSettingsService(SettingsPath).Save(settings);

        string json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"Debuffs\"", json);
        Assert.Contains("\"FiveSecondsRemaining\"", json);

        AppSettings loaded = new AppSettingsService(SettingsPath).Load();
        Assert.Equal(settings.BuffsOverlay, loaded.BuffsOverlay);
        Assert.Equal(settings.DebuffsOverlay, loaded.DebuffsOverlay);
        Assert.Equal(settings.SongsOverlay, loaded.SongsOverlay);
        Assert.Equal(settings.RaidTriggersOverlay, loaded.RaidTriggersOverlay);
        Assert.Equal(settings.DpsOverlay, loaded.DpsOverlay);
        Assert.Equal(loaded.BuffsOverlay, loaded.GetTimerOverlay(TimerOverlayCategory.Buffs));
        Assert.Equal(loaded.RaidTriggersOverlay, loaded.GetTimerOverlay(TimerOverlayCategory.RaidTriggers));

        RaidBossTimerRule rule = Assert.Single(loaded.RaidBossTimers);
        Assert.Equal(settings.RaidBossTimers[0].Id, rule.Id);
        Assert.Equal("Tormax", rule.MobName);
        Assert.Equal(1234, rule.SpellId);
        Assert.Equal(TimerOverlayCategory.Debuffs, rule.OverlayCategory);
        Assert.Null(rule.WarningTiming);                       // converted on load
        Assert.Equal(string.Empty, rule.StartTtsText);
        Assert.Equal("Roar!", rule.WarningTtsText);            // "5 seconds remaining" became the warning text
        Assert.Equal(TimerOverlayCategory.Songs, Assert.Single(loaded.Triggers).OverlayCategory);
    }

    [Fact]
    public void Defaults_lay_the_five_timer_overlays_out_side_by_side()
    {
        var settings = new AppSettings();

        OverlaySettings[] timers = TimerOverlayCategoryExtensions.All.Select(settings.GetTimerOverlay).ToArray();
        Assert.Equal(5, timers.Select(t => (t.Left, t.Top)).Distinct().Count()); // none start on top of another
        Assert.DoesNotContain((settings.DpsOverlay.Left, settings.DpsOverlay.Top), timers.Select(t => (t.Left, t.Top)));
    }

    [Fact]
    public void Old_settings_files_and_hand_edited_raid_rules_are_normalized()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              "TimerOverlay": { "Left": 5 },
              "RaidBossTimers": [ null, { "SpellName": null, "MatchText": null, "MobName": null, "RecastSeconds": 0, "OverlayCategory": 2 } ]
            }
            """);

        AppSettings settings = new AppSettingsService(SettingsPath).Load();

        RaidBossTimerRule rule = Assert.Single(settings.RaidBossTimers);
        Assert.Equal(1, rule.RecastSeconds);
        Assert.Equal(string.Empty, rule.SpellName);
        Assert.Equal(string.Empty, rule.MatchText);
        Assert.Equal(TimerOverlayCategory.Songs, rule.OverlayCategory); // numbers are still accepted
        Assert.Equal(OverlaySettings.ForCategory(TimerOverlayCategory.Buffs), settings.BuffsOverlay); // the old single overlay is not migrated
    }
}
