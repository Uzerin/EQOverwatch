using EQCompanion.Core.Export;
using EQCompanion.Core.Models;
using EQCompanion.Core.Pipeline;
using EQCompanion.Core.Roster;
using EQCompanion.Core.Services;
using EQCompanion.Core.Settings;
using Xunit;
using static EQCompanion.Core.Tests.TestData;
using static EQCompanion.Core.Tests.TestHelpers;

namespace EQCompanion.Core.Tests;

public class RosterTests
{
    private const string Giant = "a frost giant";

    // ─────────────────────────── /who ───────────────────────────

    [Theory]
    [InlineData("[65 Archmage (Magician)] Oodles (Gnome) <Some Guild> ZONE: nro", "Oodles", "Magician")]
    [InlineData("[110 Overlord (Shadow Knight)] Dread (Ogre)", "Dread", "Shadow Knight")]
    [InlineData("[125 Grandmaster (Monk)] Kicker (Human) LFG", "Kicker", "Monk")]
    [InlineData("[60 Warrior] Ream (Human) <Guild>", "Ream", "Warrior")]
    [InlineData("[70 Warlord] Bash (Barbarian)", "Bash", "")]      // a title alone is not a class
    [InlineData("[ANONYMOUS] Stackprime  <Guild>", "Stackprime", "")]
    public void A_who_line_gives_the_player_and_the_class_when_it_is_shown(string message, string name, string className)
    {
        Assert.True(RosterLineParser.TryParse(message, out RosterObservation seen));
        Assert.Equal(RosterObservationKind.Player, seen.Kind);
        Assert.Equal(name, seen.Name);
        Assert.Equal(className, seen.Class);
    }

    [Theory]
    [InlineData("You slash a frost giant for 142 points of damage.")]
    [InlineData("Oodles tells the raid, 'hello there'")]
    [InlineData("[Mon Oct 26 11:45:01 2026] Oodles tells you, 'hi'")]
    [InlineData("[65 Archmage (Magician)] a frost giant (Giant)")]
    [InlineData("There are 25 players in Plane of Knowledge.")]
    public void Other_lines_are_ignored(string message)
    {
        Assert.False(RosterLineParser.TryParse(message, out _));
    }

    // ─────────────────────────── /pet leader ───────────────────────────

    [Theory]
    [InlineData("Gabnik says, 'My leader is Oodles.'", "Gabnik", "Oodles")]
    [InlineData("Gabnik says 'My leader is Oodles.'", "Gabnik", "Oodles")]
    [InlineData("Jarl`s warder says, 'I belong to Kicker'", "Jarl`s warder", "Kicker")]
    public void A_pets_leader_answer_gives_the_pet_and_its_owner(string message, string pet, string owner)
    {
        Assert.True(RosterLineParser.TryParse(message, out RosterObservation seen));
        Assert.Equal(RosterObservationKind.Pet, seen.Kind);
        Assert.Equal(pet, seen.Name);
        Assert.Equal(owner, seen.Owner);
    }

    [Theory]
    [InlineData("Oodles says, 'My leader is Oodles.'")]          // a pet cannot lead itself
    [InlineData("Gabnik says, 'My leader is a gnoll.'")]         // the owner must look like a player
    [InlineData("Gabnik says, 'Attacking a frost giant Master.'")]
    public void Anything_else_a_pet_or_npc_says_is_ignored(string message)
    {
        Assert.False(RosterLineParser.TryParse(message, out _));
    }

    // ─────────────────────────── the roster ───────────────────────────

    [Fact]
    public void A_pet_answer_verifies_the_owner_and_links_the_pet()
    {
        var roster = new PlayerRoster();

        Assert.True(roster.Observe("Gabnik says, 'My leader is Oodles.'"));

        Assert.True(roster.IsVerifiedPlayer("Oodles"));
        Assert.True(roster.IsPet("Gabnik"));
        Assert.True(roster.TryGetOwner("Gabnik", out string? owner));
        Assert.Equal("Oodles", owner);
        Assert.Equal("Pet", roster.GetClassLabel("Gabnik"));
        Assert.Equal(string.Empty, roster.GetClassLabel("Oodles")); // verified, class not known yet

        roster.Observe("[65 Archmage (Magician)] Oodles (Gnome)");
        Assert.Equal("MAG", roster.GetClassLabel("Oodles"));
        Assert.Equal("Magician", roster.GetClass("Oodles"));
    }

    [Fact]
    public void A_later_sighting_without_a_class_keeps_the_class_and_a_new_class_replaces_it()
    {
        var roster = new PlayerRoster();
        roster.Observe("[60 Warrior] Ream (Human)");
        roster.Observe("[ANONYMOUS] Ream");
        Assert.Equal("WAR", roster.GetClassLabel("Ream"));

        roster.Observe("[60 Cleric] Ream (Human)");
        Assert.Equal("CLR", roster.GetClassLabel("Ream"));
    }

    [Fact]
    public void A_proven_player_cannot_be_turned_into_a_pet_by_a_chat_line()
    {
        var roster = new PlayerRoster();
        roster.Observe("[60 Warrior] Ream (Human)");

        Assert.False(roster.Observe("Ream says, 'My leader is Oodles.'"));
        Assert.False(roster.IsPet("Ream"));
    }

    [Fact]
    public void Forgetting_a_player_forgets_their_pets()
    {
        var roster = new PlayerRoster();
        roster.LearnPet("Gabnik", "Oodles");
        roster.LearnPet("Jarbo", "Kicker");

        Assert.True(roster.ForgetPlayer("Oodles"));

        Assert.False(roster.IsVerifiedPlayer("Oodles"));
        Assert.False(roster.IsPet("Gabnik"));
        Assert.True(roster.IsPet("Jarbo"));
    }

    [Fact]
    public void Changed_fires_for_real_changes_only()
    {
        var roster = new PlayerRoster();
        int raised = 0;
        roster.Changed += () => raised++;

        roster.Observe("Gabnik says, 'My leader is Oodles.'");
        roster.Observe("Gabnik says, 'My leader is Oodles.'"); // nothing new
        roster.Observe("[ANONYMOUS] Oodles");                  // nothing new

        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_roster_round_trips_through_settings_and_drops_junk()
    {
        var roster = new PlayerRoster { MergePetDamage = false };
        roster.Observe("[65 Archmage (Magician)] Oodles (Gnome)");
        roster.LearnPet("Gabnik", "Oodles");

        RosterSettings saved = roster.ToSettings();
        var loaded = new PlayerRoster();
        loaded.Load(saved);

        Assert.False(loaded.MergePetDamage);
        Assert.Equal("MAG", loaded.GetClassLabel("Oodles"));
        Assert.True(loaded.TryGetOwner("Gabnik", out string? owner));
        Assert.Equal("Oodles", owner);

        RosterSettings dirty = PlayerRoster.Sanitize(new RosterSettings
        {
            Players =
            {
                new RosterPlayerEntry { Name = "a frost giant", Class = "Warrior" }, // not a player name
                new RosterPlayerEntry { Name = "Ream", Class = "Berserker" },
                new RosterPlayerEntry { Name = "Ream", Class = "Warrior" },          // duplicate: the class updates
                new RosterPlayerEntry { Name = "Bash", Class = "Grandmaster" },       // not a class: name only
            },
            Pets = { new RosterPetEntry { Pet = "Gabnik", Owner = "not a name" } },
        });

        Assert.Equal(new[] { "Bash", "Ream" }, dirty.Players.Select(p => p.Name));
        Assert.Equal("Warrior", dirty.Players.Single(p => p.Name == "Ream").Class);
        Assert.Equal(string.Empty, dirty.Players.Single(p => p.Name == "Bash").Class);
        Assert.Empty(dirty.Pets);
    }

    // ─────────────────────────── DPS ───────────────────────────

    [Fact]
    public void A_known_pets_damage_goes_on_the_owners_row()
    {
        var roster = new PlayerRoster();
        roster.LearnPet("Gabnik", "Oodles");
        var tracker = new LiveDpsTracker(roster: roster);

        tracker.OnCombatEvent(Hit("Gabnik", Giant, 100, "11:45:01"));
        tracker.OnCombatEvent(Hit("Oodles", Giant, 50, "11:45:02"));
        tracker.OnCombatEvent(Miss("Gabnik", Giant, "11:45:03"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        ParticipantSnapshot row = Assert.Single(fight.Participants);
        Assert.Equal("Oodles", row.Name);
        Assert.Equal(150, row.TotalDamage);
        Assert.Equal(2, row.Hits);
        Assert.Equal(1, row.Misses);
    }

    [Fact]
    public void With_merging_off_the_pet_has_its_own_row()
    {
        var roster = new PlayerRoster { MergePetDamage = false };
        roster.LearnPet("Gabnik", "Oodles");
        var tracker = new LiveDpsTracker(roster: roster);

        tracker.OnCombatEvent(Hit("Gabnik", Giant, 100, "11:45:01"));
        tracker.OnCombatEvent(Hit("Oodles", Giant, 50, "11:45:02"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal(new[] { "Gabnik", "Oodles" }, fight.Participants.Select(p => p.Name));
    }

    [Fact]
    public void A_pet_that_attacks_a_single_word_boss_still_counts_and_the_boss_stays_an_npc()
    {
        var roster = new PlayerRoster();
        roster.LearnPet("Gabnik", "Oodles");
        var tracker = new LiveDpsTracker(roster: roster);

        tracker.OnCombatEvent(Hit("Gabnik", "Tormax", 300, "11:45:01"));
        tracker.OnCombatEvent(Hit("Tormax", "Gabnik", 900, "11:45:02")); // the boss hitting the pet is not damage dealt

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal("Tormax", fight.TargetName);
        Assert.Equal(300, fight.TotalDamage);
        Assert.Equal("Oodles", Assert.Single(fight.Participants).Name);
    }

    [Fact]
    public async Task The_tracker_learns_from_who_and_pet_lines_in_a_log_it_reads()
    {
        var roster = new PlayerRoster();
        var tracker = new LiveDpsTracker(roster: roster);

        await RunLinesAsync(new[]
        {
            Line("[65 Archmage (Magician)] Oodles (Gnome) <Guild>", "11:44:50"),
            Line("Gabnik says, 'My leader is Oodles.'", "11:44:55"),
            Line("Gabnik hit a frost giant for 100 points of damage.", "11:45:01"),
            Line("Oodles hit a frost giant for 50 points of non-melee damage.", "11:45:02"),
        }, Self, LogRoute.Dps, dpsSink: tracker);

        Assert.Equal("MAG", roster.GetClassLabel("Oodles"));
        Assert.True(roster.IsPet("Gabnik"));
        ParticipantSnapshot row = Assert.Single(Assert.Single(tracker.Snapshot.All).Participants);
        Assert.Equal("Oodles", row.Name);
        Assert.Equal(150, row.TotalDamage);
    }

    [Fact]
    public async Task The_roster_also_reads_lines_from_every_trigger_log()
    {
        var roster = new PlayerRoster();

        await RunLinesAsync(new[] { Line("Gabnik says, 'My leader is Oodles.'") }, Self, LogRoute.Triggers, triggerSink: roster);

        Assert.True(roster.IsPet("Gabnik"));
    }

    // ─────────────────────────── class column ───────────────────────────

    [Fact]
    public void Classes_are_added_when_a_fight_is_shown_and_reach_the_picture_rows()
    {
        var roster = new PlayerRoster();
        roster.Observe("[60 Warrior] Stackprime (Human)");
        roster.LearnPet("Gabnik", "Oodles");
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Stackprime");
        tracker.AddKnownPlayer("Oodles");
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 200, "11:45:01"));
        tracker.OnCombatEvent(Hit("Oodles", Giant, 100, "11:45:02"));
        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.All(fight.Participants, p => Assert.Equal(string.Empty, p.Class));

        EncounterSnapshot labelled = fight.WithClasses(roster.GetClassLabel);

        Assert.Equal(new[] { "WAR", string.Empty }, labelled.Participants.Select(p => p.Class));
        Assert.Equal(new[] { "WAR", string.Empty }, EncounterExport.BuildRows(labelled).Select(r => r.Class));
        Assert.Equal("Class", EncounterExport.ColumnHeaders[2]);
    }

    [Fact]
    public void Eq_class_names_and_labels()
    {
        Assert.True(EqClass.TryNormalize("shadowknight", out string name));
        Assert.Equal("Shadow Knight", name);
        Assert.Equal("SHD", EqClass.Abbreviate("Shadow Knight"));
        Assert.Equal("BER", EqClass.Abbreviate("berserker"));
        Assert.False(EqClass.TryNormalize("Grandmaster", out _));
        Assert.Equal(16, EqClass.Names.Count);
        Assert.Equal(16, EqClass.Names.Select(EqClass.Abbreviate).Distinct().Count());
    }
}
