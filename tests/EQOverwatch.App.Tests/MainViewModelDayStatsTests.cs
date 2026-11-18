using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Session;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_new_alert_switches_reach_the_pipeline_and_are_kept()
    {
        AppSettings saved;
        using (var h = new Harness())
        {
            Assert.True(h.Vm.TakeHoldAlert && h.Vm.ResistStreakAlert && h.Vm.FeignDeathAlert && h.Vm.RootMezAlert && h.Vm.QuietLogAlert);
            h.Vm.TakeHoldAlert = false;
            h.Vm.RootMezAlert = false;
            h.Vm.QuietLogAlert = false;
            Assert.False(h.Pipeline.SpellAlerts.TakeHoldAlert);
            Assert.False(h.Pipeline.SpellAlerts.RootMezAlert);
            Assert.True(h.Pipeline.SpellAlerts.ResistAlert);
            saved = h.Saved();
        }

        Assert.False(saved.TakeHoldAlert);
        Assert.True(saved.ResistStreakAlert);
        Assert.True(saved.FeignDeathAlert);
        Assert.False(saved.RootMezAlert);
        Assert.False(saved.QuietLogAlert);

        using var again = new Harness((s, _) =>
        {
            s.TakeHoldAlert = false;
            s.FeignDeathAlert = false;
        });
        Assert.False(again.Vm.TakeHoldAlert);
        Assert.False(again.Pipeline.SpellAlerts.TakeHoldAlert);
        Assert.False(again.Pipeline.SpellAlerts.FeignDeathAlert);
        Assert.True(again.Pipeline.SpellAlerts.RootMezAlert);
    }

    [Fact]
    public void The_session_and_spell_stats_tabs_show_todays_numbers_and_filter_by_character()
    {
        using var h = new Harness();
        var day = new DateTime(2026, 11, 18);
        var zones = new[] { new ZoneStretch("Sleeper's Tomb", day.AddHours(19), day.AddHours(21).AddMinutes(5)) };
        h.Vm.ShowDayStats(
            new[] { new SessionSummary("Stackprime", 3 * 3600 + 12 * 60, 18.4, 212_500, 46, 3, zones) },
            new[]
            {
                new SpellDayRow("Ream", "Complete Heal", 40, 1, 2, 0, string.Empty),
                new SpellDayRow("Myriad", "Lure of Ice", 10, 0, 0, 0, string.Empty),
            },
            new[] { new ResistDayRow("Myriad", "Lure of Ice", "a deepspore mushroom", 3, 1) });

        SessionRowView row = Assert.Single(h.Vm.SessionRows);
        Assert.Equal("3h 12m", row.Played);
        Assert.Equal(212.5.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), row.Platinum);
        Assert.Same(row, h.Vm.SelectedSessionRow);
        ZoneRowView zone = Assert.Single(h.Vm.ZoneRows);
        Assert.Equal(("Sleeper's Tomb", "19:00", "21:05", "2h 05m"), (zone.Zone, zone.From, zone.To, zone.Length));

        Assert.Equal(new[] { "All characters", "Myriad", "Ream" }, h.Vm.SpellStatCharacters);
        Assert.Equal(2, h.Vm.SpellStatRows.Count);
        h.Vm.SpellStatCharacter = "Myriad";
        Assert.Equal("Lure of Ice", Assert.Single(h.Vm.SpellStatRows).Spell);
        Assert.Equal("25%", Assert.Single(h.Vm.ResistStatRows).ResistPercent);
        h.Vm.SpellStatCharacter = "Ream";
        Assert.Empty(h.Vm.ResistStatRows);
    }

    [Fact]
    public void Pets_show_as_a_line_under_their_owner_and_rampage_targets_under_damage_taken()
    {
        var pets = new[] { new PetLine("Koal", 48_210, 71, 29, 2) };
        Assert.Equal($"↳ Koal (pet): {48_210:N0} damage · {71.0:0.0}% hit · died 2×", ParticipantRowViewModel.DescribePets(pets));

        var row = new ParticipantRowViewModel();
        row.Update(1, new ParticipantSnapshot("Oodles", 60_000, 50, 100, 100, 500, 100, 10, 0.9, Pets: new ValueList<PetLine>(pets)));
        Assert.True(row.HasPets);
        row.Update(1, new ParticipantSnapshot("Ream", 100, 1, 1, 1, 50, 2, 0, 1));
        Assert.False(row.HasPets);

        var fight = new EncounterSnapshot(1, "Trooper Mykeor", DateTime.Today, DateTime.Today.AddMinutes(1), 60, 1000, false,
            Array.Empty<ParticipantSnapshot>(),
            new[]
            {
                new DamageTakenSnapshot("Ream", 500, 50, 3, 207, Rampages: 2, RampageDamage: 207),
                new DamageTakenSnapshot("Teek", 500, 50, 3, 300, Rampages: 5, RampageDamage: 900),
                new DamageTakenSnapshot("Stackprime", 100, 10, 1, 100),
            });
        Assert.Equal($"\nRampage: Teek 5 ({900:N0}), Ream 2 ({207:N0})", MainViewModel.RampageSummaryText(fight));
    }
}
