using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class OverlayPlacementTests
{
    private static readonly ScreenRect Primary = new(0, 0, 1920, 1080);
    private static readonly OverlaySettings Defaults = OverlaySettings.DefaultTimer;

    private static OverlaySettings Clamp(OverlaySettings s, ScreenRect? screen = null) =>
        OverlayPlacement.Clamp(s, screen ?? Primary, minWidth: 200, minHeight: 90, Defaults);

    [Fact]
    public void A_window_fully_on_screen_is_unchanged()
    {
        var s = new OverlaySettings { Left = 300, Top = 200, Width = 320, Height = 240, IsVisible = false, IsLocked = true };
        Assert.Equal(s, Clamp(s));
    }

    [Theory]
    [InlineData(-500, 100, 0, 100)]      // off the left edge
    [InlineData(1800, 100, 1600, 100)]   // overlaps the right edge (width 320)
    [InlineData(100, -50, 100, 0)]       // above the top
    [InlineData(100, 1000, 100, 840)]    // below the bottom (height 240)
    [InlineData(99999, 99999, 1600, 840)]
    public void Windows_are_pulled_fully_onto_the_screen(double left, double top, double expectedLeft, double expectedTop)
    {
        OverlaySettings result = Clamp(new OverlaySettings { Left = left, Top = top, Width = 320, Height = 240 });
        Assert.Equal(expectedLeft, result.Left);
        Assert.Equal(expectedTop, result.Top);
        Assert.Equal(320, result.Width);
        Assert.Equal(240, result.Height);
    }

    [Fact]
    public void Oversized_and_undersized_windows_are_resized_into_range()
    {
        OverlaySettings big = Clamp(new OverlaySettings { Left = 0, Top = 0, Width = 5000, Height = 4000 });
        Assert.Equal(1920, big.Width);
        Assert.Equal(1080, big.Height);

        OverlaySettings tiny = Clamp(new OverlaySettings { Left = 0, Top = 0, Width = 10, Height = 10 });
        Assert.Equal(200, tiny.Width);
        Assert.Equal(90, tiny.Height);
    }

    [Fact]
    public void Non_finite_values_fall_back_to_the_defaults()
    {
        OverlaySettings result = Clamp(new OverlaySettings
        {
            Left = double.NaN, Top = double.PositiveInfinity, Width = double.NaN, Height = -5,
        });

        Assert.Equal(Defaults.Left, result.Left);
        Assert.Equal(Defaults.Top, result.Top);
        Assert.Equal(Defaults.Width, result.Width);
        Assert.Equal(Defaults.Height, result.Height);
    }

    [Fact]
    public void A_monitor_to_the_left_of_the_primary_is_part_of_the_virtual_screen()
    {
        var twoMonitors = new ScreenRect(-1920, 0, 3840, 1080);
        var onSecond = new OverlaySettings { Left = -1500, Top = 40, Width = 280, Height = 220 };

        Assert.Equal(onSecond, Clamp(onSecond, twoMonitors));

        // If that monitor is unplugged the same coordinates are pulled back onto the remaining one.
        OverlaySettings pulled = Clamp(onSecond, Primary);
        Assert.Equal(0, pulled.Left);
        Assert.Equal(40, pulled.Top);
    }

    [Fact]
    public void Visibility_and_lock_state_survive_clamping()
    {
        OverlaySettings result = Clamp(new OverlaySettings { Left = -9999, Top = -9999, Width = 300, Height = 200, IsVisible = false, IsLocked = true });
        Assert.False(result.IsVisible);
        Assert.True(result.IsLocked);
    }

    [Fact]
    public void A_degenerate_screen_leaves_the_settings_alone()
    {
        var s = new OverlaySettings { Left = 5, Top = 6, Width = 7, Height = 8 };
        Assert.Equal(s, Clamp(s, new ScreenRect(0, 0, 0, 0)));
    }
}
