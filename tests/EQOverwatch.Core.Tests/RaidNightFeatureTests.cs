using System.Globalization;
using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Loot;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public class RaidNightFeatureTests
{
    private const string Giant = "a frost giant";
    private const string Boss = "Lord Inquisitor Seru";

    private static string Stamp(DateTime t) => t.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture);

    private static string Line(string message) => $"[Wed Oct 21 11:45:01 2026] {message}";

    // ───────────────────────── parser ─────────────────────────

    [Fact]
    public void Swings_at_an_invulnerable_boss_are_not_misses()
    {
        string line = Line($"Regnier tries to slash {Boss}, but {Boss} is INVULNERABLE!");
        Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
        Assert.False(CombatLineParser.TryParse(line, ts, "Ream", out _));
    }

    [Fact]
    public void A_thorns_hit_is_damage_from_the_thorns_owner()
    {
        string line = Line("Vyzh`dra the Cursed is pierced by Fullfrontal's thorns for 34 points of non-melee damage.");
        Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
        Assert.True(CombatLineParser.TryParse(line, ts, "Ream", out CombatEvent e));
        Assert.Equal("Fullfrontal", e.Source);
        Assert.Equal("Vyzh`dra the Cursed", e.Target);
        Assert.Equal(34, e.Amount);
        Assert.Equal(DamageKind.NonMelee, e.DamageKind);
    }

    // ───────────────────────── damage taken from a corpse ─────────────────────────

    [Fact]
    public void Damage_over_time_ticking_from_the_bosss_corpse_is_damage_taken()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Boss, 100, "11:45:01"));
        tracker.OnCombatEvent(new CombatEvent(At("11:45:02"), CombatEventKind.Damage, $"{Boss}'s corpse", "Stackprime", 345, DamageKind.DamageOverTime, "Torturing Winds"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal(345, fight.TotalDamageTaken);
    }

    // ───────────────────────── boss casts ─────────────────────────

    [Fact]
    public void Boss_casts_in_either_wording_are_recorded_in_the_fight()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        tracker.OnLogClock(At("11:45:03"));
        tracker.OnNonCombatMessage($"{Giant} begins casting Frost Breath.");
        tracker.OnLogClock(At("11:45:08"));
        tracker.OnNonCombatMessage($"{Giant} begins to cast a spell. <Frost Breath>");
        tracker.OnLogClock(At("11:45:09"));
        tracker.OnNonCombatMessage("Trick begins casting Complete Heal."); // a player: not a boss ability

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal(new[] { "Frost Breath", "Frost Breath" }, fight.CastRows.Select(c => c.Spell));

        BossAbilityRow row = Assert.Single(BossAbilityTimeline.Build(fight));
        Assert.Equal(2, row.Casts);
        Assert.Equal("0:02, 0:07", row.TimesText);
        Assert.Equal("every 5s", row.EveryText);
    }

    [Fact]
    public void A_cast_just_before_the_pull_still_belongs_to_the_fight()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnLogClock(At("11:45:00"));
        tracker.OnNonCombatMessage($"{Boss} begins casting Torturing Winds.");
        tracker.OnCombatEvent(Hit("Stackprime", Boss, 100, "11:45:02"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal("Torturing Winds", Assert.Single(fight.CastRows).Spell);
    }

    // ───────────────────────── healing ─────────────────────────

    [Fact]
    public void Heals_during_a_fight_are_split_into_effective_healing_and_overheal()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        tracker.OnLogClock(At("11:45:02"));
        tracker.OnNonCombatMessage("Leinhart healed Regnier for 328 (8415) hit points by Complete Heal.");
        tracker.OnNonCombatMessage("Leinhart healed Regnier for 0 (8415) hit points by Complete Heal.");
        tracker.OnNonCombatMessage("Elias healed Regnier over time for 100 hit points by Celestial Elixir.");
        tracker.OnNonCombatMessage("Regnier is completely healed."); // no numbers: ignored

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal(new[] { "Leinhart", "Elias" }, fight.HealingRows.Select(h => h.Name));

        HealingSnapshot leinhart = fight.HealingRows[0];
        Assert.Equal(328, leinhart.Effective);
        Assert.Equal(8087 + 8415, leinhart.Overheal);
        Assert.Equal(2, leinhart.Heals);
        Assert.Equal(328, leinhart.MaxHeal);
        Assert.Equal("Complete Heal", leinhart.TopSpell);
        Assert.Equal(100.0 * (8087 + 8415) / (328 + 8087 + 8415), leinhart.OverhealPercent, 3);

        Assert.Equal(100, fight.HealingRows[1].Effective);
        Assert.Equal(0, fight.HealingRows[1].Overheal); // no "(possible)" number: nothing counted as overheal
    }

    [Fact]
    public void Heals_without_a_fight_are_ignored()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnLogClock(At("11:45:02"));
        tracker.OnNonCombatMessage("Leinhart healed Regnier for 328 (8415) hit points by Complete Heal.");
        Assert.Empty(tracker.Snapshot.Active);
    }

    // ───────────────────────── raid rule wording ─────────────────────────

    [Theory]
    [InlineData("begins to cast a spell. <Dragon Roar>", "begins casting Dragon Roar.")]
    [InlineData("begins casting Dragon Roar.", "begins to cast a spell. <Dragon Roar>")]
    public void A_casting_text_has_an_alternate_wording(string text, string expected) =>
        Assert.Equal(expected, RaidBossTimerRule.AlternateCastForm(text));

    [Fact]
    public void Other_match_text_has_no_alternate_wording()
    {
        Assert.Null(RaidBossTimerRule.AlternateCastForm("The ground shakes"));
        Assert.Null(RaidBossTimerRule.AlternateCastForm(string.Empty));
    }

    // ───────────────────────── recommended triggers ─────────────────────────

    private static EncounterSnapshot FightWithCasts(int id, params (int Seconds, string Spell)[] casts)
    {
        DateTime start = At("11:00:00");
        return new EncounterSnapshot(
            id, Boss, start, start.AddMinutes(5), 300, 1000, false, Array.Empty<ParticipantSnapshot>(),
            BossCasts: casts.Select(c => new BossCastSnapshot(start.AddSeconds(c.Seconds), c.Spell)).ToArray());
    }

    [Fact]
    public void A_spell_cast_at_a_steady_interval_is_suggested_with_its_recast_time()
    {
        var fights = new[]
        {
            FightWithCasts(1, (10, "Torturing Winds"), (40, "Torturing Winds"), (70, "Torturing Winds"), (100, "Torturing Winds"), (15, "Rare Smash")),
            FightWithCasts(2, (12, "Torturing Winds"), (43, "Torturing Winds"), (73, "Torturing Winds")),
        };

        RaidTriggerSuggestion suggestion = Assert.Single(RaidTriggerSuggester.Suggest(Boss, fights));
        Assert.Equal("Torturing Winds", suggestion.SpellName);
        Assert.Equal(30, suggestion.RecastSeconds);
        Assert.Equal("begins casting Torturing Winds.", suggestion.MatchText);
        Assert.Equal(7, suggestion.Casts);
        Assert.Equal(2, suggestion.Fights);
    }

    [Fact]
    public void A_spell_cast_at_uneven_gaps_is_not_suggested()
    {
        var fights = new[] { FightWithCasts(1, (10, "Chaos"), (14, "Chaos"), (60, "Chaos"), (70, "Chaos"), (140, "Chaos")) };
        Assert.Empty(RaidTriggerSuggester.Suggest(Boss, fights));
        Assert.Empty(RaidTriggerSuggester.Suggest("Someone Else", fights));
    }

    // ───────────────────────── loot ─────────────────────────

    private static readonly DateTime Now = new(2026, 10, 23, 20, 8, 49, DateTimeKind.Utc);

    [Fact]
    public void Each_looted_item_is_linked_to_the_roll_its_looter_won()
    {
        var loot = new LootTracker();
        loot.OnLine("Reamiiv", "Warshield rolled a 377 on Mnemonic of Khati Sha.", Now);
        loot.OnLine("Reamiiv", "Warshield won the need roll on 1 item(s): Mnemonic of Khati Sha with a roll of 377.", Now);
        loot.OnLine("Reamiiv", "Nasti won the need roll on 1 item(s): Mnemonic of Khati Sha with a roll of 387.", Now);
        loot.OnLine("Reamiiv", $"--Warshield has looted a Mnemonic of Khati Sha from {Boss}'s corpse.--", Now);
        loot.OnLine("Reamiiv", $"--Nasti has looted a Mnemonic of Khati Sha from {Boss}'s corpse.--", Now);
        loot.OnLine("Reamiiv", $"--Oodles has looted a Rusty Dagger from {Boss}'s corpse.--", Now);

        Assert.Equal(
            new[] { ("Warshield", "need", 377), ("Nasti", "need", 387), ("Oodles", "", 0) },
            loot.Entries.Select(e => (e.Looter, e.RollType, e.Roll)));
        Assert.All(loot.Entries, e => Assert.Equal(Boss, e.Mob));
        Assert.Equal("Mnemonic of Khati Sha", loot.Entries[0].Item);
    }

    [Fact]
    public void The_same_loot_line_from_a_second_log_is_recorded_once()
    {
        var loot = new LootTracker();
        string line = $"--Warshield has looted a Mnemonic of Khati Sha from {Boss}'s corpse.--";
        loot.OnLine("Reamiiv", line, Now);
        loot.OnLine("Ream", line, Now.AddSeconds(1)); // the other character saw it too
        Assert.Single(loot.Entries);

        loot.OnLine("Ream", line, Now.AddSeconds(2)); // the same log again is a second copy
        Assert.Equal(2, loot.Entries.Count);
    }

    [Fact]
    public void Your_own_loot_is_attributed_to_the_log_owner()
    {
        var loot = new LootTracker();
        loot.OnLine("Reamiiv", "--You have looted a Spell: Sanctuary from a gnoll's corpse.--", Now);
        Assert.Equal("Reamiiv", Assert.Single(loot.Entries).Looter);
    }

    [Fact]
    public void Loot_is_saved_and_loaded_again_and_a_scanned_log_adds_only_what_is_missing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-loot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string saved = Path.Combine(dir, "loot.json");
            var first = new LootTracker();
            first.SetStoragePath(saved);
            first.OnLine("Reamiiv", $"--Warshield has looted a Mnemonic of Khati Sha from {Boss}'s corpse.--", Now);

            var second = new LootTracker();
            second.SetStoragePath(saved);
            Assert.Single(second.Entries);

            string log = Path.Combine(dir, "eqlog_Reamiiv_frostreaver.txt");
            DateTime local = second.Entries[0].Time;
            File.WriteAllLines(log, new[]
            {
                $"[{Stamp(local)}] --Warshield has looted a Mnemonic of Khati Sha from {Boss}'s corpse.--", // already known
                $"[{Stamp(local.AddMinutes(5))}] Nasti won the greed roll on 1 item(s): Rusty Dagger with a roll of 12.",
                $"[{Stamp(local.AddMinutes(5))}] --Nasti has looted a Rusty Dagger from {Boss}'s corpse.--",
            });

            Assert.Equal(1, second.ScanLog(log));
            Assert.Equal(2, second.Entries.Count);
            LootEntry added = second.Entries[1];
            Assert.Equal(("Nasti", "greed", 12), (added.Looter, added.RollType, added.Roll));
            Assert.Equal(0, second.ScanLog(log)); // nothing new the second time
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ───────────────────────── attendance ─────────────────────────

    [Fact]
    public void A_raid_roster_file_name_gives_the_server_and_the_time()
    {
        Assert.True(RaidRosterStore.TryParseFileName("RaidRoster_frostreaver-20261029-180139", out string server, out DateTime taken));
        Assert.Equal("frostreaver", server);
        Assert.Equal(new DateTime(2026, 10, 29, 18, 1, 39), taken);
        Assert.True(RaidRosterStore.TryParseFileName("/games/everquest/RaidRoster_frostreaver-20261029-180139.txt", out _, out _));
        Assert.False(RaidRosterStore.TryParseFileName("Reamiiv_frostreaver-Inventory", out _, out _));
    }

    [Fact]
    public void Roster_lines_give_group_name_level_class_and_role()
    {
        List<RaidMember> members = RaidRosterStore.ParseMembers(new[]
        {
            "1\tStackprime\t60\tWarrior\tRaid Leader\t\t\t",
            "1\tRam\t60\tCleric\t\t\t\t",
            "2\tOodles\t60\tBard\tGroup Leader\t\t\t",
            "not a roster line",
            "2\tAbundancei\t60\tRanger",
        });

        Assert.Equal(new[] { "Stackprime", "Ram", "Oodles", "Abundancei" }, members.Select(m => m.Name));
        Assert.Equal(new RaidMember(1, "Stackprime", 60, "Warrior", "Raid Leader"), members[0]);
        Assert.Equal(string.Empty, members[1].Role);
        Assert.Equal(2, members[3].Group);
    }

    [Fact]
    public void Dumps_in_the_game_folder_are_read_once_and_kept()
    {
        string root = Path.Combine(Path.GetTempPath(), "eqo-raid-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "game");
        string saved = Path.Combine(root, "saved");
        Directory.CreateDirectory(game);
        try
        {
            File.WriteAllText(Path.Combine(game, "RaidRoster_frostreaver-20261029-180139"), "1\tStackprime\t60\tWarrior\tRaid Leader\n1\tReam\t60\tCleric\n");
            File.WriteAllText(Path.Combine(game, "RaidRoster_frostreaver-20261030-190000.txt"), "1\tStackprime\t60\tWarrior\tRaid Leader\n");
            File.WriteAllText(Path.Combine(game, "Unrelated.txt"), "1\tx\t1\ty");

            var store = new RaidRosterStore(saved);
            Assert.Equal(2, store.ScanAll(game));
            Assert.Equal(0, store.ScanAll(game));
            Assert.Equal(new DateTime(2026, 10, 30, 19, 0, 0), store.Records()[0].Taken); // newest first

            File.Delete(Path.Combine(game, "RaidRoster_frostreaver-20261029-180139"));
            Assert.Equal(2, new RaidRosterStore(saved).Records().Count); // still there from the saved copy

            IReadOnlyList<AttendanceTotal> totals = RaidRosterStore.Totals(store.Records());
            Assert.Equal(("Stackprime", 2, 2, 100.0), (totals[0].Name, totals[0].Present, totals[0].Raids, totals[0].Percent));
            Assert.Equal(("Ream", 1, 50.0), (totals[1].Name, totals[1].Present, totals[1].Percent));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ───────────────────────── character planner ─────────────────────────

    private static CharacterData Character(string name, string server, string[] missing, params (string Location, string Item, int Count)[] items) => new()
    {
        Name = name,
        Server = server,
        Missing = missing.Select((m, i) => new SpellListEntry(10 + i, m)).ToList(),
        Inventory = items.Select(i => new InventoryItem(i.Location, i.Item, 1, i.Count, 0)).ToList(),
    };

    [Fact]
    public void Items_are_found_on_every_character_and_in_the_shared_bank()
    {
        var chars = new[]
        {
            Character("Ream", "frostreaver", Array.Empty<string>(), ("General 1-Slot2", "Spell: Sanctuary", 1), ("General 1-Slot3", "Backpack", 1)),
            Character("Reamii", "frostreaver", Array.Empty<string>(), ("Bank3", "Spell: Sanctuary", 2)),
        };
        var shared = new[] { new SharedBankData { Server = "frostreaver", Items = new() { new InventoryItem("SharedBank1", "Sanctuary Pearl", 5, 1, 0) } } };

        IReadOnlyList<ItemHit> hits = CharacterPlanner.FindItems(chars, shared, "sanctuary");
        Assert.Equal(new[] { "Ream", "Reamii", "Shared bank (frostreaver)" }, hits.Select(h => h.Character));
        Assert.Empty(CharacterPlanner.FindItems(chars, shared, "  "));
    }

    [Fact]
    public void The_plan_lists_carried_scrolls_and_the_spells_nobody_has_a_scroll_for()
    {
        var chars = new[]
        {
            Character("Reamiiv", "frostreaver", new[] { "Sanctuary", "Remove Minor Curse" }),
            Character("Ream", "frostreaver", Array.Empty<string>(), ("General 2-Slot3", "Spell: Sanctuary", 1)),
            Character("Elsewhere", "othersrv", new[] { "Remove Minor Curse" }, ("Bank1", "Spell: Remove Minor Curse", 1)),
        };
        var shared = new[] { new SharedBankData { Server = "frostreaver", Items = new() { new InventoryItem("SharedBank4-Slot6", "Spell: Judgment", 2, 1, 0) } } };

        (IReadOnlyList<ScrollPlanRow> scrolls, IReadOnlyList<ShoppingRow> shopping) = CharacterPlanner.Plan(chars, shared, "frostreaver");

        ScrollPlanRow scroll = Assert.Single(scrolls);
        Assert.Equal("Sanctuary", scroll.Spell);
        Assert.Equal("Ream: General 2 · slot 3", scroll.HeldBy);
        Assert.Equal("Reamiiv", scroll.NeededBy);

        ShoppingRow buy = Assert.Single(shopping); // the scroll on the other server does not count
        Assert.Equal("Remove Minor Curse", buy.Spell);
        Assert.Equal("Reamiiv", buy.NeededBy);
    }
}
