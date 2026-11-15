using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Progress;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>Class personas (skills, faction and level per persona; tradeskills shared), per-persona spellbooks, and the inventory picture.</summary>
public sealed class PersonaAndInventoryTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqpersona-" + Guid.NewGuid().ToString("N"));

    public PersonaAndInventoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static void Line(ProgressStore store, string message, int second) =>
        store.OnLine("Ream", "frostreaver", message, T0.AddSeconds(second));

    [Fact]
    public void Skills_faction_and_level_belong_to_the_persona_that_was_on_and_tradeskills_to_the_character()
    {
        var store = new ProgressStore(T0.AddHours(-1));
        var switches = new List<(string Persona, int Level)>();
        store.PersonaChanged += (_, _, persona, level) => switches.Add((persona, level));

        Line(store, "You have become better at Meditate! (235)", 0); // before any persona line
        Line(store, "Your faction standing with Clerics of Tunare has been adjusted by 50.", 1);
        Line(store, "You have fashioned the items together to create something new: Ring of the Shissar.", 2);
        Line(store, "Your Cleric equipment set has been saved.", 10); // so the lines above were the cleric's
        Line(store, "You successfully loaded your Bard equipment set.", 15);
        Line(store, "You have become better at Singing! (180)", 20);
        Line(store, "You have fashioned the items together to create something new: Metal Bits.", 25);
        Line(store, "[40 Bard] Ream (Half Elf) <Leadership Optional> ZONE: Velketor's Labyrinth", 30);
        Line(store, "You have gained a level! Welcome to level 41!", 40);

        Assert.Equal(
            new[] { new PersonaInfo("Bard", 41, true), new PersonaInfo("Cleric", 0, false) },
            store.Personas("Ream", "frostreaver"));
        Assert.Equal("Bard", store.CurrentPersona("Ream", "frostreaver"));
        Assert.Equal("Meditate", store.Skills("Ream", "frostreaver", T0, "Cleric").Single().Skill);
        Assert.Equal("Singing", store.Skills("Ream", "frostreaver", T0).Single().Skill); // the current persona
        Assert.Equal(50, store.Factions("Ream", "frostreaver", "Cleric").Single().AllTime);
        Assert.Empty(store.Factions("Ream", "frostreaver", "Bard"));
        Assert.Equal(2, store.Tradeskills("Ream", "frostreaver").Count);
        Assert.Equal(new[] { ("Cleric", 0), ("Bard", 0), ("Bard", 40), ("Bard", 41) }, switches);
    }

    [Fact]
    public void Lines_before_a_loaded_line_go_to_the_known_class_and_a_title_still_gives_the_level()
    {
        var store = new ProgressStore();
        store.SetClassProvider((_, _) => "Warrior");
        Line(store, "You have become better at Bash! (100)", 0);
        Line(store, "You successfully loaded your Monk equipment set.", 5);
        Line(store, "[60 Grandmaster] Ream (Iksar) ZONE: The Overthere", 6);

        Assert.Equal(100, store.Skills("Ream", "frostreaver", T0, "Warrior").Single().Value);
        Assert.Equal(60, store.Personas("Ream", "frostreaver").Single(p => p.Persona == "Monk").Level);
    }

    [Fact]
    public void Each_persona_keeps_its_own_spellbook_and_missing_spells()
    {
        string game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(game);
        void Write(string name, DateTime at, params string[] lines)
        {
            string path = Path.Combine(game, name + ".txt");
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            File.SetLastWriteTimeUtc(path, at);
        }

        Write("Ream_frostreaver-CLR-Spellbook", DateTime.UtcNow.AddHours(-2), "1\tCourage", "60\tComplete Heal");
        Write("Ream_frostreaver-BRD-Spellbook", DateTime.UtcNow.AddHours(-1), "1\tChant of Battle");
        Write("Ream_frostreaver-BRD-MissingSpells", DateTime.UtcNow, "10\tSelo's Accelerando");

        var store = new CharacterDataStore(Path.Combine(_dir, "store"));
        store.ScanAll(game);
        CharacterData ream = store.Find("Ream", "frostreaver")!;
        Assert.Equal(new[] { "Courage", "Complete Heal" }, ream.Books("CLR")!.Spellbook.Select(s => s.Name));
        Assert.Equal("Chant of Battle", ream.Books("BRD")!.Spellbook.Single().Name);
        Assert.Equal("Selo's Accelerando", ream.Books("BRD")!.Missing.Single().Name);
        Assert.Null(ream.Books("CLR")!.MissingUtc);
        Assert.Null(ream.Books("WAR"));
        Assert.Equal("Chant of Battle", ream.Spellbook.Single().Name); // the newest of any persona

        var again = new CharacterDataStore(Path.Combine(_dir, "store"));
        Assert.Equal(2, again.Find("Ream", "frostreaver")!.Books("CLR")!.Spellbook.Count);
    }

    [Fact]
    public void The_inventory_picture_has_worn_gear_with_sockets_bags_with_their_slots_and_the_bank()
    {
        InventoryFile file = CharacterOutputParser.ParseInventory(new[]
        {
            "Location\tName\tID\tCount\tSlots",
            "Charm\tEmpty\t0\t0\t0",
            "Ear\tGolden Leaf Earring\t2581\t1\t6",
            "Ear-Slot1\tEmpty\t0\t0\t0",
            "Head\tCrown of the Kromzek Kings\t25194\t1\t6",
            "Head-Slot1\tRune of Rikkukin\t500\t1\t6",
            "Head-Slot2\tEmpty\t0\t0\t0",
            "Ear\tSiren Hair Earring\t24741\t1\t6",
            "General 1\tLarge Bag\t17005\t1\t4",
            "General 1-Slot1\tBone Chips\t13073\t12\t6",
            "General 1-Slot2\tBlack Ice Leggings\t1\t1\t6",
            "General 1-Slot2-Slot1\tEmpty\t0\t0\t0",
            "General 1-Slot3\tEmpty\t0\t0\t0",
            "General 1-Slot4\tEmpty\t0\t0\t0",
            "General 2\tGem of Rage\t1\t1\t6",
            "General 2-Slot1\tEmpty\t0\t0\t0",
            "Bank1\tBackpack\t17000\t1\t2",
            "Bank1-Slot1\tWater Flask\t13006\t20\t6",
            "Bank1-Slot2\tEmpty\t0\t0\t0",
            "SharedBank1\tPeridot\t10028\t3\t6",
        });

        IReadOnlyList<InventorySlot> worn = InventoryLayout.Worn(file.Layout!);
        Assert.Equal(InventoryLayout.WornSlots.Count, worn.Count);
        Assert.True(worn[0].IsEmpty); // the charm
        Assert.Equal("Golden Leaf Earring", worn[1].Item);
        Assert.Equal(new[] { false }, worn[1].Augments);
        Assert.Equal(new[] { true, false }, worn[2].Augments);
        Assert.Equal("Siren Hair Earring", worn[4].Item); // the second ear slot

        IReadOnlyList<InventorySlot> general = InventoryLayout.Containers(file.Layout!, "General", "General");
        Assert.True(general[0].IsBag);
        Assert.Equal(4, general[0].Capacity);
        Assert.Equal(new[] { "Bone Chips", "Black Ice Leggings", null, null }, general[0].Contents.Select(c => c.Item));
        Assert.Equal(12, general[0].Contents[0].Count);
        Assert.Equal(new[] { false }, general[0].Contents[1].Augments);
        Assert.False(general[1].IsBag); // an item with one augment socket is not a bag
        Assert.Equal(new[] { false }, general[1].Augments);

        Assert.Equal("Water Flask", InventoryLayout.Containers(file.Layout!, "Bank", "Bank").Single().Contents[0].Item);
        Assert.Equal("Peridot", InventoryLayout.Containers(file.Layout!, "SharedBank", "Shared bank").Single().Item);
    }
}
