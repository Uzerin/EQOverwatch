using EQOverwatch.Core.Characters;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class CharacterDataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqo-chars-" + Guid.NewGuid().ToString("N"));
    private readonly string _game;
    private readonly string _store;

    public CharacterDataTests()
    {
        _game = Path.Combine(_dir, "game");
        _store = Path.Combine(_dir, "store");
        Directory.CreateDirectory(_game);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string fileName, DateTime? at = null, params string[] lines)
    {
        string path = Path.Combine(_game, fileName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        File.SetLastWriteTimeUtc(path, at ?? DateTime.UtcNow);
        return path;
    }

    private static readonly string[] InventoryLines =
    {
        "Location\tName\tID\tCount\tSlots",
        "Charm\tEmpty\t0\t0\t0",
        "Head\tHalo of the Enlightened\t1549\t1\t6",
        "Head-Slot1\tEmpty\t0\t0\t0",
        "Ear\tGolden Leaf Earring\t2581\t1\t6",
        "Ear\tEarring of Cleansing\t5815\t1\t6",
        "General 1\tLaurion's Satchel\t151186\t1\t42",
        "General 1-Slot5\tSpell: Blessing of Piety\t30411\t20\t6",
        "General 1-Slot12\tWand of Imperceptibility\t19725\t1\t6",
        "General 1-Slot12-Slot1\tSome Augment\t999\t1\t0",
        "Bank1\tBackpack*\t32601\t1\t8",
        "Bank1-Slot1\tEmpty\t0\t0\t0",
        "SharedBank4\tBackpack\t17005\t1\t8",
        "SharedBank4-Slot6\tSpell: Judgment\t7608\t2\t6",
        "KeyRing\tName\tID\t",
        "Mount\tDesiccated Hedgerow Leaf\t52021",
    };

    // ───────────────────────────── file names ─────────────────────────────

    [Theory]
    [InlineData("Reamiiv_frostreaver-CLR-MissingSpells", "Reamiiv", "frostreaver", "CLR", OutputFileKind.MissingSpells)]
    [InlineData("Reamiiv_frostreaver-CLR-Spellbook.txt", "Reamiiv", "frostreaver", "CLR", OutputFileKind.Spellbook)]
    [InlineData("Reamiiv_frostreaver-Inventory", "Reamiiv", "frostreaver", "", OutputFileKind.Inventory)]
    [InlineData("Ream_frostreaver-inventory.txt", "Ream", "frostreaver", "", OutputFileKind.Inventory)]
    public void An_output_file_name_gives_the_character_server_class_and_kind(
        string path, string name, string server, string classCode, OutputFileKind kind)
    {
        Assert.True(CharacterOutputParser.TryParseFileName(path, out OutputFileInfo info));
        Assert.Equal((name, server, classCode, kind), (info.Name, info.Server, info.ClassCode, info.Kind));
    }

    [Theory]
    [InlineData("eqlog_Reamiiv_frostreaver.txt")]
    [InlineData("Reamiiv_frostreaver-CLR-Guild")]
    [InlineData("Reamiiv-Inventory")]
    [InlineData("_frostreaver-Inventory")]
    [InlineData("Ream..iv_frostreaver-Inventory")]
    [InlineData("Reamiiv_frost reaver-Inventory")]
    [InlineData("")]
    public void Other_files_are_not_output_files(string path)
    {
        Assert.False(CharacterOutputParser.TryParseFileName(path, out _));
    }

    // ───────────────────────────── contents ─────────────────────────────

    [Fact]
    public void A_spell_list_is_level_tab_name_and_bad_lines_are_skipped()
    {
        List<SpellListEntry> spells = CharacterOutputParser.ParseSpellList(new[]
        {
            "6\tSanctuary", "14\tIony's Lesser Cleansing", "oops", "x\tNope", "9\t", "10\tIony's Lesser Augury\r",
        });

        Assert.Equal(
            new[] { (6, "Sanctuary"), (14, "Iony's Lesser Cleansing"), (10, "Iony's Lesser Augury") },
            spells.Select(s => (s.Level, s.Name)));
    }

    [Fact]
    public void An_inventory_file_drops_empty_slots_and_reads_the_key_ring_table()
    {
        InventoryFile file = CharacterOutputParser.ParseInventory(InventoryLines);

        Assert.Equal(
            new[]
            {
                "Head", "Ear", "Ear", "General 1", "General 1 · slot 5", "General 1 · slot 12", "General 1 · slot 12 · augment 1",
                "Bank 1", "Shared bank 4", "Shared bank 4 · slot 6",
            },
            file.Items.Select(i => i.Where));
        InventoryItem scroll = file.Items.Single(i => i.Name == "Spell: Blessing of Piety");
        Assert.Equal((30411, 20, 6), (scroll.ItemId, scroll.Count, scroll.Slots));
        Assert.True(file.Items.Single(i => i.Where == "Shared bank 4 · slot 6").IsShared);

        KeyRingEntry mount = Assert.Single(file.KeyRing);
        Assert.Equal(("Mount", "Desiccated Hedgerow Leaf", 52021), (mount.Type, mount.Name, mount.ItemId));
    }

    [Theory]
    [InlineData("Head", "Head")]
    [InlineData("Head-Slot1", "Head · augment 1")]
    [InlineData("General 1", "General 1")]
    [InlineData("General 1-Slot12-Slot1", "General 1 · slot 12 · augment 1")]
    [InlineData("Bank24-Slot3", "Bank 24 · slot 3")]
    [InlineData("SharedBank3-Slot8", "Shared bank 3 · slot 8")]
    public void Locations_are_described_in_words(string location, string expected)
    {
        Assert.Equal(expected, InventoryLocation.Describe(location));
    }

    // ───────────────────────────── the store ─────────────────────────────

    [Fact]
    public void Scanning_reads_only_the_named_characters_files()
    {
        Write("Reamiiv_frostreaver-Inventory", null, InventoryLines);
        Write("Reamiiv_frostreaver-CLR-Spellbook", null, "1\tCourage", "2\tStun");
        Write("Reamiiv_frostreaver-CLR-MissingSpells", null, "6\tSanctuary");
        Write("Ream_frostreaver-Inventory", null, "Location\tName\tID\tCount\tSlots", "Head\tOld Hat\t1\t1\t6");
        var store = new CharacterDataStore();

        Assert.Equal(3, store.Scan(_game, "Reamiiv", "Frostreaver"));

        CharacterData data = store.Find("Reamiiv")!;
        Assert.Equal("CLR", data.ClassCode);
        Assert.Equal(new[] { "Courage", "Stun" }, data.Spellbook.Select(s => s.Name));
        Assert.Equal(new[] { "Sanctuary" }, data.Missing.Select(s => s.Name));
        Assert.DoesNotContain(data.Inventory, i => i.IsShared);
        Assert.Equal("Desiccated Hedgerow Leaf", Assert.Single(data.KeyRing).Name);
        Assert.Null(store.Find("Ream")); // not read: Ream's files were not asked for, and Reamiiv's are not Ream's

        Assert.Equal(1, store.Scan(_game, "Ream"));
        Assert.Equal("Old Hat", Assert.Single(store.Find("Ream")!.Inventory).Name);
        Assert.Equal(0, store.Scan(_game, "Ream")); // nothing new the second time
    }

    [Fact]
    public void The_shared_bank_is_kept_once_per_server_from_the_newest_file()
    {
        DateTime early = DateTime.UtcNow.AddHours(-2);
        Write("Reamiiv_frostreaver-Inventory", early, InventoryLines);
        Write("Other_frostreaver-Inventory", DateTime.UtcNow,
            "Location\tName\tID\tCount\tSlots", "SharedBank1\tBackpack\t17005\t1\t8", "SharedBank1-Slot1\tGem\t5\t3\t6");
        var store = new CharacterDataStore();

        store.ScanAll(_game);

        SharedBankData shared = store.FindShared("frostreaver")!;
        Assert.Equal("Other", shared.FromCharacter);
        Assert.Equal(new[] { "Backpack", "Gem" }, shared.Items.Select(i => i.Name));
        Assert.DoesNotContain(store.Find("Reamiiv")!.Inventory, i => i.IsShared);
    }

    [Fact]
    public void A_file_is_only_read_again_when_it_is_newer_than_what_is_stored()
    {
        string path = Write("Reamiiv_frostreaver-CLR-Spellbook", DateTime.UtcNow.AddHours(-1), "1\tCourage");
        var store = new CharacterDataStore();
        int changes = 0;
        store.Changed += () => changes++;

        Assert.True(store.Import(path));
        Assert.False(store.Import(path)); // same file, nothing to do
        Assert.Equal(1, changes);

        Write("Reamiiv_frostreaver-CLR-Spellbook", DateTime.UtcNow, "1\tCourage", "2\tStun");
        Assert.True(store.Import(path));
        Assert.Equal(2, store.Find("Reamiiv")!.Spellbook.Count);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void What_was_read_is_saved_and_comes_back_in_a_new_store()
    {
        Write("Reamiiv_frostreaver-Inventory", null, InventoryLines);
        Write("Reamiiv_frostreaver-CLR-MissingSpells", null, "6\tSanctuary", "8\tRemove Minor Curse");
        new CharacterDataStore(_store).ScanAll(_game);

        var again = new CharacterDataStore(_store);

        CharacterData data = again.Find("Reamiiv")!;
        Assert.Equal(2, data.Missing.Count);
        Assert.Equal("General 1 · slot 5", data.Inventory.Single(i => i.Name == "Spell: Blessing of Piety").Where);
        Assert.Equal("Bank 1", data.Inventory.Single(i => i.Name == "Backpack*").Where);
        Assert.NotNull(data.InventoryUtc);
        Assert.Equal(2, again.FindShared("frostreaver")!.Items.Count);
    }

    [Fact]
    public void A_missing_folder_or_a_damaged_saved_file_is_harmless()
    {
        Directory.CreateDirectory(_store);
        File.WriteAllText(Path.Combine(_store, "Broken_frostreaver.json"), "{ not json");
        var store = new CharacterDataStore(_store);

        Assert.Equal(0, store.Scan(Path.Combine(_dir, "nowhere"), "Reamiiv"));
        Assert.Equal(0, store.Scan(null, "Reamiiv"));
        Assert.Null(store.Find("Broken"));
    }

    // ───────────────────────────── deleted files ─────────────────────────────

    [Fact]
    public void Saved_data_whose_output_file_was_deleted_is_dropped_by_kind()
    {
        string inventory = Write("Reamiiv_frostreaver-Inventory", null, InventoryLines);
        string book = Write("Reamiiv_frostreaver-CLR-Spellbook", null, "1\tCourage", "2\tStun");
        var store = new CharacterDataStore(_store);
        store.ScanAll(_game);
        Assert.NotNull(store.Find("Reamiiv")?.InventoryUtc);

        File.Delete(inventory);
        Assert.Equal(2, store.PruneMissingFiles(_game)); // the inventory, and the shared bank that came with it

        CharacterData? data = store.Find("Reamiiv");
        Assert.NotNull(data);
        Assert.Null(data!.InventoryUtc);
        Assert.Empty(data.Inventory);
        Assert.NotNull(data.SpellbookUtc);
        Assert.Empty(store.SharedBanks());

        // With every file gone the folder only counts as a game folder if it looks like one.
        File.Delete(book);
        Assert.Equal(0, store.PruneMissingFiles(_game));
        Assert.NotNull(store.Find("Reamiiv"));

        File.WriteAllText(Path.Combine(_game, "eqgame.exe"), string.Empty);
        store.PruneMissingFiles(_game);
        Assert.Null(store.Find("Reamiiv"));

        // and it stays gone after a restart
        Assert.Null(new CharacterDataStore(_store).Find("Reamiiv"));
    }

    [Fact]
    public void Nothing_is_dropped_when_the_game_folder_is_unreachable_or_not_a_game_folder()
    {
        Write("Reamiiv_frostreaver-CLR-Spellbook", null, "1\tCourage");
        var store = new CharacterDataStore(_store);
        store.ScanAll(_game);

        Assert.Equal(0, store.PruneMissingFiles(Path.Combine(_dir, "unplugged")));
        Assert.Equal(0, store.PruneMissingFiles(null));

        string empty = Path.Combine(_dir, "elsewhere"); // exists, holds no output files and is not an EverQuest folder
        Directory.CreateDirectory(empty);
        Assert.Equal(0, store.PruneMissingFiles(empty));
        Assert.NotNull(store.Find("Reamiiv")?.SpellbookUtc);
    }

    [Fact]
    public void Data_from_another_PC_is_left_alone_and_forget_removes_one_character()
    {
        string theirs = Write("Oodles_frostreaver-CLR-Spellbook", null, "1\tCourage");
        var store = new CharacterDataStore(_store);
        Assert.True(store.Import(theirs, "LAPTOP"));
        Write("Reamiiv_frostreaver-CLR-Spellbook", null, "1\tCourage");
        store.ScanAll(_game);
        File.Delete(theirs);

        store.PruneMissingFiles(_game);
        Assert.NotNull(store.Find("Oodles")); // came from LAPTOP, not this folder

        Assert.True(store.Forget("Oodles", "frostreaver"));
        Assert.Null(store.Find("Oodles"));
        Assert.False(store.Forget("Oodles", "frostreaver"));
        Assert.NotNull(store.Find("Reamiiv"));
    }
}
