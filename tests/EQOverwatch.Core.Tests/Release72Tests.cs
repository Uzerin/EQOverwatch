using EQOverwatch.Core.Characters;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Session;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class Release72Tests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "eqo-r72-" + Guid.NewGuid().ToString("N"));

    public Release72Tests() => Directory.CreateDirectory(_game);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_game, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Inventory(string name, DateTime at, params string[] sharedRows)
    {
        string path = Path.Combine(_game, $"{name}_frostreaver-Inventory.txt");
        var lines = new List<string> { "Location\tName\tID\tCount\tSlots", "General 1\tBackpack\t17005\t1\t8" };
        lines.AddRange(sharedRows);
        lines.Add("SharedBank8\tEmpty\t0\t0\t0");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        File.SetLastWriteTimeUtc(path, at);
    }

    [Fact]
    public void Shared_banks_are_kept_per_account_not_per_server()
    {
        DateTime t = DateTime.UtcNow.AddHours(-3);
        Inventory("Ream", t.AddHours(2), "SharedBank1\tLaurion's Satchel\t151186\t1\t42", "SharedBank1-Slot1\tFrightening Flatbread\t106898\t16\t6");
        Inventory("Reamii", t.AddHours(1), "SharedBank1\tLaurion's Satchel\t151186\t1\t42", "SharedBank1-Slot1\tFrightening Flatbread\t106898\t16\t6");
        Inventory("Stackprime", t.AddHours(2.5)); // another account, its shared bank empty
        var store = new CharacterDataStore();
        store.ScanAll(_game);

        // No names: characters whose banks match are one account.
        IReadOnlyList<SharedBankData> banks = store.SharedBanks();
        Assert.Equal(2, banks.Count);
        SharedBankData ream = store.FindShared("Reamii", "frostreaver")!;
        Assert.Equal(new[] { "Ream", "Reamii" }, ream.Characters);
        Assert.Equal("Ream, Reamii", ream.Label);
        Assert.Equal("Ream", ream.FromCharacter);
        Assert.Equal(2, ream.Items.Count);
        Assert.Empty(store.FindShared("Stackprime", "frostreaver")!.Items);

        ItemHit hit = Assert.Single(CharacterPlanner.FindItems(store.All(), banks, "flatbread"));
        Assert.Equal(("Ream, Reamii", "Shared bank 1 · slot 1"), (hit.Character, hit.Where));

        // A typed account name wins, and a character without one joins the named account whose bank it shows.
        store.SetAccountResolver((name, _) => name == "Ream" ? "Ream box" : null);
        SharedBankData named = store.FindShared("Reamii", "frostreaver")!;
        Assert.Equal("Ream box", named.Label);
        Assert.Equal(new[] { "Ream", "Reamii" }, named.Characters);
        Assert.Equal("Ream box", Assert.Single(CharacterPlanner.FindItems(store.All(), store.SharedBanks(), "flatbread")).Character);
    }

    [Fact]
    public void Characters_named_to_one_account_share_the_newest_bank_even_when_their_files_differ()
    {
        DateTime t = DateTime.UtcNow.AddHours(-3);
        Inventory("Ream", t.AddHours(2), "SharedBank1\tGem\t5\t3\t6");
        Inventory("Reamiiv", t, "SharedBank1\tOld Gem\t6\t1\t6"); // an older file of the same bank
        var store = new CharacterDataStore();
        store.ScanAll(_game);
        Assert.Equal(2, store.SharedBanks().Count); // with no names the two look like different accounts

        store.SetAccountResolver((_, _) => "Ream box");
        SharedBankData bank = Assert.Single(store.SharedBanks());
        Assert.Equal("Gem", Assert.Single(bank.Items).Name);
        Assert.Equal(new[] { "Ream", "Reamiiv" }, bank.Characters);
    }

    [Fact]
    public void Shared_bank_labels_list_three_names_then_a_count()
    {
        Assert.Equal("A, B, C +2", SharedBankData.NameList(new[] { "A", "B", "C", "D", "E" }));
        Assert.Equal("Shared bank (frostreaver)", new SharedBankData { Server = "frostreaver" }.Label);
    }

    private static CombatEvent Parse(string message, string self)
    {
        string line = TestData.Line(message, "20:00:01");
        Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
        Assert.True(CombatLineParser.TryParse(line, ts, self, out CombatEvent e), message);
        return e;
    }

    [Fact]
    public void Floating_text_shows_misses_resists_and_heals_you_cast()
    {
        var feed = new FloatingCombatFeed(name => name is "Stackprime" or "Ream");
        feed.OnLogOwner("Stackprime");
        CombatEvent dodge = Parse("A frost giant tries to hit YOU, but YOU dodge!", "Stackprime");
        feed.OnCombatEvent(in dodge);
        CombatEvent miss = Parse("You try to slash a frost giant, but miss!", "Stackprime");
        feed.OnCombatEvent(in miss);
        feed.OnNonCombatMessage("A bandit resisted your Lull!");
        feed.OnNonCombatMessage("You resist the ghoul lord's Ghoul Root!");
        feed.OnLogOwner("Ream");
        feed.OnNonCombatMessage("You healed Stackprime for 638 (16830) hit points by Complete Heal. (Critical)");

        IReadOnlyList<FloatingNumber> numbers = feed.TakeAll();
        Assert.Equal(new[] { "Dodge", "Miss", "Resisted", "Resisted", null }, numbers.Select(n => n.Word));
        Assert.Equal(new[] { true, false, false, true, false }, numbers.Select(n => n.IsIncoming));
        FloatingNumber heal = numbers[^1];
        Assert.Equal((FloatingPanel.HealDone, "Ream", 638, "Stackprime", true), (heal.Panel, heal.Character, heal.Amount, heal.Target, heal.IsCritical));
        Assert.True(heal.IsHeal);

        feed.ShowMisses = false;
        feed.OnLogOwner("Stackprime");
        feed.OnCombatEvent(in dodge);
        Assert.Empty(feed.TakeAll());
    }

    [Fact]
    public void The_biggest_hit_a_mob_lands_on_your_characters_is_kept()
    {
        string path = Path.Combine(_game, "mobhits.json");
        var hits = new MobHitStore(name => name == "Stackprime");
        hits.OnLogOwner("Stackprime");
        foreach (string message in new[] { "A frost giant hits YOU for 300 points of damage.", "A frost giant hits YOU for 1250 points of damage.", "A frost giant hits YOU for 90 points of damage." })
        {
            CombatEvent e = Parse(message, "Stackprime");
            hits.OnCombatEvent(in e);
        }

        CombatEvent mine = Parse("You slash a frost giant for 5000 points of damage.", "Stackprime");
        hits.OnCombatEvent(in mine); // your own hits are not the mob's
        Assert.Equal(1250, hits.MaxHit("A frost giant"));
        Assert.Equal(1250, hits.MaxHit("a frost giant"));

        hits.SetStoragePath(path);
        hits.Flush();
        var again = new MobHitStore(_ => true);
        again.SetStoragePath(path);
        Assert.Equal(1250, again.MaxHit("A frost giant"));
    }

    [Fact]
    public void Platinum_is_counted_per_zone()
    {
        var day = new DayAccumulator("Stackprime");
        DateTime t = new(2026, 11, 18, 19, 0, 0);
        day.Line("You have entered The Maiden's Eye.", t);
        day.Line("You receive 12 platinum, 5 gold from the corpse.", t.AddMinutes(1));
        day.Line("You have entered Umbral Plains.", t.AddMinutes(3));
        day.Line("You receive 3 platinum from the corpse.", t.AddMinutes(4));
        SessionSummary s = day.Summary(t.AddMinutes(5));
        Assert.Equal(12500, s.ZoneCopper!["The Maiden's Eye"]);
        Assert.Equal(3000, s.ZoneCopper["Umbral Plains"]);
    }
}
