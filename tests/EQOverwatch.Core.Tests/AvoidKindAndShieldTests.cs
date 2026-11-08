using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Services;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public sealed class AvoidKindAndShieldTests
{
    private const string Giant = "a frost giant";

    private static CombatEvent Parse(string message, string self = "Stackprime")
    {
        string line = $"[Fri Nov 06 21:19:17 2026] {message}";
        Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
        Assert.True(CombatLineParser.TryParse(line, ts, self, out CombatEvent e), message);
        return e;
    }

    // ───────────────────────── misses (lines taken from a real log) ─────────────────────────

    [Theory]
    [InlineData("X tries to punch YOU, but misses!", AvoidKind.Miss)]
    [InlineData("X tries to punch YOU, but YOU dodge!", AvoidKind.Dodge)]
    [InlineData("X tries to punch YOU, but YOU parry!", AvoidKind.Parry)]
    [InlineData("X tries to slash YOU, but YOU riposte!", AvoidKind.Riposte)]
    [InlineData("Lord Inquisitor Seru tries to slash Regnier, but Regnier parries!", AvoidKind.Parry)]
    [InlineData("The Statue of Rallos Zek tries to hit Ugli, but Ugli blocks!", AvoidKind.Block)]
    [InlineData("Lord Yelinak tries to bite Jannder, but Jannder dodges!", AvoidKind.Dodge)]
    [InlineData("King Tormax tries to hit YOU, but YOUR magical skin absorbs the blow! (Rampage)", AvoidKind.Other)]
    [InlineData("Vyzh`dra the Exiled tries to punch YOU, but YOU are INVULNERABLE!", AvoidKind.Other)]
    public void The_way_a_miss_ended_is_classified(string message, AvoidKind expected)
    {
        CombatEvent e = Parse(message.Replace("X tries", "Gruk tries"));
        Assert.Equal(CombatEventKind.Miss, e.Kind);
        Assert.Equal(expected, e.Avoid);
    }

    [Theory]
    [InlineData("A pixie trickster tries to pierce Ashal, but misses! (Riposte)", AvoidKind.Miss)]
    [InlineData("An Elder Onyx Drake tries to bite Zinkx, but misses! (Flurry)", AvoidKind.Miss)]
    [InlineData("Trooper Mykeor tries to hit Ream, but misses! (Rampage)", AvoidKind.Miss)]
    [InlineData("An Elder Onyx Drake tries to bite Diabetus, but Diabetus parries! (Flurry)", AvoidKind.Parry)]
    [InlineData("The Statue of Rallos Zek tries to hit Ugli, but Ugli dodges! (Flurry)", AvoidKind.Dodge)]
    [InlineData("Lord Yelinak tries to bite Jannder, but Jannder blocks! (Rampage)", AvoidKind.Block)]
    [InlineData("An Elder Onyx Drake tries to bite YOU, but YOU dodge! (Flurry)", AvoidKind.Dodge)]
    public void A_miss_with_a_trailing_attack_tag_is_still_a_miss(string message, AvoidKind expected)
    {
        CombatEvent e = Parse(message);
        Assert.Equal(CombatEventKind.Miss, e.Kind);
        Assert.Equal(expected, e.Avoid);
    }

    [Fact]
    public void Your_own_tagged_miss_names_you_as_the_attacker_and_yourself_as_the_target_of_yours()
    {
        CombatEvent mine = Parse("You try to slash a lurking mummy, but miss! (Riposte)");
        Assert.Equal("Stackprime", mine.Source);
        Assert.Equal("a lurking mummy", mine.Target);
        Assert.Equal(AvoidKind.Miss, mine.Avoid);

        CombatEvent theirs = Parse("Trooper Mykeor tries to hit YOU, but YOU dodge!", "Stackprime");
        Assert.Equal("Stackprime", theirs.Target);
    }

    // ───────────────────────── damage shields ─────────────────────────

    [Theory]
    [InlineData("Roukuko is pierced by a barbed bone skeleton's thorns for 7 points of non-melee damage.", "a barbed bone skeleton", "Roukuko", 7, "thorns")]
    [InlineData("A death beetle is burned by YOUR flames for 5 points of non-melee damage.", "Stackprime", "A death beetle", 5, "flames")]
    [InlineData("A death beetle is burned by Jarartik's flames for 5 points of non-melee damage.", "Jarartik", "A death beetle", 5, "flames")]
    [InlineData("A Di`Zok Underling is pierced by YOUR thorns for 10 points of non-melee damage.", "Stackprime", "A Di`Zok Underling", 10, "thorns")]
    [InlineData("YOU are pierced by an undead brewer's thorns for 5 points of non-melee damage!", "an undead brewer", "Stackprime", 5, "thorns")]
    [InlineData("A Raptor is tormented by Exit's frost for 8 points of non-melee damage.", "Exit", "A Raptor", 8, "frost")]
    [InlineData("YOU are burned by froglok krup enchanter's flames for 11 points of non-melee damage!", "froglok krup enchanter", "Stackprime", 11, "flames")]
    public void Damage_shield_lines_are_read_as_shield_damage_from_the_owner(string message, string owner, string victim, int amount, string kind)
    {
        CombatEvent e = Parse(message);
        Assert.Equal(CombatEventKind.Damage, e.Kind);
        Assert.Equal(DamageKind.DamageShield, e.DamageKind);
        Assert.Equal(owner, e.Source);
        Assert.Equal(victim, e.Target);
        Assert.Equal(amount, e.Amount);
        Assert.Equal(kind, e.Detail);
        Assert.Equal(AttackType.DamageShield, AttackTypes.Classify(e.DamageKind, e.Detail));
    }

    private static CombatEvent Shield(string owner, string victim, int amount, string time) =>
        new(At(time), CombatEventKind.Damage, owner, victim, amount, DamageKind.DamageShield, "thorns");

    [Fact]
    public void A_players_damage_shield_is_its_own_slice_of_their_damage_and_counts_in_the_totals()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(new CombatEvent(At("10:00:00"), CombatEventKind.Damage, "Stackprime", Giant, 100, DamageKind.Melee, "slash"));
        tracker.OnCombatEvent(Shield("Stackprime", Giant, 40, "10:00:01"));
        tracker.OnCombatEvent(Shield("Stackprime", Giant, 60, "10:00:02"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        ParticipantSnapshot stack = Assert.Single(fight.Participants);
        Assert.Equal(200, stack.TotalDamage);
        Assert.Equal(200, fight.TotalDamage);
        AttackSlice shield = Assert.Single(stack.Attacks!, a => a.Type == AttackType.DamageShield);
        Assert.Equal(100, shield.Total);
        Assert.Equal(100, Assert.Single(stack.Attacks!, a => a.Type == AttackType.Slash).Total);
    }

    [Fact]
    public void The_targets_damage_shield_on_players_is_damage_taken_not_an_attack_and_is_summarised()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(new CombatEvent(At("10:00:00"), CombatEventKind.Damage, "Stackprime", Giant, 500, DamageKind.Melee, "slash"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:03"), CombatEventKind.Damage, Giant, "Ream", 200, DamageKind.Melee, "hit"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:04"), CombatEventKind.Miss, Giant, "Ream", 0, DamageKind.None, "hit", false, AvoidKind.Dodge));
        tracker.OnCombatEvent(Shield(Giant, "Ream", 30, "10:00:05"));
        for (int t = 6; t <= 36; t += 6)
        {
            // Stackprime keeps hitting, so the fight stays open (it ends 8 seconds after the last hit)
            tracker.OnCombatEvent(new CombatEvent(At($"10:00:{t:00}"), CombatEventKind.Damage, "Stackprime", Giant, 100, DamageKind.Melee, "slash"));
        }

        tracker.OnCombatEvent(Shield(Giant, "Stackprime", 50, "10:00:41"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal(1100, fight.TotalDamage); // the shield damage is not damage dealt to the target
        DamageTakenSnapshot ream = fight.TakenRows.Single(t => t.Name == "Ream");
        Assert.Equal(230, ream.TotalDamage);
        Assert.Equal(30, ream.ShieldDamage);
        Assert.Equal(1, ream.Hits);       // the shield hit is not an attack on Ream
        Assert.Equal(1, ream.Dodges);
        Assert.Equal(50.0, ream.AvoidPercent, 3);
        Assert.Equal(280, fight.TotalDamageTaken);

        DamageShieldSummary summary = Assert.IsType<DamageShieldSummary>(fight.DamageShield);
        Assert.Equal(80, summary.Total);
        Assert.Equal(2, summary.Hits);
        Assert.Equal(5.0, summary.FirstSeconds, 3);
        Assert.Equal(41.0, summary.LastSeconds, 3);
        Assert.Equal(36.0, summary.ActiveSeconds, 3);
    }

    [Fact]
    public void A_fight_without_a_damage_shield_has_no_summary()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(new CombatEvent(At("10:00:00"), CombatEventKind.Damage, "Stackprime", Giant, 500, DamageKind.Melee, "slash"));
        Assert.Null(Assert.Single(tracker.Snapshot.Active).DamageShield);
    }

    // ───────────────────────── avoidance percentages ─────────────────────────

    [Fact]
    public void Each_defense_is_a_share_of_all_attacks_on_the_player_to_two_decimals()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(new CombatEvent(At("10:00:00"), CombatEventKind.Damage, "Stackprime", Giant, 500, DamageKind.Melee, "slash"));

        int second = 1;
        void Attack(AvoidKind? avoid, int count)
        {
            for (int i = 0; i < count; i++)
            {
                string time = $"10:00:{second++:00}";
                tracker.OnCombatEvent(new CombatEvent(At(time), CombatEventKind.Damage, "Stackprime", Giant, 10, DamageKind.Melee, "slash"));
                tracker.OnCombatEvent(avoid is null
                    ? new CombatEvent(At(time), CombatEventKind.Damage, Giant, "Gruk", 100, DamageKind.Melee, "hit")
                    : new CombatEvent(At(time), CombatEventKind.Miss, Giant, "Gruk", 0, DamageKind.None, "hit", false, avoid.Value));
            }
        }

        Attack(null, 25);
        Attack(AvoidKind.Dodge, 10);
        Attack(AvoidKind.Parry, 5);
        Attack(AvoidKind.Block, 3);
        Attack(AvoidKind.Riposte, 2);
        Attack(AvoidKind.Miss, 4);
        Attack(AvoidKind.Other, 1);

        DamageTakenSnapshot gruk = Assert.Single(Assert.Single(tracker.Snapshot.Active).TakenRows);
        Assert.Equal(50, gruk.Attacks);
        Assert.Equal(25, gruk.Avoided);
        Assert.Equal(50.0, gruk.AvoidPercent, 2);
        Assert.Equal(20.0, gruk.DodgePercent, 2);
        Assert.Equal(10.0, gruk.ParryPercent, 2);
        Assert.Equal(6.0, gruk.BlockPercent, 2);
        Assert.Equal(4.0, gruk.RipostePercent, 2);
        Assert.Equal(8.0, gruk.MissPercent, 2);
        Assert.Equal(2.0, gruk.OtherAvoidPercent, 2);
    }

    [Fact]
    public void A_miss_event_without_a_reason_counts_as_a_plain_miss()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(new CombatEvent(At("10:00:00"), CombatEventKind.Damage, "Stackprime", Giant, 500, DamageKind.Melee, "slash"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:01"), CombatEventKind.Miss, Giant, "Gruk", 0, DamageKind.None, "hit"));
        DamageTakenSnapshot gruk = Assert.Single(Assert.Single(tracker.Snapshot.Active).TakenRows);
        Assert.Equal(1, gruk.Missed);
        Assert.Equal(100.0, gruk.AvoidPercent, 2);
    }
}
