using EQOverwatch.Core.Models;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class CombinedFightsPickTests
{
    private static readonly DateTime T0 = new(2026, 11, 22, 19, 0, 0, DateTimeKind.Utc);

    private static EncounterSnapshot Fight(int id, string mob, int startSeconds, int seconds, params (string Name, long Damage)[] dealers)
    {
        long total = dealers.Sum(d => d.Damage);
        var participants = dealers.Select(d => new ParticipantSnapshot(d.Name, d.Damage, 100.0 * d.Damage / total, d.Damage / (double)seconds, d.Damage / (double)seconds, 100, 10, 0, 1.0)).ToArray();
        return new EncounterSnapshot(id, mob, T0.AddSeconds(startSeconds), T0.AddSeconds(startSeconds + seconds), seconds, total, false, participants,
            Healing: new[] { new HealingSnapshot("Mendwell", 1000, 250, 20.0, 4, 400, 1000.0 / seconds, "Complete Heal") });
    }

    [Fact]
    public void Picked_fights_add_up_and_dps_uses_only_the_time_in_combat()
    {
        // 20 s fight, a 100 s walk, then a 10 s fight: 30 s in combat.
        EncounterSnapshot a = Fight(1, "a blood spider", 0, 20, ("Tankard", 4000), ("Porter", 2000));
        EncounterSnapshot b = Fight(2, "a bog snake", 120, 10, ("Tankard", 2000));

        EncounterSnapshot combined = CombinedFights.Combine(new[] { a, b });

        Assert.Equal("Combined (2)", combined.TargetName);
        Assert.Equal(2, combined.CombinedCount);
        Assert.Equal(30, combined.DurationSeconds, 3);
        Assert.Equal(8000, combined.TotalDamage);
        Assert.Equal(6000, combined.Participants[0].TotalDamage);
        Assert.Equal(200, combined.Participants[0].ScaledDps, 3);
        Assert.Equal("Combined (2) - Total Damage: 8,000 - DPS: 267", CombinedFights.Header(combined));
        Assert.Equal(CombinedFights.Header(combined), EQOverwatch.Core.Export.EncounterExport.BuildHeader(combined));

        HealingSnapshot heals = Assert.Single(combined.HealingRows);
        Assert.Equal((2000L, 500L, 8), (heals.Effective, heals.Overheal, heals.Heals)); // two separate fights: added up
    }

    [Fact]
    public void Mobs_fought_at_the_same_time_count_their_time_and_heals_once()
    {
        EncounterSnapshot a = Fight(1, "a vann militis", 0, 30, ("Tankard", 3000));
        EncounterSnapshot b = Fight(2, "a vann hellig", 10, 30, ("Tankard", 1500)); // overlaps a: 0 to 40 s

        EncounterSnapshot combined = CombinedFights.Combine(new[] { a, b });

        Assert.Equal(40, combined.DurationSeconds, 3);
        Assert.Equal(1000, Assert.Single(combined.HealingRows).Effective); // the same heals were filed under both mobs
    }

    [Fact]
    public void Graphs_are_put_back_to_back()
    {
        var first = new DpsTimeline(2, new ValueList<double>(new[] { 10.0, 10.0 }), new ValueList<TimelineSeries>(new[] { new TimelineSeries("Tankard", new ValueList<double>(new[] { 10.0, 10.0 })) })) { DurationSeconds = 4 };
        var second = new DpsTimeline(2, new ValueList<double>(new[] { 30.0, 30.0 }), new ValueList<TimelineSeries>(new[] { new TimelineSeries("Porter", new ValueList<double>(new[] { 30.0, 30.0 })) })) { DurationSeconds = 4 };

        DpsTimeline joined = Assert.IsType<DpsTimeline>(DpsTimeline.Concat(new DpsTimeline?[] { first, null, second }));

        Assert.Equal(8, joined.Span);
        Assert.Equal(new[] { 10.0, 10.0, 30.0, 30.0 }, joined.Group);
        Assert.Equal(new[] { "Porter", "Tankard" }, joined.Players.Select(p => p.Name));
        Assert.Equal(new[] { 0.0, 0.0, 30.0, 30.0 }, joined.Players[0].Dps);
    }
}
