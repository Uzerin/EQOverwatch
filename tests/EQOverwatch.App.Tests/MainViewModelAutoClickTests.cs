using EQOverwatch.App.Services;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_red_outline_follows_the_auto_click_switch_and_moves_to_raid_triggers_when_ch_chain_is_off()
    {
        using var h = new Harness();
        h.Vm.IsChChainOverlayVisible = true;
        Assert.Null(h.Overlays.Outline);

        h.Vm.AutoClickEnabled = true;
        Assert.Equal(OverlayKind.ChChain, h.Overlays.Outline);

        h.Vm.IsChChainOverlayVisible = false;
        Assert.Equal(OverlayKind.RaidTriggers, h.Overlays.Outline);

        h.Vm.ToggleAutoClick(); // the hotkey
        Assert.Null(h.Overlays.Outline);
    }

    [Fact]
    public void The_auto_click_switch_and_spot_are_kept_and_it_opens_as_it_was_left()
    {
        AppSettings saved;
        using (var h = new Harness())
        {
            h.Vm.AutoClickEnabled = true;
            h.Vm.ApplyAutoClickSpot(new ClickSpot(412, 655));
            Assert.Equal("412, 655 in the EverQuest window", h.Vm.AutoClickSpotText);
            saved = h.Saved();
        }

        Assert.True(saved.AutoClickEnabled);
        Assert.Equal(412, saved.AutoClickSpotX);
        Assert.Equal(655, saved.AutoClickSpotY);

        using var again = new Harness((s, _) =>
        {
            s.ChChainOverlay = s.ChChainOverlay with { IsVisible = true };
            s.AutoClickEnabled = true;
            s.AutoClickSpotX = 412;
            s.AutoClickSpotY = 655;
        });
        Assert.True(again.Vm.AutoClickEnabled);
        Assert.True(again.Pipeline.AutoClick.IsArmed);
        Assert.Equal(new ClickSpot(412, 655), again.Pipeline.AutoClick.Spot);
        Assert.Equal(OverlayKind.ChChain, again.Overlays.Outline);

        again.Vm.UseMousePointerCommand.Execute(null);
        Assert.Null(again.Pipeline.AutoClick.Spot);
        Assert.Equal("Where the mouse pointer is", again.Vm.AutoClickSpotText);
    }

    [Fact]
    public void A_cancelled_pick_keeps_the_spot_and_says_why()
    {
        using var h = new Harness();
        h.Vm.ApplyAutoClickSpot(new ClickSpot(10, 20));
        h.Vm.ApplyAutoClickSpot(null, "No EverQuest window was found.");
        Assert.Equal(new ClickSpot(10, 20), h.Vm.AutoClickSpot);
        Assert.Equal("No EverQuest window was found.", h.Vm.AutoClickSpotNote);
    }

    [Fact]
    public void The_status_line_names_the_character_position_and_tag_and_follows_a_change_at_once()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Oodlesii") };
        h.Vm.AddLogCommand.Execute(null);
        h.Vm.AutoClickEnabled = true;
        Assert.StartsWith("On, but none of your characters holds a CH Chain position", h.Vm.AutoClickStatus);

        h.Vm.Logs[0].ChChainPosition = 3;
        h.Vm.Refresh();
        Assert.Contains("Oodlesii at position 3, on \"3-3-3 CAST NOW\"", h.Vm.AutoClickStatus);

        h.Vm.Logs[0].ChChainPosition = 6;
        h.Vm.Refresh();
        Assert.Contains("Oodlesii at position 6, on \"6-6-6 CAST NOW\"", h.Vm.AutoClickStatus);
    }

    [Fact]
    public void Timed_mode_interval_and_key_are_checked_and_kept()
    {
        AppSettings saved;
        using (var h = new Harness())
        {
            h.Vm.AutoClickTimed = true;
            h.Vm.AutoClickIntervalText = (12.5).ToString(System.Globalization.CultureInfo.CurrentCulture);
            Assert.Equal(12.5, h.Pipeline.AutoClick.IntervalSeconds);

            h.Vm.AutoClickIntervalText = "9";
            Assert.StartsWith("Between 10.0 and 60.0", h.Vm.AutoClickIntervalError);
            Assert.Equal(12.5, h.Pipeline.AutoClick.IntervalSeconds); // the last good value

            h.Vm.AutoClickPressKey = true;
            h.Vm.SetAutoClickKeyCommand.Execute(null);
            Assert.Equal("Press a key…", h.Vm.AutoClickKeyText);
            h.Vm.RecordAutoClickKey(new KeyChord(0x72, Shift: true));
            Assert.Equal("Shift+F3", h.Vm.AutoClickKeyText);
            Assert.Equal(AutoClickAction.KeyPress, h.Pipeline.AutoClick.Action);
            Assert.Equal(AutoClickTiming.Timed, h.Pipeline.AutoClick.Timing);

            h.Vm.AutoClickEnabled = true;
            Assert.StartsWith("On, timed: waiting for your first Complete Heal", h.Vm.AutoClickStatus);
            saved = h.Saved();
        }

        Assert.True(saved.AutoClickTimed);
        Assert.Equal(12.5, saved.AutoClickIntervalSeconds);
        Assert.True(saved.AutoClickPressKey);
        Assert.Equal("Shift+F3", saved.AutoClickKey);

        using var again = new Harness((s, _) =>
        {
            s.AutoClickTimed = true;
            s.AutoClickIntervalSeconds = 12.5;
            s.AutoClickPressKey = true;
            s.AutoClickKey = "Shift+F3";
        });
        Assert.Equal(AutoClickTiming.Timed, again.Pipeline.AutoClick.Timing);
        Assert.Equal(12.5, again.Pipeline.AutoClick.IntervalSeconds);
        Assert.Equal(new KeyChord(0x72, Shift: true), again.Pipeline.AutoClick.Key);
    }
}
