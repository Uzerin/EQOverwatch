using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_red_outline_follows_the_auto_press_switch_and_moves_to_raid_triggers_when_ch_chain_is_off()
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
    public void Auto_press_settings_are_checked_kept_and_restored()
    {
        AppSettings saved;
        using (var h = new Harness())
        {
            Assert.Equal("2", h.Vm.AutoClickKeyText); // the defaults
            Assert.Equal("1", h.Vm.AutoClickAssistKeyText);

            h.Vm.AutoClickTimed = true;
            h.Vm.AutoClickIntervalText = (18.0).ToString(System.Globalization.CultureInfo.CurrentCulture);
            Assert.Equal(18.0, h.Pipeline.AutoClick.IntervalSeconds);

            h.Vm.AutoClickIntervalText = "9";
            Assert.StartsWith("Between 10.0 and 60.0", h.Vm.AutoClickIntervalError);
            Assert.Equal(18.0, h.Pipeline.AutoClick.IntervalSeconds); // the last good value

            h.Vm.SetAutoClickKeyCommand.Execute(null);
            Assert.Equal("Press a key…", h.Vm.AutoClickKeyText);
            h.Vm.RecordAutoClickKey(new KeyChord(0x72, Shift: true));
            Assert.Equal("Shift+F3", h.Vm.AutoClickKeyText);

            h.Vm.SetAutoClickAssistKeyCommand.Execute(null);
            Assert.Equal("Press a key…", h.Vm.AutoClickAssistKeyText);
            h.Vm.RecordAutoClickKey(null); // Esc
            Assert.Equal("1", h.Vm.AutoClickAssistKeyText);
            h.Vm.SetAutoClickAssistKeyCommand.Execute(null);
            h.Vm.RecordAutoClickKey(new KeyChord(0x51));
            h.Vm.AutoClickUseAssist = true;

            h.Vm.AutoClickEnabled = true;
            Assert.StartsWith("On, timed: waiting for your first Complete Heal, then presses Q at 1.0 s and 0.5 s before, then Shift+F3", h.Vm.AutoClickStatus);
            saved = h.Saved();
        }

        Assert.True(saved.AutoClickEnabled);
        Assert.True(saved.AutoClickTimed);
        Assert.Equal(18.0, saved.AutoClickIntervalSeconds);
        Assert.Equal("Shift+F3", saved.AutoClickKey);
        Assert.Equal("Q", saved.AutoClickAssistKey);
        Assert.True(saved.AutoClickUseAssist);

        using var again = new Harness((s, _) =>
        {
            s.ChChainOverlay = s.ChChainOverlay with { IsVisible = true };
            s.AutoClickEnabled = true;
            s.AutoClickTimed = true;
            s.AutoClickIntervalSeconds = 18.0;
            s.AutoClickKey = "Shift+F3";
            s.AutoClickAssistKey = "Q";
            s.AutoClickUseAssist = true;
        });
        AutoClickService press = again.Pipeline.AutoClick;
        Assert.True(press.IsArmed);
        Assert.Equal(AutoClickTiming.Timed, press.Timing);
        Assert.Equal(18.0, press.IntervalSeconds);
        Assert.Equal(new KeyChord(0x72, Shift: true), press.Key);
        Assert.Equal(new KeyChord(0x51), press.AssistKey);
        Assert.True(press.UseAssist);
        Assert.Equal(OverlayKind.ChChain, again.Overlays.Outline);
    }

    [Fact]
    public void The_status_line_names_the_character_position_tag_and_keys_and_follows_a_change_at_once()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Oodlesii") };
        h.Vm.AddLogCommand.Execute(null);
        h.Vm.AutoClickEnabled = true;
        Assert.StartsWith("On, but none of your characters holds a CH Chain position", h.Vm.AutoClickStatus);

        h.Vm.Logs[0].ChChainPosition = 3;
        h.Vm.Refresh();
        Assert.Contains("Oodlesii at position 3, on \"3-3-3 CAST NOW\": presses 2.", h.Vm.AutoClickStatus);

        h.Vm.AutoClickUseAssist = true;
        h.Vm.Logs[0].ChChainPosition = 6;
        h.Vm.Refresh();
        Assert.Contains("Oodlesii at position 6, on \"6-6-6 CAST NOW\": presses 1 at 1.0 s and 0.5 s before, then 2.", h.Vm.AutoClickStatus);
    }

    [Theory]
    [InlineData(null, "2")] // 1.0.65 in mouse mode: its key "1" was never used
    [InlineData(false, "2")]
    [InlineData(true, "1")] // 1.0.65 in "Press key" mode: the key was chosen
    public void Settings_from_1_0_65_start_the_ch_key_at_2_unless_its_press_key_mode_was_on(bool? pressKey, string expected)
    {
        using var h = new Harness((s, _) =>
        {
            s.AutoClickKey = "1";
            s.AutoClickAssistKey = null;
            s.AutoClickPressKey = pressKey;
        });
        Assert.Equal(expected, h.Vm.AutoClickKeyText);
        Assert.Equal("1", h.Vm.AutoClickAssistKeyText);
    }
}
