using EQOverwatch.Core.Spells;
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

public sealed class AaAndActiveTimeTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Learned_abilities_come_from_the_log_and_edits_last_until_learned_again()
    {
        var store = new EQOverwatch.Core.Progress.ProgressStore(T0.AddDays(-1));
        void Line(string text, int minute) => store.OnLine("Tankard", "frostreaver", text, T0.AddMinutes(minute));
        Line("[60 Paladin] Tankard (Ogre) <Gearbox>", 0);
        Line("You have gained the ability \"Lay on Hands\" at a cost of 0 ability points.", 1);
        Line("You have improved Healing Adept 2 at a cost of 3 ability points.", 2);
        Line("You have improved Healing Adept 3 at a cost of 6 ability points.", 3);

        var rows = store.Aas("Tankard", "frostreaver");
        Assert.Equal(new[] { ("Healing Adept", 3, 9), ("Lay on Hands", 1, 0) }, rows.Select(r => (r.Name, r.Rank, r.Points)));

        store.SetAa("Tankard", "frostreaver", null, "Healing Adept", 5, T0.AddMinutes(4));
        store.SetAa("Tankard", "frostreaver", null, "Mental Clarity", 2, T0.AddMinutes(4));
        Assert.Equal(5, store.AaRanks("Tankard", "frostreaver")["Healing Adept"]);
        Assert.Equal("Added by you", store.Aas("Tankard", "frostreaver").Single(r => r.Name == "Mental Clarity").Source);

        Line("You have improved Healing Adept 4 at a cost of 6 ability points.", 5); // learned again after the edit: the log wins
        var adept = store.Aas("Tankard", "frostreaver").Single(r => r.Name == "Healing Adept");
        Assert.Equal((4, "Log"), (adept.Rank, adept.Source));
    }

    [Fact]
    public void A_learned_rank_lengthens_the_spells_it_names()
    {
        var bonuses = AaDurations.For(new Dictionary<string, int> { ["Improved Thief's Eyes"] = 7, ["Not An AA"] = 3 });
        AaDurationBonus eyes = Assert.Single(bonuses);
        Assert.Equal((540, "Improved Thief's Eyes 7"), (eyes.Seconds, eyes.Source));

        var spell = new SpellInfo(8001, "Thief's Eyes", "", "", "", 0, 3, 10, 60, ClassLevels: Enumerable.Repeat(255, 8).Append(65).Concat(Enumerable.Repeat(255, 7)).ToArray(),
            SpellLine: 100090260);
        var profile = new DurationProfile(null, null, 0, bonuses);
        Assert.Equal(600, DurationCalculator.Seconds(spell, 8, profile, SpellDatabase.Empty));
        Assert.Equal("+540 s from Improved Thief's Eyes 7", DurationCalculator.AaBonusText(spell, 8, profile));
        Assert.Equal(60, DurationCalculator.Seconds(spell with { SpellLine = 1 }, 8, profile, SpellDatabase.Empty));
    }
}

public sealed class ItemFocusTests
{
    [Fact]
    public void A_focus_row_gives_its_kind_size_and_limits()
    {
        // Extended Range III: 1|129|15  2|134|60|5  3|311
        var effects = SpellEffects.Parse("1|129|15|0|100|0$2|134|60|5|100|0$3|311|0|0|100|0");
        ItemFocus range = Assert.Single(ItemFocus.FromEffects(2350, "Extended Range III", effects));
        Assert.Equal((FocusKind.Range, 15, 60, 5, true), (range.Kind, range.Percent, range.MaxLevel, range.PercentPerLevelOver, range.ExcludesCombatSkills));
        Assert.Equal("Extended Range III (+15% range, to level 60)", range.Label);
        Assert.Equal(15, range.PercentAt(60));
        Assert.Equal(11.25, range.PercentAt(65), 2);
        Assert.Equal("+15% range on spells up to level 60; not combat abilities", range.BenefitAt(60));
        Assert.StartsWith("+15% range on spells up to level 60; your level 65 spells get ", range.BenefitAt(65));

        var both = ItemFocus.FromEffects(1, "Extended Enhancement I", SpellEffects.Parse("1|128|10|0|100|0"));
        Assert.Equal(new[] { FocusKind.BuffDuration, FocusKind.DetrimentalDuration }, both.Select(f => f.Kind));
    }

    [Fact]
    public void Item_focus_messages_are_counted_per_character()
    {
        Assert.Equal(("Meditative Blanket", "feels alive with power."), EQOverwatch.Core.Progress.ProgressStore.ItemFocusLine("Your Meditative Blanket feels alive with power."));
        Assert.Null(EQOverwatch.Core.Progress.ProgressStore.ItemFocusLine("You feel focused."));
        Assert.Equal(FocusKind.Healing, FocusKinds.FromItemMessage("feels alive with power."));
        Assert.Null(FocusKinds.FromItemMessage("sparkles.")); // not confirmed on live yet

        var store = new EQOverwatch.Core.Progress.ProgressStore(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        for (int i = 0; i < 3; i++)
        {
            store.OnLine("Mendwell", "frostreaver", "Your Meditative Blanket feels alive with power.", new DateTime(2026, 11, 24, 20, 0, i, DateTimeKind.Utc));
        }

        Assert.Equal(("Meditative Blanket", "feels alive with power.", 3), Assert.Single(store.FocusFires("Mendwell", "frostreaver")));
    }
}
