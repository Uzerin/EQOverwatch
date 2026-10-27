using EQCompanion.Core.Models;
using EQCompanion.Core.Parsing;
using Xunit;
using static EQCompanion.Core.Tests.TestData;

namespace EQCompanion.Core.Tests;

public class CombatLineParserTests
{
    private static CombatEvent Parse(string line)
    {
        Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
        Assert.True(CombatLineParser.TryParse(line, ts, Self, out CombatEvent ev), $"Expected a combat event for: {line}");
        return ev;
    }

    [Fact]
    public void Melee_by_self_is_normalized_to_the_character_name()
    {
        CombatEvent ev = Parse(Line("You slash a frost giant for 142 points of damage."));

        Assert.Equal(CombatEventKind.Damage, ev.Kind);
        Assert.Equal(Self, ev.Source);
        Assert.Equal("a frost giant", ev.Target);
        Assert.Equal(142, ev.Amount);
        Assert.Equal(DamageKind.Melee, ev.DamageKind);
        Assert.Equal("slash", ev.Detail);
        Assert.Equal(new DateTime(2026, 10, 21, 11, 45, 1), ev.Timestamp);
    }

    [Fact]
    public void Melee_by_another_player()
    {
        CombatEvent ev = Parse(Line("Stackprime crushes a frost giant for 210 points of damage.", "11:45:02"));

        Assert.Equal("Stackprime", ev.Source);
        Assert.Equal("a frost giant", ev.Target);
        Assert.Equal(210, ev.Amount);
        Assert.Equal(DamageKind.Melee, ev.DamageKind);
    }

    [Fact]
    public void Non_melee_spell_damage()
    {
        CombatEvent ev = Parse(Line("Oodles hit a frost giant for 450 points of non-melee damage.", "11:45:03"));

        Assert.Equal("Oodles", ev.Source);
        Assert.Equal(450, ev.Amount);
        Assert.Equal(DamageKind.NonMelee, ev.DamageKind);
    }

    [Fact]
    public void Target_death()
    {
        CombatEvent ev = Parse(Line("a frost giant has been slain by Stackprime!", "11:45:15"));

        Assert.Equal(CombatEventKind.Death, ev.Kind);
        Assert.Equal("a frost giant", ev.Target);
        Assert.Equal("Stackprime", ev.Source);
    }

    [Fact]
    public void Npc_attacker_with_a_multi_word_name_hitting_you()
    {
        CombatEvent ev = Parse(Line("a frost giant hits YOU for 50 points of damage."));

        Assert.Equal("a frost giant", ev.Source);
        Assert.Equal(Self, ev.Target);
        Assert.Equal(50, ev.Amount);
    }

    [Fact]
    public void Misses_are_parsed_for_hit_miss_rate()
    {
        CombatEvent mine = Parse(Line("You try to slash a frost giant, but miss!"));
        Assert.Equal(CombatEventKind.Miss, mine.Kind);
        Assert.Equal(Self, mine.Source);
        Assert.Equal("a frost giant", mine.Target);

        CombatEvent theirs = Parse(Line("Stackprime tries to crush a frost giant, but misses!"));
        Assert.Equal(CombatEventKind.Miss, theirs.Kind);
        Assert.Equal("Stackprime", theirs.Source);
    }

    [Fact]
    public void Own_spell_and_dot_damage()
    {
        CombatEvent spell = Parse(Line("a frost giant was hit by non-melee for 300 points of damage."));
        Assert.Equal(Self, spell.Source);
        Assert.Equal(300, spell.Amount);
        Assert.Equal(DamageKind.NonMelee, spell.DamageKind);

        CombatEvent dot = Parse(Line("a frost giant has taken 100 damage from your Flame Lick."));
        Assert.Equal(Self, dot.Source);
        Assert.Equal(DamageKind.DamageOverTime, dot.DamageKind);
        Assert.Equal("Flame Lick", dot.Detail);

        CombatEvent otherDot = Parse(Line("a frost giant has taken 80 damage from Flame Lick by Oodles."));
        Assert.Equal("Oodles", otherDot.Source);
        Assert.Equal(80, otherDot.Amount);
    }

    [Theory]
    [InlineData("Ream begins to cast a spell. <Complete Heal>")]
    [InlineData("Your protective skin fades.")]
    [InlineData("You have gained experience!")]
    [InlineData("Stackprime tells the group, 'pull it, no points of damage worries'")]
    public void Non_combat_lines_are_ignored(string message)
    {
        string line = Line(message);
        Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
        Assert.False(CombatLineParser.TryParse(line, ts, Self, out _));
    }
}
