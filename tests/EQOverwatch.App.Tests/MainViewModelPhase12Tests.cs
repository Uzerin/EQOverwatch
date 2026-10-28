using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    private static CombatEvent RosterHit(string source, string target, int amount, int second) =>
        new(new DateTime(2026, 10, 21, 11, 45, second), CombatEventKind.Damage, source, target, amount, DamageKind.Melee, "hit");

    [Fact]
    public void The_saved_roster_is_restored_and_listed()
    {
        using var h = new Harness((settings, _) => settings.Roster = new RosterSettings
        {
            Players = { new RosterPlayerEntry { Name = "Oodles", Class = "Magician" } },
            Pets = { new RosterPetEntry { Pet = "Gabnik", Owner = "Oodles" } },
        });

        Assert.Equal("MAG", h.Pipeline.Roster.GetClassLabel("Oodles"));
        RosterPlayerRow player = Assert.Single(h.Vm.RosterPlayers);
        Assert.Equal(("Oodles", "Magician", "Gabnik"), (player.Name, player.Class, player.Pets));
        Assert.Equal("Oodles", Assert.Single(h.Vm.RosterPets).Owner);
        Assert.Equal("1 verified player(s), 1 pet(s).", h.Vm.RosterSummary);
    }

    [Fact]
    public void Something_learned_while_running_is_listed_and_saved()
    {
        using var h = new Harness();

        h.Pipeline.Roster.Observe("Gabnik says, 'My leader is Oodles.'");
        h.Pipeline.Roster.Observe("[65 Archmage (Magician)] Oodles (Gnome)");
        h.Vm.Refresh();

        Assert.Equal("Magician", Assert.Single(h.Vm.RosterPlayers).Class);
        Assert.Single(h.Vm.RosterPets);

        RosterSettings saved = h.Saved().Roster;
        Assert.Equal("Magician", Assert.Single(saved.Players).Class);
        Assert.Equal("Gabnik", Assert.Single(saved.Pets).Pet);
    }

    [Fact]
    public void The_participant_grid_shows_each_attackers_class()
    {
        using var h = new Harness();
        h.Pipeline.Roster.Observe("[60 Warrior] Stackprime (Human)");
        h.Pipeline.Roster.LearnPet("Gabnik", "Oodles");
        h.Pipeline.Roster.MergePetDamage = false;
        h.Pipeline.LiveDps.AddKnownPlayer("Stackprime");
        h.Pipeline.LiveDps.AddKnownPlayer("Oodles");
        h.Pipeline.LiveDps.OnCombatEvent(RosterHit("Stackprime", "a frost giant", 1000, 1));
        h.Pipeline.LiveDps.OnCombatEvent(RosterHit("Gabnik", "a frost giant", 500, 2));
        h.Pipeline.LiveDps.OnCombatEvent(RosterHit("Oodles", "a frost giant", 100, 3));

        h.Vm.Refresh();

        Assert.Equal(new[] { "WAR", "Pet", string.Empty }, h.Vm.Participants.Select(p => p.Class));
    }

    [Fact]
    public void A_class_learned_after_the_fight_appears_on_the_row()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.AddKnownPlayer("Stackprime");
        h.Pipeline.LiveDps.OnCombatEvent(RosterHit("Stackprime", "a frost giant", 1000, 1));
        h.Vm.Refresh();
        Assert.Equal(string.Empty, h.Vm.Participants[0].Class);

        h.Pipeline.Roster.Observe("[60 Paladin] Stackprime (Human)");
        h.Vm.Refresh();

        Assert.Equal("PAL", h.Vm.Participants[0].Class);
    }

    [Fact]
    public void Forgetting_a_player_removes_them_and_their_pets_from_the_lists_and_the_file()
    {
        using var h = new Harness();
        h.Pipeline.Roster.LearnPet("Gabnik", "Oodles");
        h.Vm.Refresh();

        h.Vm.ForgetPlayerCommand.Execute(Assert.Single(h.Vm.RosterPlayers));

        Assert.Empty(h.Vm.RosterPlayers);
        Assert.Empty(h.Vm.RosterPets);
        RosterSettings saved = h.Saved().Roster;
        Assert.Empty(saved.Players);
        Assert.Empty(saved.Pets);
    }

    [Fact]
    public void The_pet_damage_switch_reaches_the_roster_and_is_saved()
    {
        using var h = new Harness();
        Assert.True(h.Pipeline.Roster.MergePetDamage);

        h.Vm.MergePetDamage = false;

        Assert.False(h.Pipeline.Roster.MergePetDamage);
        Assert.False(h.Saved().Roster.MergePetDamage);
    }

    [Fact]
    public void The_dps_overlay_rows_carry_the_class()
    {
        using var h = new Harness();
        h.Pipeline.Roster.Observe("[60 Warrior] Stackprime (Human)");
        h.Pipeline.LiveDps.AddKnownPlayer("Stackprime");
        h.Pipeline.LiveDps.OnCombatEvent(RosterHit("Stackprime", "a frost giant", 1000, 1));
        var overlay = new DpsOverlayViewModel(() => h.Pipeline.LiveDps.Snapshot, h.Pipeline.Roster.GetClassLabel);

        overlay.Tick();

        Assert.Equal("WAR", Assert.Single(overlay.Rows).ClassText);
        Assert.Equal("WAR", overlay.DisplayedEncounter!.Participants[0].Class);
    }
}
