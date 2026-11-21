using EQOverwatch.Core.Drops;
using EQOverwatch.Core.Loot;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>Loot is filed under the server of the log it came from, kept once, and read in every shape.</summary>
public sealed class LootServerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 21, 5, 30, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqo-lootsrv-" + Guid.NewGuid().ToString("N"));

    public LootServerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Live_loot_uses_the_logs_server_and_a_you_line_is_the_same_loot()
    {
        // The same name plays on two servers: the name alone would guess the wrong one.
        var loot = new LootTracker { ServerLookup = _ => "vaniki" };
        loot.OnLine("Shieldwall", "frostreaver", "--You have looted a Spell: Cloak of the Akheva from Diabo Xi Xin's corpse.--", Now);
        loot.OnLine("Mendwell", "frostreaver", "--Shieldwall has looted a Spell: Cloak of the Akheva from Diabo Xi Xin's corpse.--", Now.AddSeconds(1));
        loot.OnLine("Mendwell", "frostreaver", "--Gearbox has looted a Glowing Orb of Luclinite from Diabo Xi Xin.--", Now.AddSeconds(2));

        Assert.Equal(new[] { "Shieldwall|Spell: Cloak of the Akheva|Diabo Xi Xin|frostreaver", "Gearbox|Glowing Orb of Luclinite|Diabo Xi Xin|frostreaver" },
            loot.Entries.Select(e => $"{e.Looter}|{e.Item}|{e.Mob}|{e.Server}"));
    }

    [Fact]
    public void Reading_the_logs_again_puts_misfiled_and_doubled_loot_right()
    {
        string a = Path.Combine(_dir, "eqlog_Shieldwall_frostreaver.txt");
        string b = Path.Combine(_dir, "eqlog_Mendwell_frostreaver.txt");
        File.WriteAllLines(a, new[]
        {
            "[Fri Nov 20 21:29:51 2026] --Porter has looted a Cloak of the Black Wind from Diabo Xi Xin's corpse.--",
            "[Fri Nov 20 21:30:07 2026] --You have looted a Spell: Cloak of the Akheva from Diabo Xi Xin's corpse.--",
        });
        File.WriteAllLines(b, new[]
        {
            "[Fri Nov 20 21:29:51 2026] --Porter has looted a Cloak of the Black Wind from Diabo Xi Xin's corpse.--",
            "[Fri Nov 20 21:30:07 2026] Shieldwall won the need roll on 1 item(s): Spell: Cloak of the Akheva with a roll of 981.",
            "[Fri Nov 20 21:30:07 2026] --Shieldwall has looted a Spell: Cloak of the Akheva from Diabo Xi Xin's corpse.--",
        });

        var oldLive = new DateTime(2026, 11, 20, 21, 29, 52);
        File.WriteAllText(Path.Combine(_dir, "loot.json"), System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new LootEntry(oldLive, "Porter", "Cloak of the Black Wind", "Diabo Xi Xin", Server: "vaniki"),
            new LootEntry(oldLive.AddSeconds(16), "Shieldwall", "Spell: Cloak of the Akheva", "Diabo Xi Xin", "need", 981, "vaniki"),
            new LootEntry(oldLive.AddSeconds(16), "Shieldwall", "Spell: Cloak of the Akheva", "Diabo Xi Xin", Server: "frostreaver"),
        }));
        var loot = new LootTracker();
        loot.SetStoragePath(Path.Combine(_dir, "loot.json"));
        Assert.Equal(3, loot.Entries.Count);

        Assert.True(loot.Repair(new[] { a, b }, new DateTime(2026, 11, 14)) > 0);
        Assert.Equal(new[] { "Cloak of the Black Wind|frostreaver|0", "Spell: Cloak of the Akheva|frostreaver|981" },
            loot.Entries.Select(e => $"{e.Item}|{e.Server}|{e.Roll}"));
        Assert.Equal(0, loot.Repair(new[] { a, b }, new DateTime(2026, 11, 14)));
    }

    [Fact]
    public void Stacks_items_left_on_the_corpse_and_loot_without_corpse_count_as_drops()
    {
        var store = new DropStore();
        DateTime t = Now;
        store.OnLine("Mendwell", "frostreaver", "Diabo Xi Xin was slain by Gearbox!", t);
        store.OnLine("Mendwell", "frostreaver", "--Taijo has looted 2 Spell: Hammer of Souls from Diabo Xi Xin's corpse.--", t.AddSeconds(60));
        store.OnLine("Mendwell", "frostreaver", "--Gearbox has looted a Glowing Orb of Luclinite from Diabo Xi Xin.--", t.AddSeconds(90));
        store.OnLine("Mendwell", "frostreaver", "--a Shissar Fangs was left on Diabo Xi Xin's corpse.--", t.AddSeconds(900));

        DropMobRow diabo = Assert.Single(store.Table("frostreaver", false));
        Assert.Equal(1, diabo.Kills);
        Assert.Equal(new[] { "Spell: Hammer of Souls 2", "Glowing Orb of Luclinite 1", "Shissar Fangs 1" },
            diabo.Items.Select(i => $"{i.Item} {i.Drops}"));
    }
}
