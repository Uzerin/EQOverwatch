using EQOverwatch.Core.Models;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class EncounterModelTests
{
    [Fact]
    public void Recalculate_computes_percent_dps_scaled_dps_and_hit_rate()
    {
        var t0 = new DateTime(2026, 10, 21, 11, 45, 0);
        var encounter = new CombatEncounter(1, "a frost giant", t0);

        encounter.AddDamage(t0, "Stackprime", 100);
        encounter.AddMiss(t0.AddSeconds(1), "Stackprime");
        encounter.AddDamage(t0.AddSeconds(2), "Oodles", 100);
        encounter.AddDamage(t0.AddSeconds(4), "Stackprime", 300);
        encounter.Close(t0.AddSeconds(4));

        Assert.False(encounter.IsActive);
        Assert.Equal(4.0, encounter.DurationSeconds);
        Assert.Equal(500, encounter.TotalDamage);

        ParticipantStats sp = encounter.ParticipantStats.Single(p => p.Name == "Stackprime");
        Assert.Equal(400, sp.TotalDamage);
        Assert.Equal(300, sp.MaxHit);
        Assert.Equal(80.0, sp.PercentOfGroupDamage, 3);
        Assert.Equal(100.0, sp.Dps, 3);        // 400 over its own 4s window
        Assert.Equal(100.0, sp.ScaledDps, 3);  // 400 over the 4s encounter
        Assert.Equal(2.0 / 3.0, sp.HitRate, 3);

        ParticipantStats oodles = encounter.ParticipantStats.Single(p => p.Name == "Oodles");
        Assert.Equal(100.0, oodles.Dps, 3);       // single action: 1s minimum window
        Assert.Equal(25.0, oodles.ScaledDps, 3);  // 100 over the 4s encounter
    }
}
