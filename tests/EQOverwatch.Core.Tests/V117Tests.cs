using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Discord;
using EQOverwatch.Core.Roster;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V117Tests
{
    [Fact]
    public void The_loot_windows_link_split_by_bars_is_a_loot_list_but_a_dps_parse_is_not()
    {
        Assert.Equal(new[] { ("Undead Shissar Scales", 1), ("Shabby Rough Spell Scroll", 1), ("Grubby Crude Spell Scroll", 1) },
            RaidLootLines.LootList("Undead Shissar Scales | Shabby Rough Spell Scroll | Grubby Crude Spell Scroll"));
        Assert.Empty(RaidLootLines.LootList("Lord Inquisitor Seru in 446s, 1.37M Damage @3.06K, 1. Tankard = 103.74K@256 in 405s | 2. Mendwell = 94.66K@212 in 446s"));
        Assert.Empty(RaidLootLines.LootList("Shissar Fangs"));
    }

    [Fact]
    public void Guild_and_raid_dumps_give_the_roster_class_and_level_without_lowering_a_level()
    {
        string folder = Path.Combine(Path.GetTempPath(), "eqo-v117-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllLines(Path.Combine(folder, "Test Guild_frostreaver-20261125-183627.txt"), new[]
            {
                "Tankard\t60\tWarrior\tRaider\t\t11/25/26\tSsraeshza Temple\t\t\toff\toff\t0\t\t\t",
                "Mendwell\t58\tCleric\tRecruit\t\t11/24/26\t\t\t\toff\toff\t0\t\t\t",
            });
            var raid = new RaidAttendance { Server = "frostreaver", Taken = new DateTime(2026, 11, 25, 18, 12, 5), Members = { new RaidMember(1, "Shieldwall", 60, "Paladin", "Raid Leader") } };
            var roster = new PlayerRoster();
            roster.LearnPlayer("Mendwell", "Cleric", 60, "frostreaver"); // a /who saw a higher level

            Assert.True(DumpRosterImport.Learn(roster, folder, new[] { raid }) > 0);
            Assert.Equal(("Warrior", 60), (roster.GetClass("Tankard", "frostreaver"), roster.GetLevel("Tankard", "frostreaver")));
            Assert.Equal(("Paladin", 60), (roster.GetClass("Shieldwall", "frostreaver"), roster.GetLevel("Shieldwall", "frostreaver")));
            Assert.Equal(60, roster.GetLevel("Mendwell", "frostreaver"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
