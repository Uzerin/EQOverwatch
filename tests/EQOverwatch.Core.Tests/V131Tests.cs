using EQOverwatch.Core.Maps;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>1.0.131: possible spawn locations for each zone, and Verified spots saved on this PC.</summary>
public sealed class V131Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc131-" + Guid.NewGuid().ToString("N"));

    public V131Tests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Dungeons_have_fixed_spots_and_outdoor_zones_keep_spots_only_for_named_mobs()
    {
        Assert.True(SpawnLocations.Zones.Count > 300);

        SpawnZone permafrost = SpawnLocations.Find("permafrost")!;
        Assert.False(permafrost.IsOutdoor);
        SpawnMob vox = permafrost.Mobs.Single(m => m.Name == "Lady Vox");
        Assert.True(vox.IsNamed);
        Assert.NotEmpty(vox.Spots!);

        SpawnZone skyfire = SpawnLocations.Find("SKYFIRE")!;
        Assert.True(skyfire.IsOutdoor);
        Assert.NotEmpty(skyfire.Mobs.Single(m => m.Name == "Eldrig the Old").Spots!);
        Assert.Contains(skyfire.Mobs, m => !m.IsNamed && m.Spots is null); // random spots
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(400, "6:40")]
    [InlineData(4320, "1:12:00")]
    [InlineData(86400, "1 day")]
    [InlineData(259200, "3 days")]
    public void Respawn_times_read_like_timers(int seconds, string text) => Assert.Equal(text, SpawnMob.FormatRespawn(seconds));

    [Fact]
    public void Spots_read_in_loc_order_and_verified_spots_are_kept()
    {
        var spot = new SpawnSpot(1768, -3849, -160);
        Assert.Equal("1768, -3849, -160", spot.LocText);
        Assert.Equal("/waypoint 1768 -3849 -160", spot.Waypoint);
        Assert.Equal("51-55", new SpawnMob("a wurm", 51, 55, 400, null).LevelText);

        string path = Path.Combine(_dir, "verifiedspawns.json");
        var store = new VerifiedSpawnStore();
        store.SetStoragePath(path);
        store.Set("skyfire", "Eldrig the Old", spot, true);
        var reloaded = new VerifiedSpawnStore();
        reloaded.SetStoragePath(path);
        Assert.True(reloaded.IsVerified("skyfire", "Eldrig the Old", spot));
        reloaded.Set("skyfire", "Eldrig the Old", spot, false);
        Assert.False(reloaded.IsVerified("skyfire", "Eldrig the Old", spot));
    }
}
