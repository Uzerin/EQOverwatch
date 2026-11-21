using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>The hits and heals a player took in the last seconds before their death, or before the end of the fight.</summary>
public class LastSecondsTests
{
    private static void Feed(DpsTrackerService tracker, params string[] lines)
    {
        foreach (string line in lines)
        {
            Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
            tracker.OnLogClock(ts);
            if (CombatLineParser.TryParse(line, ts, "Oodles", out CombatEvent e))
            {
                tracker.OnCombatEvent(e);
            }
            else
            {
                tracker.OnNonCombatMessage(line[(line.IndexOf(']') + 2)..]);
            }
        }
    }

    [Fact]
    public void A_tanks_last_ten_seconds_before_death_show_hits_from_every_mob_and_heals()
    {
        var tracker = new LiveDpsTracker();
        foreach (string name in new[] { "Oodles", "Tankard", "Mendwell" })
        {
            tracker.AddKnownPlayer(name);
        }

        Feed(tracker,
            "[Fri Nov 20 21:27:40 2026] Oodles punches Diabo Xi Xin for 120 points of damage.",
            "[Fri Nov 20 21:27:45 2026] Diabo Xi Xin hits Tankard for 900 points of damage.", // 14 seconds before: left out
            "[Fri Nov 20 21:27:47 2026] Oodles punches Diabo Xi Xin for 120 points of damage.",
            "[Fri Nov 20 21:27:50 2026] Diabo Xi Xin hits Tankard for 1725 points of damage.",
            "[Fri Nov 20 21:27:53 2026] Oodles punches Diabo Xi Xin for 120 points of damage.",
            "[Fri Nov 20 21:27:52 2026] Mendwell healed Tankard for 638 (16830) hit points by Complete Heal.",
            "[Fri Nov 20 21:27:54 2026] a shissar arch arcanist hits Tankard for 400 points of damage.",
            "[Fri Nov 20 21:27:55 2026] Diabo Xi Xin hits Tankard for 1402 points of damage. (Rampage)",
            "[Fri Nov 20 21:27:56 2026] Diabo Xi Xin tries to hit Tankard, but Tankard dodges!",
            "[Fri Nov 20 21:27:58 2026] Oodles punches Diabo Xi Xin for 120 points of damage.",
            "[Fri Nov 20 21:27:59 2026] Diabo Xi Xin hits Tankard for 1688 points of damage.",
            "[Fri Nov 20 21:27:59 2026] Tankard has been slain by Diabo Xi Xin!",
            "[Fri Nov 20 21:28:03 2026] Oodles punches Diabo Xi Xin for 120 points of damage.");

        EncounterSnapshot fight = tracker.Snapshot.Active.Single(a => a.TargetName == "Diabo Xi Xin");
        LastSecondsSnapshot tank = fight.LastSeconds.Single(w => w.Name == "Tankard");
        Assert.True(tank.Died);
        Assert.Equal(new DateTime(2026, 11, 20, 21, 27, 59), tank.End);
        Assert.Equal(
            new[] { "Diabo Xi Xin hit 1725", "Mendwell Complete Heal +638", "a shissar arch arcanist hit 400", "Diabo Xi Xin rampage 1402", "Diabo Xi Xin hit 1688" },
            tank.Events.Select(e => $"{e.Source} {e.What} {(e.IsHeal ? "+" : string.Empty)}{e.Amount}"));
        Assert.Equal((5215L, 638L), (tank.Taken, tank.Healed));
    }

    [Fact]
    public void Someone_who_lived_shows_the_last_ten_seconds_of_the_fight()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Oodles");
        tracker.AddKnownPlayer("Shieldwall");
        Feed(tracker,
            "[Fri Nov 20 21:27:40 2026] Oodles punches Diabo Xi Xin for 120 points of damage.",
            "[Fri Nov 20 21:27:41 2026] Diabo Xi Xin hits Shieldwall for 500 points of damage.",
            "[Fri Nov 20 21:27:44 2026] Oodles punches Diabo Xi Xin for 120 points of damage.",
            "[Fri Nov 20 21:27:48 2026] Oodles punches Diabo Xi Xin for 120 points of damage.",
            "[Fri Nov 20 21:27:53 2026] Oodles punches Diabo Xi Xin for 120 points of damage.",
            "[Fri Nov 20 21:27:55 2026] Diabo Xi Xin hits Shieldwall for 980 points of damage.",
            "[Fri Nov 20 21:28:00 2026] Oodles punches Diabo Xi Xin for 120 points of damage.");

        EncounterSnapshot f = Assert.Single(tracker.Snapshot.Active);
        LastSecondsSnapshot window = Assert.Single(f.LastSeconds);
        Assert.False(window.Died);
        Assert.Equal(980, Assert.Single(window.Events).Amount);
    }
}
