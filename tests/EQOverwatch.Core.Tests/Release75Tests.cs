using System.Globalization;
using EQOverwatch.Core.History;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release75Tests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 20, 0, 0);

    private static BossFight Kill(double seconds, long damage, int n, bool killed = true, string mob = "a temple guard", string? server = "frostreaver") =>
        new(mob, T0.AddMinutes(n), seconds, damage, 6, 0, killed, "Ream", server);

    [Fact]
    public void Regen_and_hit_points_come_from_kills_of_different_lengths()
    {
        // 10,000 hit points and 20 a second: a kill of t seconds takes 10,000 + 20t.
        var fights = new List<BossFight>
        {
            Kill(20, 10_400, 1), Kill(40, 10_800, 2), Kill(60, 11_200, 3), Kill(90, 11_800, 4), Kill(120, 12_400, 5),
            Kill(30, 4_000, 6),            // the logs saw only part of this kill: left out
            Kill(50, 3_000, 7, killed: false), // not a kill
            Kill(60, 99_000, 8, mob: "someone else"),
        };

        MobRegen regen = MobRegenEstimator.Estimate(fights, "A Temple Guard", "frostreaver")!;
        Assert.Equal(20, regen.RegenPerSecond, 3);
        Assert.Equal(10_000, regen.HitPoints);
        Assert.Equal(5, regen.Kills);
    }

    [Fact]
    public void No_estimate_with_too_few_kills_too_little_spread_or_kills_that_disagree()
    {
        Assert.Null(MobRegenEstimator.Estimate(new[] { Kill(20, 10_400, 1), Kill(80, 11_600, 2) }, "a temple guard"));
        Assert.Null(MobRegenEstimator.Estimate(new[] { Kill(20, 10_400, 1), Kill(30, 10_600, 2), Kill(40, 10_800, 3) }, "a temple guard")); // 20 s spread
        Assert.Null(MobRegenEstimator.Estimate(new[] { Kill(20, 10_000, 1), Kill(60, 16_000, 2), Kill(100, 10_500, 3), Kill(140, 17_000, 4) }, "a temple guard"));
        Assert.Null(MobRegenEstimator.Estimate(new[] { Kill(20, 10_400, 1), Kill(60, 11_200, 2), Kill(120, 12_400, 3) }, "a temple guard", "other server"));
    }

    [Fact]
    public void Regen_never_goes_below_zero_and_reads_short()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            MobRegen none = MobRegenEstimator.Estimate(new[] { Kill(20, 15_620, 1), Kill(60, 15_600, 2), Kill(100, 15_580, 3) }, "a temple guard")!;
            Assert.Equal(0, none.RegenPerSecond);
            Assert.Equal("Regen ~0/s · HP ~15.6k", none.Describe());
            Assert.Equal("Regen ~45/s · HP ~1.2M", new MobRegen(45.2, 1_180_000, 6).Describe());
            Assert.Equal("~2.4/s", new MobRegen(2.4, 950, 3).RegenText);
            Assert.Equal("~950", new MobRegen(2.4, 950, 3).HpText);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
