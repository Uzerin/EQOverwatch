using EQOverwatch.Core.Maps;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>1.0.132: placeholders and spawn chance, the player's Named ticks, and con colors.</summary>
public sealed class V132Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc132-" + Guid.NewGuid().ToString("N"));

    public V132Tests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_named_spot_lists_its_placeholders_and_chance()
    {
        SpawnMob eldrig = SpawnLocations.Find("skyfire")!.Mobs.Single(m => m.Name == "Eldrig the Old");
        SpawnSpot spot = eldrig.Spots![0];
        Assert.InRange(spot.Chance, 1, 100);
        Assert.Contains("a chromadrac", spot.SharesWith!);
        Assert.DoesNotContain("Eldrig the Old", spot.SharesWith!);
        Assert.Equal("", new SpawnSpot(1, 2, 3).ChanceText);
        Assert.Equal("25%", new SpawnSpot(1, 2, 3, 25, new[] { "a wurm", "a wyvern" }).ChanceText);
        Assert.Equal("a wurm, a wyvern", new SpawnSpot(1, 2, 3, 25, new[] { "a wurm", "a wyvern" }).SharesWithText);
    }

    [Fact]
    public void Named_ticks_override_the_known_answer_and_are_kept()
    {
        string path = Path.Combine(_dir, "namedspawns.json");
        var wurm = new SpawnMob("a wurm", 58, 58, 400, null);
        var eldrig = new SpawnMob("Eldrig the Old", 51, 51, 640, null);
        var store = new NamedSpawnStore();
        store.SetStoragePath(path);
        Assert.False(store.IsNamed("skyfire", wurm));
        Assert.True(store.IsNamed("skyfire", eldrig));
        store.Set("skyfire", wurm, true);
        store.Set("skyfire", eldrig, false);

        var reloaded = new NamedSpawnStore();
        reloaded.SetStoragePath(path);
        Assert.True(reloaded.IsNamed("skyfire", wurm));
        Assert.False(reloaded.IsNamed("skyfire", eldrig));
        Assert.False(reloaded.IsNamed("dreadlands", wurm)); // per zone
    }

    [Theory]
    [InlineData(52, 52, ConColor.White)]
    [InlineData(52, 54, ConColor.Yellow)]
    [InlineData(52, 55, ConColor.Red)]
    [InlineData(52, 48, ConColor.Blue)]
    [InlineData(52, 40, ConColor.LightBlue)]
    [InlineData(52, 36, ConColor.Green)]
    [InlineData(52, 31, ConColor.Gray)]
    [InlineData(5, 1, ConColor.Gray)]
    [InlineData(0, 30, ConColor.None)]
    [InlineData(30, 0, ConColor.None)]
    public void Cons_follow_the_level_difference(int player, int mob, ConColor con) => Assert.Equal(con, Cons.For(player, mob));
}
