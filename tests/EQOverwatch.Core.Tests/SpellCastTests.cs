using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public sealed class SpellCastTests
{
    private const string Boss = "Lord Inquisitor Seru";

    private static void Say(LiveDpsTracker tracker, string time, string message)
    {
        // The pipeline moves the log clock first, then delivers the line (see LogEventProcessor).
        tracker.OnLogClock(At(time));
        tracker.OnNonCombatMessage(message);
    }

    /// <summary>A fight with casts before it (one too old to count, one just ahead of it) and during it.</summary>
    private static EncounterSnapshot Fight()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnLogOwner("Ream");
        Say(tracker, "11:44:30", "Nasti begins casting Haste.");
        Say(tracker, "11:44:58", "You begin casting Cripple.");
        tracker.OnCombatEvent(Hit("Ream", Boss, 100, "11:45:00"));
        Say(tracker, "11:45:02", "Nasti begins casting Weaken.");
        Say(tracker, "11:45:03", "You begin casting Slow.");
        Say(tracker, "11:45:03", "Warene begins to cast a spell. <Slow>");
        Say(tracker, "11:45:04", "Your spell fizzles!");
        tracker.OnCombatEvent(Hit("Ream", Boss, 100, "11:45:06"));
        tracker.OnCombatEvent(Death(Boss, "Ream", "11:45:08"));
        return Assert.Single(tracker.Snapshot.Completed);
    }

    [Fact]
    public void Casts_by_players_are_recorded_with_the_fizzle_and_the_lead_in()
    {
        IReadOnlyList<PlayerCastSnapshot> casts = Fight().PlayerCastRows;

        Assert.Equal(4, casts.Count);
        Assert.DoesNotContain(casts, c => c.Spell == "Haste"); // too long before the fight began
        Assert.Contains(casts, c => c.Caster == "Ream" && c.Spell == "Cripple" && !c.Failed); // "You" became the log's character
        Assert.Contains(casts, c => c.Caster == "Nasti" && c.Spell == "Weaken");
        Assert.Contains(casts, c => c.Caster == "Warene" && c.Spell == "Slow" && !c.Failed);
        Assert.Contains(casts, c => c.Caster == "Ream" && c.Spell == "Slow" && c.Failed);
    }

    [Fact]
    public void The_report_breaks_casts_down_by_player_spell_and_lane()
    {
        SpellCastReport report = SpellCastReport.Build(Fight());

        Assert.Equal(4, report.TotalCasts);
        Assert.Equal(new[] { "Ream", "Nasti", "Warene" }, report.Players.Select(p => p.Name));
        Assert.Equal(2, report.Players[0].Casts);
        Assert.Equal(1, report.Players[0].Failed);

        SpellTotalRow slow = report.Spells[0];
        Assert.Equal("Slow", slow.Spell);
        Assert.Equal(2, slow.Casts);
        Assert.Equal(2, slow.Casters);
        Assert.Equal(1, slow.Failed);
        Assert.Equal("Ream", slow.TopCaster);

        CastLane lane = report.Lanes[0];
        Assert.Equal("Slow", lane.Spell);
        Assert.Equal(2, lane.Marks.Count);
        Assert.All(lane.Marks, m => Assert.Equal(3.0, m.Offset));
    }

    [Fact]
    public void The_spell_filter_keeps_only_matching_spells_and_the_text_lists_them()
    {
        EncounterSnapshot fight = Fight();

        SpellCastReport slow = SpellCastReport.Build(fight, "slow");
        Assert.Equal(2, slow.TotalCasts);
        Assert.Equal(2, slow.Players.Count);
        Assert.All(slow.Spells, s => Assert.Equal("Slow", s.Spell));
        Assert.Equal(0, SpellCastReport.Build(fight, "nothing like this").TotalCasts);

        string text = SpellCastReport.Build(fight).BuildText(Boss);
        Assert.Contains("Spell casts vs Lord Inquisitor Seru", text);
        Assert.Contains("Ream: 2 casts", text);
        Assert.Contains("Slow: 2 by 2", text);
    }

    [Fact]
    public void A_boss_casting_is_not_counted_as_a_player_cast()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnLogOwner("Ream");
        tracker.OnCombatEvent(Hit("Ream", Boss, 100, "11:45:00"));
        Say(tracker, "11:45:02", $"{Boss} begins casting Judgment.");
        tracker.OnCombatEvent(Hit("Ream", Boss, 100, "11:45:06"));
        tracker.OnCombatEvent(Death(Boss, "Ream", "11:45:07"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Completed);
        Assert.Empty(fight.PlayerCastRows);
        Assert.Single(fight.CastRows);
    }
}
