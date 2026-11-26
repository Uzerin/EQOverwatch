using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public sealed class WelcomeOverlayTests
{
    [Fact]
    public void Every_overlay_is_hidden_while_the_first_run_window_is_open()
    {
        var settings = new AppSettings();
        MainViewModel.HideOverlaysForWelcome(settings);
        Assert.All(new[]
        {
            settings.BuffsOverlay, settings.DebuffsOverlay, settings.SongsOverlay, settings.RaidTriggersOverlay, settings.ChChainOverlay,
            settings.DpsOverlay, settings.TellsOverlay, settings.MobCardOverlay, settings.FctDamageOverlay, settings.FctHealsOverlay,
        }, overlay => Assert.False(overlay.IsVisible));
        Assert.Equal(60, settings.BuffsOverlay.Left); // the placement is kept
    }

    [Fact]
    public void The_first_run_window_starts_with_four_overlays_ticked()
    {
        Assert.Equal(new[] { OverlayKind.Buffs, OverlayKind.Debuffs, OverlayKind.RaidTriggers, OverlayKind.Dps }, MainViewModel.WelcomeOverlayDefaults);
    }
}
