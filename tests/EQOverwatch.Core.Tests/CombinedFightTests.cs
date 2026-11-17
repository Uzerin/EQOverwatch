using System.IO.Compression;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace EQOverwatch.Core.Tests;

/// <summary>Several mobs fought at once (some of them mezzed) are one live fight; each gets its own row when it dies or the fight ends.</summary>
public class CombinedFightTests(ITestOutputHelper output)
{
    private static readonly string[] Adds =
    {
        "Heriz the Malignant", "Yasiz the Devourer", "Slakiz the Malignant", "Zlakas the Slayer", "Nilasz the Devourer",
        "Grziz the Tormentor", "Klazaz the Slayer", "Skzik the Tormentor",
    };

    /// <summary>The lines naming Blood of Ssraeshza and its adds, from Oodles' log of Nov 16 2026, 20:51 to 21:12.</summary>
    private static IEnumerable<string> RealFight()
    {
        using var gzip = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Data", "ssraeshza-adds.log.gz")), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    [Fact]
    public void Blood_of_Ssraeshza_and_its_mezzed_adds_are_one_live_fight_and_each_mob_gets_one_row()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnLogOwner("Oodles");
        DateTime last = default;
        var titles = new HashSet<string>();
        foreach (string line in RealFight())
        {
            Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
            last = ts;
            if (CombatLineParser.TryParse(line, ts, "Oodles", out CombatEvent e))
            {
                tracker.OnCombatEvent(e);
            }
            else
            {
                tracker.OnLogClock(ts);
            }

            DpsSnapshot now = tracker.Snapshot;
            Assert.True(now.Active.Count <= 1, $"{ts:HH:mm:ss}: {string.Join(", ", now.Active.Select(a => a.TargetName))}");
            if (ts < new DateTime(2026, 11, 16, 21, 11, 44))
            {
                Assert.Empty(now.Completed); // no mezzed add times out and splits off while the boss is fought
            }

            if (now.Active.SingleOrDefault() is { } live)
            {
                titles.Add(live.TargetName);
            }

            if (ts == new DateTime(2026, 11, 16, 21, 5, 0))
            {
                EncounterSnapshot fight = Assert.Single(now.Active);
                Assert.Equal("Blood of Ssraeshza +8", fight.TargetName); // the mob taking the most damage, plus the others
                Assert.True(fight.Id < 0);
                Assert.Equal(new DateTime(2026, 11, 16, 20, 51, 50), fight.StartTime);
            }
        }

        // The boss died: it split off with its own row, and the next mob took the title.
        EncounterSnapshot boss = Assert.Single(tracker.Snapshot.Completed);
        Assert.Equal(("Blood of Ssraeshza", true), (boss.TargetName, boss.Killed));
        Assert.Equal(new DateTime(2026, 11, 16, 20, 51, 50), boss.StartTime);
        Assert.Equal(new DateTime(2026, 11, 16, 21, 11, 44), boss.EndTime);
        Assert.Contains("Slakiz the Malignant +7", titles);

        // The fight went quiet: the adds that never died get one row each, from their first hit to their last.
        tracker.OnLogClock(last.AddSeconds(30));
        Assert.Empty(tracker.Snapshot.Active);
        Assert.Equal(
            Adds.Append("Blood of Ssraeshza").OrderBy(n => n, StringComparer.Ordinal),
            tracker.Snapshot.Completed.Select(c => c.TargetName).OrderBy(n => n, StringComparer.Ordinal));
        EncounterSnapshot klazaz = tracker.Snapshot.Completed.Single(c => c.TargetName == "Klazaz the Slayer");
        Assert.Equal((new DateTime(2026, 11, 16, 20, 51, 55), new DateTime(2026, 11, 16, 21, 2, 22)), (klazaz.StartTime, klazaz.EndTime));
    }

    private static CombatEvent Hit(string source, string target, int amount, int second) =>
        new(new DateTime(2026, 11, 16, 20, 0, 0).AddSeconds(second), CombatEventKind.Damage, source, target, amount, DamageKind.Melee, "hits");

    [Fact]
    public void Two_camps_no_one_shares_stay_two_fights()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Ream");
        tracker.AddKnownPlayer("Kel");
        tracker.OnLogOwner("Ream");
        tracker.OnCombatEvent(Hit("Ream", "a frost giant", 100, 0));
        tracker.OnLogOwner("Kel");
        tracker.OnCombatEvent(Hit("Kel", "an ice goblin", 50, 1));

        Assert.Equal(new[] { "a frost giant", "an ice goblin" }, tracker.Snapshot.Active.Select(a => a.TargetName));

        // Kel's goblin fight goes quiet: it ends on its own while the giant fight goes on.
        tracker.OnLogOwner("Kel");
        tracker.OnCombatEvent(Hit("Kel", "an ice goblin", 50, 2));
        for (int s = 3; s <= 20; s += 2)
        {
            tracker.OnLogOwner("Ream");
            tracker.OnCombatEvent(Hit("Ream", "a frost giant", 100, s));
        }

        Assert.Equal("an ice goblin", Assert.Single(tracker.Snapshot.Completed).TargetName);
        Assert.Equal("a frost giant", Assert.Single(tracker.Snapshot.Active).TargetName);
    }

    [Fact]
    public void A_combined_fight_adds_up_the_mobs_and_counts_heals_once()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Ream");
        tracker.AddKnownPlayer("Kel");
        tracker.OnLogOwner("Ream");
        tracker.OnCombatEvent(Hit("Ream", "a frost giant", 300, 0));
        tracker.OnCombatEvent(Hit("Kel", "an ice goblin", 50, 1));
        tracker.OnCombatEvent(Hit("Ream", "an ice goblin", 25, 2));
        tracker.OnNonCombatMessage("Kel healed Ream for 200 hit points by Healing.");

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        Assert.Equal("a frost giant +1", fight.TargetName);
        Assert.Equal(375, fight.TotalDamage);
        Assert.Equal((325L, 50L), (fight.Participants.Single(p => p.Name == "Ream").TotalDamage, fight.Participants.Single(p => p.Name == "Kel").TotalDamage));
        Assert.Equal(200, Assert.Single(fight.HealingRows).Effective);
    }
}
