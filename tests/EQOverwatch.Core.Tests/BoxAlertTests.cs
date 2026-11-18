using System.Globalization;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>The tracker's live events for the box alerts and the mob card, the rampage tag, and the fight progress estimate.</summary>
public class BoxAlertTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 21, 18, 0);

    private static string Line(DateTime at, string message) =>
        "[" + at.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "] " + message;

    private static void Feed(LiveDpsTracker tracker, string owner, DateTime at, string message)
    {
        string line = Line(at, message);
        tracker.OnLogOwner(owner);
        if (CombatLineParser.TryParse(line, at, owner, out CombatEvent e))
        {
            tracker.OnCombatEvent(e);
        }
        else
        {
            tracker.OnLogClock(at);
            tracker.OnNonCombatMessage(line.AsSpan(LogLineFormat.MessageOffset));
        }
    }

    [Theory]
    [InlineData("Trooper Mykeor hits Ream for 207 points of damage. (Rampage)", true)]
    [InlineData("Gobann hits froglok jin shaman for 29 points of damage. (Wild Rampage)", true)]
    [InlineData("Trooper Mykeor tries to hit Ream, but misses! (Rampage)", true)]
    [InlineData("Emperor Ssraeshza  punches Ugli for 300 points of damage.", false)]
    [InlineData("Emperor Ssraeshza  tries to punch Ugli, but misses!", false)]
    public void Rampage_hits_and_misses_are_tagged(string message, bool rampage)
    {
        Assert.True(CombatLineParser.TryParse(Line(T0, message), T0, "Oodles", out CombatEvent e));
        Assert.Equal(rampage, e.IsRampage);
    }

    [Fact]
    public void A_mob_attacking_a_player_in_a_live_fight_is_reported_but_not_a_rampage_a_spell_or_a_log_read_in()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Stackprime");
        tracker.AddKnownPlayer("Plethora");
        var attacked = new List<string>();
        tracker.PlayerAttacked += (player, mob) => attacked.Add(player + " <- " + mob);

        Feed(tracker, "Stackprime", T0, "You slash Emperor Ssraeshza for 100 points of damage.");
        Feed(tracker, "Plethora", T0.AddSeconds(1), "Emperor Ssraeshza  punches Plethora for 300 points of damage.");
        Feed(tracker, "Plethora", T0.AddSeconds(2), "Emperor Ssraeshza  tries to punch YOU, but YOU dodge!");
        Feed(tracker, "Plethora", T0.AddSeconds(3), "Emperor Ssraeshza  hits Plethora for 207 points of damage. (Rampage)");
        Feed(tracker, "Plethora", T0.AddSeconds(4), "Emperor Ssraeshza  hit Plethora for 4000 points of unresistable damage by Rage of Ssraeshza.");

        Assert.Equal(new[] { "Plethora <- Emperor Ssraeshza", "Plethora <- Emperor Ssraeshza" }, attacked);

        tracker.BeginBatch();
        Feed(tracker, "Plethora", T0.AddSeconds(5), "Emperor Ssraeshza  punches Plethora for 300 points of damage.");
        tracker.EndBatch();
        Assert.Equal(2, attacked.Count);
    }

    [Fact]
    public void Out_of_range_and_out_of_sight_are_reported_only_during_a_fight()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Myriad");
        var reports = new List<string>();
        tracker.TargetOutOfReach += (who, unseen) => reports.Add(who + (unseen ? " unseen" : " far"));

        Feed(tracker, "Myriad", T0, "Your target is too far away, get closer!"); // no fight yet
        Feed(tracker, "Myriad", T0.AddSeconds(1), "You shoot a gnome for 100 points of damage.");
        Feed(tracker, "Myriad", T0.AddSeconds(2), "Your target is too far away, get closer!");
        Feed(tracker, "Myriad", T0.AddSeconds(3), "Your target is out of range, get closer!");
        Feed(tracker, "Myriad", T0.AddSeconds(4), "You cannot see your target.");

        Assert.Equal(new[] { "Myriad far", "Myriad far", "Myriad unseen" }, reports);
    }

    [Fact]
    public void A_con_with_a_level_is_reported_with_the_log_owner()
    {
        var tracker = new LiveDpsTracker();
        var cons = new List<string>();
        tracker.MobConsidered += (who, mob, level) => cons.Add($"{who}:{mob}:{level}");

        Feed(tracker, "Stackprime", T0, "Yasiz the Devourer scowls at you, ready to attack -- looks like she would wipe the floor with you! (Lvl: 62)");

        Assert.Equal(new[] { "Stackprime:Yasiz the Devourer:62" }, cons);
    }

    // ───────────────────────────── fight progress ─────────────────────────────

    private static BossFight Kill(long damage, string? server = "frostreaver", bool killed = true) =>
        new("Emperor Ssraeshza", T0, 400, damage, 40, 0, killed, null, server);

    [Fact]
    public void Progress_comes_from_the_median_kill_on_the_server_and_needs_two_kills()
    {
        var fights = new List<BossFight> { Kill(400_000), Kill(5_000_000, killed: false), Kill(9_000_000, "teek") };
        Assert.Null(FightProgress.Estimate(fights, "Emperor Ssraeshza", "frostreaver", 100_000, 100));

        fights.Add(Kill(500_000));
        fights.Add(Kill(450_000));
        FightEstimate e = FightProgress.Estimate(fights, "emperor ssraeshza", "frostreaver", 225_000, 100)!;
        Assert.Equal(3, e.Kills);
        Assert.Equal(0.5, e.Fraction, 3); // median 450,000
        Assert.Equal(100, e.SecondsLeft!.Value, 3); // 225,000 more at 2,250 a second
        Assert.Equal("~50% · about 1:40 left (from 3 kills)", e.Describe());

        FightEstimate past = FightProgress.Estimate(fights, "Emperor Ssraeshza", "frostreaver", 900_000, 300)!;
        Assert.Equal(0.99, past.Fraction, 3);
        Assert.Equal("~99% · any moment now (from 3 kills)", past.Describe());
    }
}
