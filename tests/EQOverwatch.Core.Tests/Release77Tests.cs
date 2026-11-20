using EQOverwatch.Core.History;
using EQOverwatch.Core.Pipeline;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release77Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_character_is_active_only_on_the_server_its_log_is_from()
    {
        var activity = new CharacterActivityService();
        ((ILogActivityObserver)activity).OnLogLine("Ream", "vaniki", T0);

        Assert.True(activity.IsActive("Ream", "vaniki", T0.AddSeconds(5)));
        Assert.False(activity.IsActive("Ream", "frostreaver", T0.AddSeconds(5)));
        Assert.True(activity.IsActive("Ream", T0.AddSeconds(5))); // overlays: any server
        Assert.True(activity.IsActive("Ream", null, T0.AddSeconds(5)));
        Assert.False(activity.IsActive("Ream", "vaniki", T0.Add(CharacterActivityService.ActiveWindow).AddSeconds(1)));
    }

    [Fact]
    public void Regen_comes_from_kills_on_every_server()
    {
        BossFight Kill(double seconds, long damage, int n, string server) =>
            new("Xi Vius", T0.AddMinutes(n), seconds, damage, 6, 0, true, "Ream", server);
        var fights = new[] { Kill(20, 10_400, 1, "frostreaver"), Kill(60, 11_200, 2, "vaniki"), Kill(120, 12_400, 3, "vaniki") };

        MobRegen regen = MobRegenEstimator.Estimate(fights, "Xi Vius")!;
        Assert.Equal((20.0, 10_000L, 3), (Math.Round(regen.RegenPerSecond, 3), regen.HitPoints, regen.Kills));
    }
}
