using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Models;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class TimelineExtrasTests
{
    private static ValueList<double> V(params double[] values) => new(values);

    private static DpsTimeline Timeline(params double[] group) =>
        new DpsTimeline(10, V(group), new ValueList<TimelineSeries>(Array.Empty<TimelineSeries>())) { DurationSeconds = group.Length * 10 };

    [Fact]
    public void BossHpFallsWithDamageAndRisesWithRegen()
    {
        // 100 DPS for 40 s against 4,000 HP with no regen: 75%, 50%, 25%, 0%.
        var hp = Timeline(100, 100, 100, 100).WithBossHp(4000, 0).BossHp!;
        Assert.Equal(new[] { 0.75, 0.5, 0.25, 0.0 }, hp.Select(v => Math.Round(v, 3)));

        // 10/s regen gives back 100 per bucket.
        var regen = Timeline(100, 100, 100, 100).WithBossHp(4000, 10).BossHp!;
        Assert.Equal(new[] { 0.775, 0.55, 0.325, 0.1 }, regen.Select(v => Math.Round(v, 3)));

        Assert.Null(Timeline(100).WithBossHp(0, 5).BossHp);
    }

    [Fact]
    public void BurnsAreStretchesWellAboveAverage()
    {
        // Average 125: buckets at 200+ (1.6x) are burns, 150 (1.2x) is not.
        var burns = Timeline(50, 200, 220, 50, 150, 80).Burns();
        Assert.Equal(new[] { (1, 2) }, burns);
        Assert.Empty(Timeline(0, 0, 0).Burns());
    }

    [Fact]
    public void StandingsReplayTheTableAtAnyPoint()
    {
        var timeline = Timeline(100, 100, 100, 100) with
        {
            Damage = new ValueList<TimelineSeries>(new[]
            {
                new TimelineSeries("Tankard", V(500, 500, 0, 0)),
                new TimelineSeries("Mendwell", V(0, 200, 800, 1000)),
            }),
        };

        var early = timeline.Standings(15);
        Assert.Equal(("Tankard", 750L, 50.0), early[0]);
        Assert.Equal(("Mendwell", 100L, 100 / 15.0), early[1]);

        var end = timeline.Standings(40);
        Assert.Equal("Mendwell", end[0].Name);
        Assert.Equal(2000, end[0].Damage);
        Assert.Equal(1000, end[1].Damage);

        Assert.Empty(timeline.Standings(0));
    }

    [Fact]
    public void FightRecordsHealingAndEveryAttackersDamage()
    {
        var start = new DateTime(2026, 11, 21, 20, 0, 0);
        var fight = new CombatEncounter(1, "a training dummy", start);
        for (int s = 0; s < 20; s += 2)
        {
            fight.AddDamage(start.AddSeconds(s), "Tankard", 100);
            fight.AddDamage(start.AddSeconds(s), "Gearbox", 50);
        }

        fight.AddHeal("Mendwell", "Complete Heal", 900, 900, timestamp: start.AddSeconds(5));
        DpsTimeline timeline = fight.BuildTimeline()!;

        Assert.Equal(900, timeline.Healing!.Sum() * timeline.BucketSeconds, 3);
        Assert.Equal(1000, timeline.Damage!.Single(d => d.Name == "Tankard").Dps.Sum());
        Assert.Equal(500, timeline.Damage!.Single(d => d.Name == "Gearbox").Dps.Sum());
    }

    [Fact]
    public void DamageTakenLinesFollowEachCharacterAndHealsFollowTheTank()
    {
        var start = new DateTime(2026, 11, 21, 20, 0, 0);
        var fight = new CombatEncounter(1, "a training dummy", start);
        for (int s = 0; s <= 120; s += 2)
        {
            fight.AddDamage(start.AddSeconds(s), "Gearbox", 100);
            if (s < 60)
            {
                fight.AddDamageTaken("Tankard", 300, timestamp: start.AddSeconds(s), isMelee: true);
            }
            else
            {
                fight.AddDamageTaken("Shieldwall", 300, timestamp: start.AddSeconds(s), isMelee: true);
            }

            // rampage hits everybody: never its own line
            fight.AddDamageTaken("Porter", 50, isRampage: true, timestamp: start.AddSeconds(s), isMelee: true);
        }

        // Gearbox takes damage twice, 40 s apart: two pieces, each rounded out to 10-second marks.
        fight.AddDamageTaken("Gearbox", 80, timestamp: start.AddSeconds(23), isMelee: true);
        fight.AddDamageTaken("Gearbox", 80, timestamp: start.AddSeconds(27), isMelee: true);
        fight.AddDamageTaken("Gearbox", 80, timestamp: start.AddSeconds(67), isMelee: true);

        fight.AddHeal("Mendwell", "Complete Heal", 9000, 9000, timestamp: start.AddSeconds(15), target: "Tankard");
        fight.AddHeal("Mendwell", "Complete Heal", 9000, 9000, timestamp: start.AddSeconds(25), target: "Shieldwall");
        fight.AddHeal("Mendwell", "Complete Heal", 9000, 9000, timestamp: start.AddSeconds(85), target: "Shieldwall");
        fight.AddHeal("Mendwell", "Celestial Regen", 300, 300, timestamp: start.AddSeconds(16), target: "Tankard", overTime: true);

        DpsTimeline timeline = fight.BuildTimeline()!;
        var taken = timeline.Taken!;

        Assert.DoesNotContain(taken, t => t.Name == "Porter");
        Assert.Equal(new TimelineSpan(0, 60), taken.Single(t => t.Name == "Tankard").Spans.Single());
        Assert.Equal(new TimelineSpan(60, 121), taken.Single(t => t.Name == "Shieldwall").Spans.Single());
        Assert.Equal(new[] { new TimelineSpan(20, 30), new TimelineSpan(60, 70) }, taken.Single(t => t.Name == "Gearbox").Spans);
        Assert.Equal(new[] { 15.0, 85.0 }, timeline.TankHeals!);
    }

    [Fact]
    public void MapPackRecordKnowsWhenANewerPackIsOut()
    {
        var none = new MapPackRecord();
        Assert.False(none.IsOutdated("maps-2026-12-01"));

        var old = new MapPackRecord { Version = "2026-11-21" };
        old.Files["a.txt"] = new string('a', 64);
        Assert.Equal("maps-2026-11-21", old.InstalledTag);
        Assert.False(old.IsOutdated("maps-2026-11-21"));
        Assert.True(old.IsOutdated("maps-2026-12-01"));

        old.Tag = "maps-2026-12-01";
        Assert.False(old.IsOutdated("maps-2026-12-01"));
    }
}
