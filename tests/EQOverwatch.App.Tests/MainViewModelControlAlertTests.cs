using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    private static void Line(Harness h, string character, string message, int second)
    {
        h.Pipeline.LiveDps.OnLogOwner(character);
        h.Pipeline.LiveDps.OnLogClock(FightStart.AddSeconds(second));
        h.Pipeline.LiveDps.OnNonCombatMessage(message);
    }

    [Fact]
    public void A_stun_in_a_fight_flashes_on_the_floating_text_overlay_and_ends_when_the_stun_does()
    {
        (Harness h, List<string> spoken) = BoxHarness();
        using (h)
        {
            Line(h, "Plethora", "You are stunned!", 0); // no fight yet: nothing
            Assert.Empty(h.Vm.FloatingBanners(FightStart));

            StartFight(h);
            Line(h, "Plethora", "You are stunned!", 1);
            Assert.Equal(new[] { "Plethora stunned" }, spoken);
            Assert.Equal("PLETHORA STUNNED!", Assert.Single(h.Vm.FloatingBanners(FightStart)).Text);
            Assert.DoesNotContain(h.Pipeline.Triggers.Snapshot.Timers, t => t.Text == "PLETHORA STUNNED!");

            Line(h, "Plethora", "You are no longer stunned.", 2);
            Assert.Empty(h.Vm.FloatingBanners(FightStart));

            // On another overlay it is a banner there, put up again each second so it flashes.
            h.Vm.StunAlertOverlay = AlertOverlay.Debuffs;
            Line(h, "Myriad", "You are stunned!", 3);
            ActiveTimerSnapshot banner = Assert.Single(h.Pipeline.Triggers.Snapshot.Timers, t => t.Text == "MYRIAD STUNNED!");
            Assert.Equal(TimerOverlayCategory.Debuffs, banner.Category);
            h.Vm.TickControlAlerts(FightStart.AddSeconds(1.2));
            Assert.NotEqual(banner.Id, Assert.Single(h.Pipeline.Triggers.Snapshot.Timers, t => t.Text == "MYRIAD STUNNED!").Id);
            h.Vm.TickControlAlerts(FightStart + MainViewModel.StunLongest);
            Assert.DoesNotContain(h.Pipeline.Triggers.Snapshot.Timers, t => t.Text == "MYRIAD STUNNED!");

            Assert.Equal(AlertOverlay.Debuffs, h.Saved().StunAlertOverlay);
        }
    }

    [Fact]
    public void Losing_control_names_the_spell_cast_that_second_and_lasts_until_control_comes_back()
    {
        (Harness h, List<string> spoken) = BoxHarness();
        using (h)
        {
            StartFight(h, "Criare Sunmane");
            Line(h, "Myriad", "Criare Sunmane begins casting Phoenix Charm.", 1);
            Line(h, "Myriad", "You lose control of yourself!", 1);

            Assert.Contains("Myriad lost control", spoken);
            Assert.Equal("MYRIAD LOST CONTROL! (Phoenix Charm)", Assert.Single(h.Vm.FloatingBanners(FightStart.AddSeconds(60))).Text);

            Line(h, "Myriad", "You have control of yourself again.", 5);
            Assert.Empty(h.Vm.FloatingBanners(FightStart.AddSeconds(5)));

            LostControlSnapshot lost = Assert.Single(h.Pipeline.LiveDps.Snapshot.Active.Single().LostControl);
            Assert.Equal(new LostControlSnapshot("Myriad", FightStart.AddSeconds(1), FightStart.AddSeconds(5), "Phoenix Charm", "Criare Sunmane"), lost);

            h.Vm.LostControlAlert = false;
            Line(h, "Myriad", "You lose control of yourself!", 6);
            Assert.Empty(h.Vm.FloatingBanners(FightStart.AddSeconds(6)));
        }
    }

    [Fact]
    public void Running_out_of_food_is_spoken_and_running_low_is_only_shown_once_every_five_minutes()
    {
        (Harness h, List<string> spoken) = BoxHarness();
        using (h)
        {
            Line(h, "Plethora", "You are low on food and drink.", 0);
            Line(h, "Plethora", "You are low on food and drink.", 1);
            Assert.Empty(spoken);
            Assert.Single(h.Pipeline.Triggers.Snapshot.Timers, t => t.Text == "Plethora low on food and drink");

            Line(h, "Plethora", "You are out of drink and low on food.", 2);
            Assert.Equal(new[] { "Plethora out of drink and low on food" }, spoken);

            Line(h, "Ugli", "You are out of food and drink.", 3); // not one of your characters
            Assert.Single(spoken);
        }
    }

    [Fact]
    public void The_lost_control_list_shows_each_stretch_and_the_total_per_character()
    {
        var start = new DateTime(2026, 11, 22, 16, 54, 0);
        var fight = new EncounterSnapshot(1, "Criare Sunmane", start, start.AddMinutes(9), 540, 1000, false, Array.Empty<ParticipantSnapshot>())
        {
            LostControl = new ValueList<LostControlSnapshot>(new[]
            {
                new LostControlSnapshot("Myriad", start.AddSeconds(29), start.AddSeconds(153), "Phoenix Charm", "Criare Sunmane"),
                new LostControlSnapshot("Myriad", start.AddSeconds(500), null, string.Empty, string.Empty),
            }),
        };

        (IReadOnlyList<LostControlRowView> rows, string summary) = MainViewModel.LostControlView(fight, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(new LostControlRowView("16:54:29", "Myriad", "Phoenix Charm", "Criare Sunmane", "2:04"), rows[0]);
        Assert.Equal("0:40+", rows[1].Lasted); // still going when the fight ended
        Assert.Equal("Myriad lost control 2 times for 2:44 in total.", summary);
        Assert.Equal("None of your characters lost control of themselves in this fight.",
            MainViewModel.LostControlView(fight with { LostControl = new ValueList<LostControlSnapshot>(Array.Empty<LostControlSnapshot>()) }, System.Globalization.CultureInfo.InvariantCulture).Summary);
    }
}
