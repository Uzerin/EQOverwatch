using EQOverwatch.Core.Drops;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>The drop table: kills and drops per server, each counted once however many logs saw it.</summary>
public sealed class DropStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqdrops-" + Guid.NewGuid().ToString("N"));

    public DropStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static DropStore Store(string zone = "Crushbone")
    {
        var store = new DropStore();
        store.SetZoneProvider((_, _) => zone);
        store.SetPlayerProbe(name => name is "Stackprime" or "Ream" or "Jannder");
        return store;
    }

    private static void Line(DropStore store, string character, string message, int second, string server = "frostreaver") =>
        store.OnLine(character, server, message, T0.AddSeconds(second));

    [Fact]
    public void Kills_and_drops_make_a_drop_rate_per_mob()
    {
        DropStore store = Store();
        for (int i = 0; i < 4; i++)
        {
            Line(store, "Ream", "Orc pawn was slain by Jannder!", i * 20);
            Line(store, "Ream", "--Jannder has looted an Orc Scalp from orc pawn's corpse.--", i * 20 + 3);
        }

        Line(store, "Ream", "--You have looted a Torch from orc pawn's corpse.--", 63);
        Line(store, "Ream", "--You have looted 3 Bone Chips from a decaying skeleton's corpse.--", 90);
        Line(store, "Ream", "--You have looted 2 platinum from orc pawn's corpse.--", 64); // coin is left out
        Line(store, "Ream", "Stackprime has been slain by orc pawn!", 100); // a player's death is no kill

        DropMobRow pawn = store.Table("frostreaver", includeImported: true).First();
        Assert.Equal(("Crushbone", "orc pawn", 4), (pawn.Zone, pawn.Mob, pawn.Kills));
        Assert.Equal(new[] { new DropItemRow("Orc Scalp", 4), new DropItemRow("Torch", 1) }, pawn.Items);

        // A corpse looted whose kill no log saw still counts as a kill.
        DropMobRow skeleton = store.Table("frostreaver", true).Single(r => r.Mob == "a decaying skeleton");
        Assert.Equal(1, skeleton.Kills);
        Assert.Equal("Bone Chips", skeleton.Items.Single().Item);
    }

    [Fact]
    public void One_kill_that_drops_several_of_an_item_is_still_one_kill()
    {
        DropStore store = Store();
        foreach (string log in new[] { "Ream", "Stackprime" })
        {
            Line(store, log, "Diabo Xi Va was slain by Jannder!", 0);
            for (int i = 0; i < 4; i++)
            {
                Line(store, log, "--Jannder has looted a Bracer of the Silent Star from Diabo Xi Va's corpse.--", 60 + i / 2);
            }

            Line(store, log, "--Jannder has looted a Cap of the Silent Star from Diabo Xi Va's corpse.--", 62);
        }

        // Two corpses looted 10 minutes apart with no kill line seen are two kills.
        Line(store, "Ream", "--You have looted a Torch from orc pawn's corpse.--", 0);
        Line(store, "Ream", "--You have looted a Torch from orc pawn's corpse.--", 2);
        Line(store, "Ream", "--You have looted a Torch from orc pawn's corpse.--", 600);

        IReadOnlyList<DropMobRow> table = store.Table("frostreaver", true);
        DropMobRow diabo = table.Single(r => r.Mob == "Diabo Xi Va");
        Assert.Equal(1, diabo.Kills);
        Assert.Equal(new[] { new DropItemRow("Bracer of the Silent Star", 4), new DropItemRow("Cap of the Silent Star", 1) }, diabo.Items);
        Assert.Equal((2, 3), (table.Single(r => r.Mob == "orc pawn").Kills, table.Single(r => r.Mob == "orc pawn").Items.Single().Drops));
    }

    [Fact]
    public void A_kill_and_drop_seen_by_several_logs_count_once_but_two_together_count_two()
    {
        DropStore store = Store();
        foreach (string log in new[] { "Ream", "Stackprime", "Oodles" })
        {
            Line(store, log, "Orc pawn was slain by Jannder!", 0);
            Line(store, log, "Orc pawn was slain by Jannder!", 1); // two pawns at once
            Line(store, log, "--Jannder has looted a Torch from orc pawn's corpse.--", 4);
        }

        Line(store, "Oodles", "--Oodles has looted a Torch from orc pawn's corpse.--", 5); // only Oodles saw the second Torch

        DropMobRow pawn = store.Table("frostreaver", true).Single();
        Assert.Equal(2, pawn.Kills);
        Assert.Equal(2, pawn.Items.Single().Drops);
    }

    [Fact]
    public void Servers_are_kept_apart()
    {
        DropStore store = Store();
        Line(store, "Ream", "--You have looted a Torch from orc pawn's corpse.--", 0, "frostreaver");
        Line(store, "Abundance", "--You have looted a Torch from orc pawn's corpse.--", 0, "oakwynd");
        Line(store, "Abundance", "--You have looted a Torch from orc pawn's corpse.--", 30, "oakwynd");

        Assert.Equal(new[] { "frostreaver", "oakwynd" }, store.Servers());
        Assert.Equal(1, store.Table("frostreaver", true).Single().Items.Single().Drops);
        Assert.Equal(2, store.Table("oakwynd", true).Single().Items.Single().Drops);
    }

    [Fact]
    public void An_advanced_loot_item_nobody_looted_counts_when_one_mob_was_just_killed()
    {
        DropStore store = Store();
        Line(store, "Ream", "A ghoul was slain by Stackprime!", 0);
        Line(store, "Ream", "Ream won the greed roll on 1 item(s): Battleworn Canteen with a roll of 983.", 2);
        Line(store, "Ream", "--You have looted a Battleworn Canteen from a ghoul's corpse.--", 4); // looted: counted once, from this line
        Line(store, "Ream", "You cannot choose to need or greed on Lucid Shard because you already have one and it is lore.", 5);
        Line(store, "Ream", "You cannot choose to need or greed on Lucid Shard because you already have one and it is lore.", 6);
        store.ResolveExpired(T0.AddSeconds(30));

        DropMobRow ghoul = store.Table("frostreaver", true).Single();
        Assert.Equal("a ghoul", ghoul.Mob);
        Assert.Equal(1, ghoul.Kills);
        Assert.Equal(new[] { new DropItemRow("Battleworn Canteen", 1), new DropItemRow("Lucid Shard", 1) }, ghoul.Items);

        // Two kinds of mob killed just before: it can't be told which one dropped it, so it isn't counted.
        DropStore mixed = Store();
        Line(mixed, "Ream", "A ghoul was slain by Stackprime!", 0);
        Line(mixed, "Ream", "A skeleton was slain by Stackprime!", 1);
        Line(mixed, "Ream", "You cannot choose to need or greed on Lucid Shard because you already have one and it is lore.", 3);
        mixed.ResolveExpired(T0.AddSeconds(30));
        Assert.Empty(mixed.Table("frostreaver", true));
    }

    [Fact]
    public void Log_history_is_read_with_its_zones_and_the_table_is_kept_between_runs()
    {
        string log = Path.Combine(_dir, "eqlog_Ream_frostreaver.txt");
        File.WriteAllLines(log, new[]
        {
            "[Wed May 27 12:50:00 2026] You have entered Clan Crushbone.",
            "[Wed May 27 12:53:20 2026] Orc pawn was slain by Jannder!",
            "[Wed May 27 12:53:22 2026] --Jannder has looted a Rusty Two Handed Sword from orc pawn's corpse.--",
            "[Wed May 27 12:53:22 2026] --Jannder has looted a Torch from orc pawn's corpse.--",
            "[Wed May 27 13:10:00 2026] You have entered Befallen.",
            "[Wed May 27 13:10:30 2026] You have slain a decaying skeleton!",
            "[Wed May 27 13:10:31 2026] --You have looted 3 Bone Chips from a decaying skeleton's corpse.--",
        });

        string storePath = Path.Combine(_dir, "drops.json");
        DropStore store = Store(zone: "Live zone");
        store.SetStoragePath(storePath);
        Assert.False(store.IsScanned("Ream", "frostreaver"));
        Assert.True(store.ScanLog(log, "Ream", "frostreaver"));
        Assert.True(store.ScanLog(log, "Ream", "frostreaver")); // reading again changes nothing
        Assert.True(store.IsScanned("Ream", "frostreaver"));

        DropStore reloaded = Store();
        reloaded.SetStoragePath(storePath);
        Assert.Equal(store.Tag, reloaded.Tag);
        Assert.True(reloaded.IsScanned("Ream", "frostreaver"));
        IReadOnlyList<DropMobRow> rows = reloaded.Table("frostreaver", true);
        Assert.Equal(("Clan Crushbone", 1, 2), (rows.Single(r => r.Mob == "orc pawn").Zone, rows.Single(r => r.Mob == "orc pawn").Kills, rows.Single(r => r.Mob == "orc pawn").Items.Count));
        Assert.Equal("Befallen", rows.Single(r => r.Mob == "a decaying skeleton").Zone);
    }

    [Fact]
    public void A_drops_file_carries_one_servers_kills_and_drops_and_counts_a_shared_fight_once()
    {
        DropStore mine = Store();
        Line(mine, "Ream", "Orc pawn was slain by Jannder!", 0);
        Line(mine, "Ream", "--Jannder has looted a Torch from orc pawn's corpse.--", 2);
        Line(mine, "Ream", "--You have looted a Torch from orc pawn's corpse.--", 0, "oakwynd");

        string file = Path.Combine(_dir, "drops-frostreaver.eqodrops");
        Assert.Equal(1, mine.Export("frostreaver", file));
        string text = File.ReadAllText(file);
        Assert.DoesNotContain("Jannder", text, StringComparison.Ordinal); // no character names
        Assert.DoesNotContain("Ream", text, StringComparison.Ordinal);
        Assert.DoesNotContain("oakwynd", text, StringComparison.Ordinal);

        // A friend was at the same fight (clock a second off) and at another one.
        DropStore friend = Store();
        friend.SetTag("Kel-91B");
        Line(friend, "Kel", "Orc pawn was slain by Jannder!", 1);
        Line(friend, "Kel", "--Jannder has looted a Torch from orc pawn's corpse.--", 3);
        Line(friend, "Kel", "Orc pawn was slain by Kel!", 100);
        string theirs = Path.Combine(_dir, "kel.eqodrops");
        friend.Export("frostreaver", theirs);

        DropImportResult result = mine.Import(theirs);
        Assert.Equal(new DropImportResult("Kel-91B", "frostreaver", 2, 1, Replaced: false), result);
        DropMobRow pawn = mine.Table("frostreaver", includeImported: true).Single();
        Assert.Equal((2, 1), (pawn.Kills, pawn.Items.Single().Drops)); // the shared fight counts once
        Assert.Equal(1, mine.Table("frostreaver", includeImported: false).Single().Kills);
        Assert.Equal("Kel-91B", mine.Sources().Single().Tag);

        Assert.True(mine.Import(theirs).Replaced); // the same file again replaces, not adds
        Assert.Equal(2, mine.Table("frostreaver", true).Single().Kills);

        mine.RemoveSource("Kel-91B", "frostreaver");
        Assert.Empty(mine.Sources());
        Assert.Equal(1, mine.Table("frostreaver", true).Single().Kills);

        Assert.Throws<InvalidDataException>(() => mine.Import(file)); // your own export
        File.WriteAllText(Path.Combine(_dir, "junk.eqodrops"), "{\"Hello\":1}");
        Assert.Throws<InvalidDataException>(() => mine.Import(Path.Combine(_dir, "junk.eqodrops")));
    }

    [Fact]
    public void The_tag_is_random_and_can_be_changed_to_a_plain_name()
    {
        var store = new DropStore();
        Assert.Matches("^[A-Za-z]+-[0-9A-F]{3}$", store.Tag);
        Assert.True(store.SetTag("Guild Drops"));
        Assert.False(store.SetTag("bad/name"));
        Assert.False(store.SetTag(new string('x', 25)));
        Assert.Equal("Guild Drops", store.Tag);
    }
}
