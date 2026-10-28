using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using Xunit;
using static EQOverwatch.Core.Tests.TestData;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public class DpsTrackerServiceTests
{
    private const string Giant = "a frost giant";

    [Fact]
    public async Task Sample_log_yields_one_encounter_with_the_expected_breakdown()
    {
        var tracker = new LiveDpsTracker();
        await RunLinesAsync(SampleLog, Self, LogRoute.Dps, dpsSink: tracker);

        DpsSnapshot snap = tracker.Snapshot;
        Assert.Empty(snap.Active);

        // The death line arrives 12s after the last hit, which is past the 8s timeout, so the encounter
        // ends at its last activity (11:45:03) rather than at the death.
        EncounterSnapshot enc = Assert.Single(snap.Completed);
        Assert.Equal(Giant, enc.TargetName);
        Assert.Equal(At("11:45:01"), enc.StartTime);
        Assert.Equal(At("11:45:03"), enc.EndTime);
        Assert.Equal(2.0, enc.DurationSeconds);
        Assert.Equal(802, enc.TotalDamage);
        Assert.False(enc.IsActive);

        Assert.Equal(new[] { "Oodles", "Stackprime" }, enc.Participants.Select(p => p.Name)); // ordered by damage

        ParticipantSnapshot oodles = enc.Participants[0];
        Assert.Equal(450, oodles.TotalDamage);
        Assert.Equal(100.0 * 450 / 802, oodles.PercentOfGroupDamage, 3);
        Assert.Equal(450.0, oodles.Dps, 3);        // single action => 1s minimum window
        Assert.Equal(225.0, oodles.ScaledDps, 3);  // 450 over the 2s encounter

        ParticipantSnapshot self = enc.Participants[1];
        Assert.Equal(352, self.TotalDamage);       // "You slash" 142 + "Stackprime crushes" 210
        Assert.Equal(2, self.Hits);
        Assert.Equal(210, self.MaxHit);
        Assert.Equal(352.0, self.Dps, 3);          // own window 11:45:01..02 = 1s
        Assert.Equal(176.0, self.ScaledDps, 3);    // 352 over the 2s encounter
    }

    [Fact]
    public void Target_death_closes_the_encounter_at_the_death_time()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        tracker.OnCombatEvent(Death(Giant, "Stackprime", "11:45:02"));

        EncounterSnapshot enc = Assert.Single(tracker.Snapshot.Completed);
        Assert.Equal(At("11:45:02"), enc.EndTime);
        Assert.Equal(1.0, enc.DurationSeconds);
        Assert.Empty(tracker.Snapshot.Active);
    }

    [Fact]
    public void Inactivity_timeout_is_strictly_greater_than_eight_log_seconds()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:09")); // exactly 8s later: same encounter
        Assert.Empty(tracker.Snapshot.Completed);
        Assert.Equal(2, Assert.Single(tracker.Snapshot.Active).Participants[0].Hits);

        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:18")); // 9s later: new encounter

        EncounterSnapshot first = Assert.Single(tracker.Snapshot.Completed);
        Assert.Equal(At("11:45:01"), first.StartTime);
        Assert.Equal(At("11:45:09"), first.EndTime);
        Assert.Equal(8.0, first.DurationSeconds);

        EncounterSnapshot second = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal(At("11:45:18"), second.StartTime);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Non_combat_log_lines_advance_the_clock_and_expire_encounters()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));

        tracker.OnLogClock(At("11:45:09"));
        Assert.Single(tracker.Snapshot.Active);

        tracker.OnLogClock(At("11:45:10"));
        Assert.Empty(tracker.Snapshot.Active);
        EncounterSnapshot enc = Assert.Single(tracker.Snapshot.Completed);
        Assert.Equal(At("11:45:01"), enc.EndTime); // closed at last activity, not at the tick
    }

    [Fact]
    public void Times_come_from_the_log_never_from_wall_clock()
    {
        var tracker = new LiveDpsTracker();
        var old = new DateTime(2001, 2, 3, 4, 5, 6);
        tracker.OnCombatEvent(new CombatEvent(old, CombatEventKind.Damage, "Stackprime", Giant, 10, DamageKind.Melee, null));

        EncounterSnapshot enc = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal(old, enc.StartTime);
        Assert.Equal(old, enc.EndTime);
    }

    [Fact]
    public void Live_feed_and_batch_import_produce_identical_encounters()
    {
        CombatEvent[] events =
        {
            Hit("Stackprime", Giant, 100, "11:45:01"),
            Hit("Oodles", Giant, 200, "11:45:03"),
            Hit("Stackprime", "a gnoll", 50, "11:45:04"),
            Miss("Stackprime", Giant, "11:45:05"),
            Death(Giant, "Stackprime", "11:45:06"),
            Hit("Stackprime", "a gnoll", 70, "11:45:20"),
        };

        var live = new LiveDpsTracker();
        foreach (CombatEvent e in events)
        {
            live.OnCombatEvent(e);
        }

        live.OnLogClock(At("11:46:59")); // let the last encounter time out

        var historical = new HistoricalDpsTracker();
        historical.BeginBatch();
        foreach (CombatEvent e in events)
        {
            historical.OnCombatEvent(e);
        }

        historical.EndBatch();

        Assert.Equal(3, live.Snapshot.Completed.Count);
        Assert.Equal(Signature(live.Snapshot), Signature(historical.Snapshot));
    }

    [Fact]
    public void Batches_suppress_publishing_until_EndBatch_which_also_closes_open_encounters()
    {
        var tracker = new HistoricalDpsTracker();
        tracker.BeginBatch();
        long versionAtStart = tracker.Snapshot.Version;

        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:02"));
        Assert.Equal(versionAtStart, tracker.Snapshot.Version);
        Assert.Empty(tracker.Snapshot.Active);

        tracker.EndBatch();

        Assert.True(tracker.Snapshot.Version > versionAtStart);
        Assert.Empty(tracker.Snapshot.Active);
        Assert.Equal(200, Assert.Single(tracker.Snapshot.Completed).TotalDamage);
    }

    [Fact]
    public void Historical_tracker_replaces_the_previous_import_but_live_tracker_does_not_reset()
    {
        var historical = new HistoricalDpsTracker();
        historical.BeginBatch();
        historical.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        historical.EndBatch();
        Assert.Single(historical.Snapshot.Completed);

        historical.BeginBatch();
        Assert.Empty(historical.Snapshot.Completed); // cleared at the start of the next import
        historical.EndBatch();

        var live = new LiveDpsTracker();
        live.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        live.BeginBatch();
        live.EndBatch();
        Assert.Single(live.Snapshot.Completed);

        live.Reset();
        Assert.Empty(live.Snapshot.Completed);
        Assert.Empty(live.Snapshot.Active);
    }

    [Fact]
    public void Damage_taken_by_players_and_player_vs_player_damage_are_ignored()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit(Giant, "Stackprime", 500, "11:45:01"));     // NPC hits a player
        tracker.OnCombatEvent(Hit("Oodles", "Stackprime", 50, "11:45:02"));   // duel
        tracker.OnCombatEvent(Miss(Giant, "Stackprime", "11:45:03"));         // NPC misses a player

        Assert.Empty(tracker.Snapshot.Active);
        Assert.Empty(tracker.Snapshot.Completed);
    }

    [Fact]
    public void Misses_feed_the_hit_rate()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        tracker.OnCombatEvent(Miss("Stackprime", Giant, "11:45:02"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:03"));

        ParticipantSnapshot p = Assert.Single(Assert.Single(tracker.Snapshot.Active).Participants);
        Assert.Equal(2, p.Hits);
        Assert.Equal(1, p.Misses);
        Assert.Equal(2.0 / 3.0, p.HitRate, 3);
    }

    [Fact]
    public void Concurrent_targets_have_independent_encounters_and_death_closes_only_its_own()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        tracker.OnCombatEvent(Hit("Stackprime", "a gnoll", 40, "11:45:02"));
        tracker.OnCombatEvent(Death("a gnoll", "Stackprime", "11:45:03"));

        Assert.Equal("a gnoll", Assert.Single(tracker.Snapshot.Completed).TargetName);
        Assert.Equal(Giant, Assert.Single(tracker.Snapshot.Active).TargetName);
    }

    [Fact]
    public void Published_snapshots_never_change_after_publication()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        DpsSnapshot before = tracker.Snapshot;
        EncounterSnapshot encounterBefore = Assert.Single(before.Active);

        tracker.OnCombatEvent(Hit("Stackprime", Giant, 900, "11:45:02"));

        Assert.Equal(100, encounterBefore.TotalDamage);
        Assert.Equal(100, encounterBefore.Participants[0].TotalDamage);
        Assert.Equal(1000, Assert.Single(tracker.Snapshot.Active).TotalDamage);
        Assert.True(tracker.Snapshot.Version > before.Version);
    }

    [Fact]
    public void Completed_history_is_capped_oldest_first()
    {
        var tracker = new LiveDpsTracker(maxCompletedEncounters: 2);
        foreach (string time in new[] { "11:45:01", "11:45:20", "11:45:40", "11:45:59" })
        {
            tracker.OnCombatEvent(Hit("Stackprime", Giant, 10, time));
        }

        Assert.Equal(new[] { 2, 3 }, tracker.Snapshot.Completed.Select(e => e.Id));
        Assert.Equal(4, Assert.Single(tracker.Snapshot.Active).Id);
    }

    [Fact]
    public async Task Snapshots_can_be_read_while_events_are_being_processed()
    {
        var tracker = new LiveDpsTracker();
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 20_000; i++)
            {
                tracker.OnCombatEvent(Hit("Stackprime", Giant, 1 + i % 50, "11:45:01"));
            }
        });

        long lastVersion = 0;
        while (!writer.IsCompleted)
        {
            DpsSnapshot snap = tracker.Snapshot;
            Assert.True(snap.Version >= lastVersion);
            lastVersion = snap.Version;
            foreach (EncounterSnapshot enc in snap.Active)
            {
                Assert.Equal(enc.Participants.Sum(p => p.TotalDamage), enc.TotalDamage); // internally consistent
            }
        }

        await writer;
    }

    [Fact]
    public async Task Single_word_bosses_are_npcs_once_a_confirmed_player_fights_them()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnLogOwner(Self); // seeds Stackprime as a confirmed player

        await RunLinesAsync(
            new[]
            {
                Line("Stackprime slashes Tormax for 210 points of damage.", "11:45:01"),
                Line("Tormax slashes Stackprime for 350 points of damage.", "11:45:02"),
            },
            Self, LogRoute.Dps, dpsSink: tracker);

        EncounterSnapshot enc = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal("Tormax", enc.TargetName);
        ParticipantSnapshot only = Assert.Single(enc.Participants);
        Assert.Equal("Stackprime", only.Name);
        Assert.Equal(210, only.TotalDamage);   // Tormax's 350 against Stackprime was not counted
        Assert.Empty(tracker.Snapshot.Completed);
    }

    [Fact]
    public void An_unknown_single_word_attacker_of_a_confirmed_npc_becomes_a_player()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Stackprime");

        tracker.OnCombatEvent(Hit("Stackprime", "Tormax", 100, "11:45:01")); // Tormax -> confirmed NPC
        tracker.OnCombatEvent(Hit("Oodles", "Tormax", 200, "11:45:02"));     // Oodles hits a confirmed NPC -> player

        EncounterSnapshot enc = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal(new[] { "Oodles", "Stackprime" }, enc.Participants.Select(p => p.Name));
        Assert.Equal(300, enc.TotalDamage);
    }

    [Fact]
    public void Multi_word_and_lowercase_names_are_always_npcs_and_unknown_pairs_are_ignored()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Oodles", "Stackprime", 100, "11:45:01"));          // two unknown single words
        tracker.OnCombatEvent(Hit("Lord Nagafen", "Oodles", 100, "11:45:02"));         // multi-word NPC attacking
        Assert.Empty(tracker.Snapshot.Active);

        tracker.OnCombatEvent(Hit("Oodles", "Lord Nagafen", 100, "11:45:03"));         // multi-word target is an NPC
        Assert.Equal("Lord Nagafen", Assert.Single(tracker.Snapshot.Active).TargetName);
    }

    [Fact]
    public void Known_players_are_never_reclassified_as_npcs()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Stackprime");
        tracker.AddKnownPlayer("Oodles");

        tracker.OnCombatEvent(Hit("Stackprime", "Oodles", 100, "11:45:01")); // duel between two known players

        Assert.Empty(tracker.Snapshot.Active);
        tracker.OnCombatEvent(Hit("Oodles", "Tormax", 100, "11:45:02"));
        Assert.Equal("Tormax", Assert.Single(tracker.Snapshot.Active).TargetName);
    }

    [Fact]
    public void Reset_forgets_learned_names_but_keeps_seeded_players()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Stackprime");
        tracker.OnCombatEvent(Hit("Stackprime", "Tormax", 100, "11:45:01"));
        tracker.OnCombatEvent(Hit("Oodles", "Tormax", 100, "11:45:02")); // learns Oodles as a player

        tracker.Reset();

        tracker.OnCombatEvent(Hit("Oodles", "Tormax", 100, "11:46:01")); // Oodles and Tormax are unknown again
        Assert.Empty(tracker.Snapshot.Active);
        tracker.OnCombatEvent(Hit("Stackprime", "Tormax", 100, "11:46:02")); // seed survived
        Assert.Single(tracker.Snapshot.Active);
    }

    [Fact]
    public void The_inactivity_timeout_can_be_changed_at_runtime()
    {
        var tracker = new LiveDpsTracker();
        tracker.InactivityTimeout = TimeSpan.FromSeconds(15);
        Assert.Equal(TimeSpan.FromSeconds(15), tracker.InactivityTimeout);

        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:01"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:16")); // 15s gap: still the same encounter
        Assert.Empty(tracker.Snapshot.Completed);

        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:32")); // 16s gap: splits
        Assert.Single(tracker.Snapshot.Completed);

        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.InactivityTimeout = TimeSpan.Zero);
    }

    private static string Signature(DpsSnapshot snap) => string.Join(
        "|",
        snap.Completed.Select(e =>
            $"{e.TargetName}@{e.StartTime:O}-{e.EndTime:O}:{e.TotalDamage}:" +
            string.Join(",", e.Participants.Select(p => $"{p.Name}={p.TotalDamage}/{p.Hits}/{p.Misses}"))));
}
