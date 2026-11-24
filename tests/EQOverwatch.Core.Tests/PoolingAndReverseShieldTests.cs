using System.IO.Compression;
using System.Text.Json;
using EQOverwatch.Core.Drops;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Tlp;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class PoolingAndReverseShieldTests
{
    [Fact]
    public void Only_servers_with_normal_loot_are_poolable()
    {
        string[] normal = TlpServers.All.Where(s => s.PlainLoot).Select(s => s.Key).Order().ToArray();
        Assert.Equal(new[] { "agnarr", "aradune", "tormax", "yelinak" }, normal);
        Assert.Equal("Teek", TlpServers.Find("teek")!.Name);
        Assert.Contains(TlpServers.Find("teek")!.Rules, r => r.Contains("Random loot", StringComparison.Ordinal));
        Assert.DoesNotContain(TlpServers.Find("tormax")!.Rules, r => r.Contains("Random loot", StringComparison.Ordinal));
    }

    [Fact]
    public void Vaniki_is_open_through_Shattering_of_Ro_with_its_cap_unchanged()
    {
        TlpServer vaniki = TlpServers.Find("vaniki")!;
        var today = new DateOnly(2026, 11, 23);
        Assert.Contains(33, vaniki.OpenExpansions(today));
        Assert.Equal(120, vaniki.LevelCap(today));
        Assert.Null(vaniki.Next(today));
    }

    [Fact]
    public void Reverse_damage_shield_heals_stay_out_of_the_floating_text()
    {
        var feed = new FloatingCombatFeed(name => name is "Stackprime");
        feed.OnLogOwner("Stackprime");
        feed.OnNonCombatMessage("Stackprime healed himself for 40 hit points by Mark of Karn.");
        feed.OnNonCombatMessage("You healed yourself for 40 hit points by Mark of Darkness.");
        feed.OnNonCombatMessage("Ream healed you for 1250 (8415) hit points by Complete Heal.");
        FloatingNumber only = Assert.Single(feed.TakeAll());
        Assert.Equal((FloatingPanel.Healed, 1250), (only.Panel, only.Amount));
        Assert.False(FloatingCombatFeed.IsReverseDamageShield("Naltron's Mark"));
    }

    [Fact]
    public void A_pooled_table_adds_the_other_servers_merged_drops()
    {
        var store = new DropStore();
        store.ImportMerged("tormax", 100, 3, Merged("tormax", kills: 10, drops: 2));
        store.ImportMerged("yelinak", 100, 2, Merged("yelinak", kills: 30, drops: 3));

        DropMobRow alone = Assert.Single(store.Table("tormax", DropScope.Everyone));
        Assert.Equal((10, 2), (alone.Kills, alone.Items.Single().Drops));
        DropMobRow pooled = Assert.Single(store.Table("tormax", DropScope.Everyone, new[] { "yelinak" }));
        Assert.Equal((40, 5), (pooled.Kills, pooled.Items.Single().Drops));
        Assert.Empty(store.Table("tormax", DropScope.Mine, new[] { "yelinak" })); // pooling is for shared drops only

        var servers = HttpSharedDropsService.ParseMergedServers("""{"servers":[{"s":"Tormax","p":3,"k":1200},{"p":1},{"s":"yelinak"}]}""");
        Assert.Equal(new[] { new MergedServer("tormax", 3, 1200), new MergedServer("yelinak", 0, 0) }, servers);
    }

    private static byte[] Merged(string server, int kills, int drops)
    {
        var file = new DropStore.MergedFile
        {
            Format = "EQOverwatchDropsMerged", Version = 1, Server = server, Updated = 100, Players = 1,
            Zones = { "Permafrost Caverns" }, Mobs = { "Lady Vox" }, Items = { "White Dragon Scale" },
            Kills = { new long[] { 0, 0, kills } }, Drops = { new long[] { 0, 0, 0, drops } },
        };
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            JsonSerializer.Serialize(gzip, file);
        }

        return output.ToArray();
    }
}
