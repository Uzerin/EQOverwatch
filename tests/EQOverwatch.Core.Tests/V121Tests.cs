using EQOverwatch.Core.History;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V121Tests
{
    [Fact]
    public void The_hp_estimate_uses_the_top_five_or_top_half_of_the_kills_by_damage()
    {
        // HP 1,000,000 and regen 200/s; the last four kills missed damage (players out of range).
        var kills = new List<(double Seconds, double Damage)>
        {
            (300, 1_060_000), (400, 1_080_000), (500, 1_100_000), (600, 1_120_000), (700, 1_140_000), (800, 1_160_000),
            (350, 700_000), (450, 650_000), (550, 900_000), (650, 800_000),
        };

        MobRegen estimate = Assert.IsType<MobRegen>(MobRegenEstimator.Estimate(kills));
        Assert.Equal(5, estimate.Kills); // ten kills: the top half and the top five are both 5
        Assert.Equal(200, estimate.RegenPerSecond, 3);
        Assert.Equal(1_000_000, estimate.HitPoints);
    }
}
