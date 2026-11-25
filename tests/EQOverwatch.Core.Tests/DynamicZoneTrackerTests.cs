using EQOverwatch.Core.Progress;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class DynamicZoneTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Joining_starts_a_six_hour_timer_shared_by_boxes_and_leaving_ends_it()
    {
        var dz = new DynamicZoneTracker();
        dz.OnLine("Tankard", "Tankard has been added to Vex Thal.", T0);
        dz.OnLine("Mendwell", "Mendwell has been added to Vex Thal.", T0.AddMinutes(2));
        dz.OnLine("Tankard", "Porter has been added to Vex Thal.", T0.AddMinutes(3)); // someone else's line in Tankard's log
        dz.OnLine("Tankard", "Trakanon Idol has been added to your key ring.", T0);

        DynamicZoneTimer timer = Assert.Single(dz.Current(T0.AddHours(1)));
        Assert.Equal("Vex Thal", timer.Zone);
        Assert.Equal(T0.AddHours(6), timer.ClosesUtc); // from the first to join
        Assert.Equal(new[] { "Tankard", "Mendwell" }, timer.Members);
        Assert.Equal(TimeSpan.FromHours(5), timer.Remaining(T0.AddHours(1)));

        dz.OnLine("Tankard", "Tankard has been removed from Vex Thal.", T0.AddHours(2));
        Assert.Single(dz.Current(T0.AddHours(2)));
        dz.OnLine("Mendwell", "Mendwell has been removed from Vex Thal.", T0.AddHours(2));
        Assert.Empty(dz.Current(T0.AddHours(2)));
    }

    [Fact]
    public void A_timer_runs_out_and_is_kept_across_restarts()
    {
        string path = Path.Combine(Path.GetTempPath(), "eqo-dz-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var dz = new DynamicZoneTracker();
            dz.SetStoragePath(path);
            DateTime now = DateTime.UtcNow;
            dz.Join("Skyshrine", "Shieldwall", now.AddHours(-1));

            var again = new DynamicZoneTracker();
            again.SetStoragePath(path);
            DynamicZoneTimer timer = Assert.Single(again.Current(now));
            Assert.Equal("Skyshrine", timer.Zone);
            Assert.Empty(again.Current(now.AddHours(5).AddSeconds(1)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
