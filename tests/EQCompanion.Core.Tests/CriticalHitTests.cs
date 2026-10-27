using EQCompanion.Core.Models;
using EQCompanion.Core.Parsing;
using EQCompanion.Core.Pipeline;
using EQCompanion.Core.Services;
using Xunit;
using static EQCompanion.Core.Tests.TestData;
using static EQCompanion.Core.Tests.TestHelpers;

namespace EQCompanion.Core.Tests;

public class CriticalHitTests
{
    private const string Giant = "a frost giant";

    private static CombatEvent ParseOk(string message)
    {
        string line = Line(message);
        Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
        Assert.True(CombatLineParser.TryParse(line, ts, Self, out CombatEvent ev), $"Expected a combat event for: {message}");
        return ev;
    }

    private static CombatEvent Crit(string attacker, int damage, string time, DamageKind kind = DamageKind.Melee) =>
        new(At(time), CombatEventKind.CriticalHit, attacker, string.Empty, damage, kind, null);

    private static CombatEvent SpellHit(string source, string target, int amount, string time) =>
        new(At(time), CombatEventKind.Damage, source, target, amount, DamageKind.NonMelee, null);

    private static CombatEvent DotTick(string source, string target, int amount, string time) =>
        new(At(time), CombatEventKind.Damage, source, target, amount, DamageKind.DamageOverTime, "Flame Lick");

    private static ParticipantSnapshot Only(LiveDpsTracker tracker) =>
        Assert.Single(Assert.Single(tracker.Snapshot.Active).Participants);

    // ───────────────────────── parsing ─────────────────────────

    [Fact]
    public void Melee_crit_announcements_are_parsed_for_others_and_self()
    {
        CombatEvent other = ParseOk("Ream scores a critical hit! (1234)");
        Assert.Equal(CombatEventKind.CriticalHit, other.Kind);
        Assert.Equal("Ream", other.Source);
        Assert.Equal(1234, other.Amount);
        Assert.Equal(DamageKind.Melee, other.DamageKind);
        Assert.Equal(string.Empty, other.Target);

        CombatEvent self = ParseOk("You score a critical hit! (500)");
        Assert.Equal(Self, self.Source); // "You" becomes the log owner
        Assert.Equal(500, self.Amount);
        Assert.Equal(DamageKind.Melee, self.DamageKind);
    }

    [Fact]
    public void Spell_crit_announcements_are_parsed_for_others_and_self()
    {
        CombatEvent other = ParseOk("Oodles delivers a critical blast! (2100)");
        Assert.Equal(CombatEventKind.CriticalHit, other.Kind);
        Assert.Equal("Oodles", other.Source);
        Assert.Equal(2100, other.Amount);
        Assert.Equal(DamageKind.NonMelee, other.DamageKind);

        CombatEvent self = ParseOk("You deliver a critical blast! (900)");
        Assert.Equal(Self, self.Source);
        Assert.Equal(900, self.Amount);
        Assert.Equal(DamageKind.NonMelee, self.DamageKind);
    }

    [Fact]
    public void An_npc_with_a_multi_word_name_can_crit()
    {
        CombatEvent ev = ParseOk("a frost giant scores a critical hit! (77)");
        Assert.Equal(Giant, ev.Source);
    }

    [Theory]
    [InlineData("Warene slashes a Praetorian Medicus for 180 points of damage. (Critical)", "Warene", 180, true)]
    [InlineData("Zepheera shoots Lord Inquisitor Seru for 82 points of damage. (Critical Double Bow Shot)", "Zepheera", 82, true)]
    [InlineData("Warene slashes a Praetorian Medicus for 180 points of damage. (Lucky Critical)", "Warene", 180, true)]
    [InlineData("Warene slashes a Praetorian Medicus for 180 points of damage. (Crippling Blow)", "Warene", 180, false)]
    [InlineData("Warene slashes a Praetorian Medicus for 180 points of damage.", "Warene", 180, false)]
    public void A_damage_line_tagged_critical_is_marked_as_a_crit(string message, string attacker, int damage, bool critical)
    {
        CombatEvent ev = ParseOk(message);
        Assert.Equal(CombatEventKind.Damage, ev.Kind);
        Assert.Equal(attacker, ev.Source);
        Assert.Equal(damage, ev.Amount);
        Assert.Equal(critical, ev.IsCritical);
    }

    [Fact]
    public void Spell_and_dot_lines_can_carry_the_critical_tag_too()
    {
        Assert.True(ParseOk("a frost giant was hit by non-melee for 900 points of damage. (Critical)").IsCritical);
        Assert.True(ParseOk("a frost giant has taken 300 damage from your Flame Lick. (Critical)").IsCritical);
        Assert.False(ParseOk("a frost giant has taken 300 damage from your Flame Lick.").IsCritical);
    }

    [Fact]
    public async Task Tagged_crits_fill_max_crit_and_crit_percent()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Warene");
        await RunLinesAsync(new[]
        {
            Line("Warene slashes a Praetorian Medicus for 180 points of damage. (Critical)", "11:45:01"),
            Line("Warene slashes a Praetorian Medicus for 100 points of damage.", "11:45:02"),
            Line("Warene slashes a Praetorian Medicus for 150 points of damage. (Critical)", "11:45:03"),
            Line("Warene slashes a Praetorian Medicus for 90 points of damage.", "11:45:04"),
        }, Self, LogRoute.Dps, dpsSink: tracker);

        ParticipantSnapshot warene = Only(tracker);
        Assert.Equal(4, warene.Hits);
        Assert.Equal(2, warene.CritHitCount);
        Assert.Equal(180, warene.MaxCritHit);
        Assert.Equal(50.0, warene.CritRatePercent, 3);
    }

    [Theory]
    [InlineData("Ream Scores a critical hit!(1234)", "Ream", 1234, DamageKind.Melee)]      // capital verb, no space, as the client writes it
    [InlineData("Ream Scores a critical hit! (1234)", "Ream", 1234, DamageKind.Melee)]
    [InlineData("Ream Scores a Lucky critical hit!(88)", "Ream", 88, DamageKind.Melee)]
    [InlineData("Oodles Delivers a critical blast!(2100)", "Oodles", 2100, DamageKind.NonMelee)]
    public void The_clients_capitalized_and_unspaced_crit_lines_are_understood(string message, string attacker, int damage, DamageKind kind)
    {
        CombatEvent ev = ParseOk(message);
        Assert.Equal(CombatEventKind.CriticalHit, ev.Kind);
        Assert.Equal(attacker, ev.Source);
        Assert.Equal(damage, ev.Amount);
        Assert.Equal(kind, ev.DamageKind);
    }

    [Theory]
    [InlineData("Ream scores a critical hit!")]                       // no damage in parentheses
    [InlineData("Ream scores a critical hit! (abc)")]
    [InlineData("Ream tells the group, 'a critical hit! (12)'")]      // chat that merely contains the words
    [InlineData("Ream lands a critical hit! (12)")]
    public void Lines_that_are_not_crit_announcements_are_not_parsed(string message)
    {
        string line = Line(message);
        Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
        Assert.False(CombatLineParser.TryParse(line, ts, Self, out _));
    }

    // ───────────────────────── correlation ─────────────────────────

    [Fact]
    public async Task A_crit_announcement_flags_the_following_damage_line_end_to_end()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnLogOwner(Self);

        await RunLinesAsync(
            new[]
            {
                Line("You score a critical hit! (900)", "11:45:01"),
                Line("You slash a frost giant for 900 points of damage.", "11:45:01"),
                Line("You slash a frost giant for 100 points of damage.", "11:45:02"),
            },
            Self, LogRoute.Dps, dpsSink: tracker);

        ParticipantSnapshot p = Only(tracker);
        Assert.Equal(2, p.Hits);
        Assert.Equal(1, p.CritHitCount);
        Assert.Equal(900, p.MaxCritHit);
        Assert.Equal(900, p.MaxHit);
        Assert.Equal(50.0, p.CritRatePercent, 3);
    }

    [Fact]
    public void Damage_on_the_same_second_is_a_crit_and_the_announcement_is_consumed_once()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Crit("Stackprime", 500, "11:45:01"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 500, "11:45:01"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 120, "11:45:01")); // same second, but the announcement is used up

        ParticipantSnapshot p = Only(tracker);
        Assert.Equal(2, p.Hits);
        Assert.Equal(1, p.CritHitCount);
        Assert.Equal(500, p.MaxCritHit);
    }

    [Fact]
    public void Damage_within_one_second_is_a_crit_but_two_seconds_later_is_not()
    {
        var withinWindow = new LiveDpsTracker();
        withinWindow.OnCombatEvent(Crit("Stackprime", 500, "11:45:01"));
        withinWindow.OnCombatEvent(Hit("Stackprime", Giant, 500, "11:45:02")); // exactly 1s later
        Assert.Equal(1, Only(withinWindow).CritHitCount);

        var outsideWindow = new LiveDpsTracker();
        outsideWindow.OnCombatEvent(Crit("Stackprime", 500, "11:45:01"));
        outsideWindow.OnCombatEvent(Hit("Stackprime", Giant, 500, "11:45:03")); // 2s later: a different hit
        ParticipantSnapshot p = Only(outsideWindow);
        Assert.Equal(0, p.CritHitCount);
        Assert.Equal(0, p.MaxCritHit);
        Assert.Equal(0.0, p.CritRatePercent);
    }

    [Fact]
    public void A_crit_only_applies_to_the_attacker_who_announced_it()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Crit("Oodles", 800, "11:45:01"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 300, "11:45:01")); // someone else's hit
        tracker.OnCombatEvent(Hit("Oodles", Giant, 800, "11:45:01"));

        EncounterSnapshot enc = Assert.Single(tracker.Snapshot.Active);
        ParticipantSnapshot oodles = enc.Participants.Single(p => p.Name == "Oodles");
        ParticipantSnapshot stackprime = enc.Participants.Single(p => p.Name == "Stackprime");
        Assert.Equal(1, oodles.CritHitCount);
        Assert.Equal(0, stackprime.CritHitCount);
    }

    [Fact]
    public void Spell_crits_flag_non_melee_damage()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Crit("Oodles", 2100, "11:45:01", DamageKind.NonMelee));
        tracker.OnCombatEvent(SpellHit("Oodles", Giant, 2100, "11:45:01"));
        tracker.OnCombatEvent(SpellHit("Oodles", Giant, 700, "11:45:02"));

        ParticipantSnapshot p = Only(tracker);
        Assert.Equal(2, p.Hits);
        Assert.Equal(1, p.CritHitCount);
        Assert.Equal(2100, p.MaxCritHit);
    }

    [Fact]
    public void Damage_over_time_ticks_neither_use_nor_consume_a_crit_announcement()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Crit("Stackprime", 500, "11:45:01"));
        tracker.OnCombatEvent(DotTick("Stackprime", Giant, 40, "11:45:01")); // a DoT tick in between
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 500, "11:45:01"));    // the announced hit

        ParticipantSnapshot p = Only(tracker);
        Assert.Equal(2, p.Hits);
        Assert.Equal(1, p.CritHitCount);
        Assert.Equal(500, p.MaxCritHit);
    }

    [Fact]
    public void An_announcement_alone_creates_no_encounter_and_an_npcs_crit_does_not_leak_onto_player_hits()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Crit(Giant, 300, "11:45:01"));
        Assert.Empty(tracker.Snapshot.Active);

        tracker.OnCombatEvent(Hit(Giant, "Stackprime", 300, "11:45:01")); // the NPC's damage is ignored but uses its announcement
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 50, "11:45:01"));

        Assert.Equal(0, Only(tracker).CritHitCount);
    }

    [Fact]
    public void Crit_statistics_reach_the_encounter_snapshot()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Crit("Stackprime", 700, "11:45:01"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 700, "11:45:01"));
        tracker.OnCombatEvent(Crit("Stackprime", 400, "11:45:02"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 400, "11:45:02"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:03"));
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, "11:45:04"));

        ParticipantSnapshot p = Only(tracker);
        Assert.Equal(4, p.Hits);
        Assert.Equal(2, p.CritHitCount);
        Assert.Equal(700, p.MaxCritHit);
        Assert.Equal(50.0, p.CritRatePercent, 3); // 2 of 4
    }

    [Fact]
    public void Reset_forgets_pending_announcements()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Crit("Stackprime", 500, "11:45:01"));
        tracker.Reset();

        tracker.OnCombatEvent(Hit("Stackprime", Giant, 500, "11:45:01"));

        Assert.Equal(0, Only(tracker).CritHitCount);
    }

    [Fact]
    public void Live_and_batch_feeds_agree_on_crits()
    {
        CombatEvent[] events =
        {
            Crit("Stackprime", 500, "11:45:01"),
            Hit("Stackprime", Giant, 500, "11:45:01"),
            Hit("Oodles", Giant, 200, "11:45:02"),
            Crit("Oodles", 900, "11:45:03", DamageKind.NonMelee),
            SpellHit("Oodles", Giant, 900, "11:45:03"),
        };

        var live = new LiveDpsTracker();
        foreach (CombatEvent e in events)
        {
            live.OnCombatEvent(e);
        }

        live.OnLogClock(At("11:46:59"));

        var historical = new HistoricalDpsTracker();
        historical.BeginBatch();
        foreach (CombatEvent e in events)
        {
            historical.OnCombatEvent(e);
        }

        historical.EndBatch();

        Assert.Equal(
            live.Snapshot.Completed.Single().Participants,
            historical.Snapshot.Completed.Single().Participants);
    }
}
