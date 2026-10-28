using EQOverwatch.App.Services;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Theming;
using Xunit;

namespace EQOverwatch.App.Tests;

public class OverlayKindAndThemeServiceTests
{
    [Fact]
    public void There_are_five_timer_overlays_and_one_dps_overlay_with_the_requested_titles()
    {
        Assert.Equal(6, OverlayKindExtensions.All.Count);
        Assert.Equal(
            new[] { "Buffs", "Debuffs", "Songs", "Raid Triggers", "CH Chain", "DPS" },
            OverlayKindExtensions.All.Select(k => k.Title()));
        Assert.Null(OverlayKind.Dps.Category());
    }

    [Fact]
    public void Overlay_kinds_and_timer_categories_map_to_each_other()
    {
        foreach (TimerOverlayCategory category in TimerOverlayCategoryExtensions.All)
        {
            OverlayKind kind = OverlayKindExtensions.FromCategory(category);
            Assert.Equal(category, kind.Category());
            Assert.Equal(category.DisplayName(), kind.Title());
        }
    }

    [Fact]
    public void The_theme_manager_records_the_sanitized_palette_even_without_a_running_application()
    {
        var manager = new ThemeManagerService();
        Assert.Equal(ThemePresets.ObsidianSlate, manager.Current);

        manager.Apply(ThemePresets.VeliousFrost);
        Assert.Equal(ThemePresets.VeliousFrost, manager.Current);

        manager.Apply(ThemePresets.KunarkEmber with { Accent = "not-a-color", WindowBg = "#abcdef" });
        Assert.Equal(ThemePresets.ObsidianSlate.Accent, manager.Current.Accent); // invalid color replaced
        Assert.Equal("#ABCDEF", manager.Current.WindowBg);                        // valid color normalized
        Assert.Equal(new[] { "ObsidianSlate", "VeliousFrost", "KunarkEmber", "Custom" }, ThemeManagerService.PresetNames);
    }
}
