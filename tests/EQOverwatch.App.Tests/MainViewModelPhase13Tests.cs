using System.IO;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Models;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Watched_characters_show_the_level_and_class_learned_from_the_logs()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime"), h.CreateLog("Newalt") };
        h.Vm.AddLogCommand.Execute(null);
        Assert.All(h.Vm.Logs, c => Assert.Equal("—", c.LevelText));

        h.Pipeline.Roster.Observe("[125 Warlord (Cleric)] Stackprime (Human)");
        h.Vm.Refresh();

        var stack = h.Vm.Logs.Single(c => c.CharacterName == "Stackprime");
        Assert.Equal("125", stack.LevelText);
        Assert.Equal("Cleric", stack.ClassText);
        var alt = h.Vm.Logs.Single(c => c.CharacterName == "Newalt");
        Assert.Equal("—", alt.LevelText);
        Assert.Equal("—", alt.ClassText);
    }

    [Fact]
    public void Additional_characters_get_the_same_columns()
    {
        using var h = new Harness();
        h.Vm.NewAdditionalCharacter = "Oodles";
        h.Vm.AddAdditionalCharacterCommand.Execute(null);

        CharacterInfoRow before = Assert.Single(h.Vm.AdditionalCharacterRows);
        Assert.Equal(("Oodles", "—", "—"), (before.Name, before.LevelText, before.ClassText));

        h.Pipeline.Roster.Observe("[65 Archmage (Magician)] Oodles (Gnome)");
        h.Vm.Refresh();

        CharacterInfoRow after = Assert.Single(h.Vm.AdditionalCharacterRows);
        Assert.Equal(("65", "Magician"), (after.LevelText, after.ClassText));

        h.Vm.RemoveAdditionalCharacterCommand.Execute("Oodles");
        Assert.Empty(h.Vm.AdditionalCharacterRows);
    }

    [Fact]
    public void The_saved_level_comes_back_on_the_next_run()
    {
        using var h = new Harness((settings, _) =>
        {
            settings.AdditionalMonitoredCharacters = new List<string> { "Oodles" };
            settings.Roster = new RosterSettings { Players = { new RosterPlayerEntry { Name = "Oodles", Class = "Magician", Level = 124 } } };
        });

        CharacterInfoRow row = Assert.Single(h.Vm.AdditionalCharacterRows);
        Assert.Equal(("124", "Magician"), (row.LevelText, row.ClassText));
        Assert.Equal("124", Assert.Single(h.Vm.RosterPlayers).LevelText);
    }

    [Fact]
    public async Task An_ability_row_shows_when_it_starts_and_the_spell_files_fade_text()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);

        h.Vm.AbilitySearchText = "Spirit of Wolf";
        h.Vm.AbilityActiveText = "15:00";
        h.Vm.AbilityCooldownText = "1:00:00";
        h.Vm.AddAbilityCommand.Execute(null);
        h.Vm.AbilitySearchText = "Nameless Aura";
        h.Vm.AbilityActiveText = "1:00";
        h.Vm.AddAbilityCommand.Execute(null);

        AbilityRowViewModel known = h.Vm.TrackedAbilities.Single(r => r.Name == "Spirit of Wolf");
        Assert.Equal("You activate Spirit of Wolf. · <Name> activates Spirit of Wolf.", known.StartsWhen);
        Assert.Equal("Your speed returns to normal.", known.FadesText);
        Assert.Equal("—", h.Vm.TrackedAbilities.Single(r => r.Name == "Nameless Aura").FadesText);
    }

    [Fact]
    public void Importing_still_reports_through_the_status_line_without_the_import_bar()
    {
        using var h = new Harness();

        Assert.False(h.Vm.IsImporting);
        Assert.NotNull(h.Vm.ImportStatus);
    }

    // ───────────────────────────── character data windows ─────────────────────────────

    private sealed class FakeCharacterPresenter : ICharacterDataPresenter
    {
        public List<CharacterDataViewModel> Shown { get; } = new();
        public List<(string Title, string Message)> Messages { get; } = new();

        public void Show(CharacterDataViewModel viewModel) => Shown.Add(viewModel);

        public void ShowMessage(string title, string message) => Messages.Add((title, message));
    }

    private static void WriteOutputFile(Harness h, string fileName, params string[] lines) =>
        File.WriteAllText(Path.Combine(h.Dir, fileName), string.Join("\n", lines) + "\n");

    private static async Task<(WatchedLogConfig Reamiiv, WatchedLogConfig Ream)> WatchReamCharactersAsync(Harness h)
    {
        await h.Vm.ApplyEqFolderAsync(h.Dir); // the game folder is where the /outputfile files are
        h.Dialogs.LogFiles = new[] { h.CreateLog("Reamiiv"), h.CreateLog("Ream") };
        h.Vm.AddLogCommand.Execute(null);
        return (h.Vm.Logs.Single(l => l.CharacterName == "Reamiiv"), h.Vm.Logs.Single(l => l.CharacterName == "Ream"));
    }

    [Fact]
    public async Task A_data_button_without_an_output_file_explains_which_command_to_type()
    {
        using var h = new Harness();
        (WatchedLogConfig reamiiv, _) = await WatchReamCharactersAsync(h);

        h.Vm.OpenInventoryCommand.Execute(reamiiv);

        Assert.Empty(h.CharacterPresenter.Shown);
        (string title, string message) = Assert.Single(h.CharacterPresenter.Messages);
        Assert.Contains("Reamiiv", title);
        Assert.Contains("/outputfile inventory", message);
        Assert.Contains(h.Dir, message);
        Assert.Equal(message, h.Vm.CharacterDataStatus);
        Assert.Equal("No data yet", reamiiv.DataText);

        h.Vm.OpenMissingSpellsCommand.Execute(reamiiv);
        Assert.Contains("/outputfile missingspells", h.CharacterPresenter.Messages[1].Message);
        h.Vm.OpenSpellbookCommand.Execute(reamiiv);
        Assert.Contains("/outputfile spellbook", h.CharacterPresenter.Messages[2].Message);
    }

    [Fact]
    public void Without_a_game_folder_the_message_asks_for_it()
    {
        string message = MainViewModel.NoCharacterDataMessage("Reamiiv", OutputFileKind.Inventory, string.Empty);

        Assert.Contains("EverQuest folder", message);
        Assert.DoesNotContain("/outputfile inventory", message); // no command to type until the folder is set
    }

    [Fact]
    public async Task A_data_button_opens_the_window_on_its_tab_with_that_characters_data_only()
    {
        using var h = new Harness();
        (WatchedLogConfig reamiiv, WatchedLogConfig ream) = await WatchReamCharactersAsync(h);
        WriteOutputFile(h, "Reamiiv_frostreaver-Inventory",
            "Location\tName\tID\tCount\tSlots",
            "Head\tHalo of the Enlightened\t1549\t1\t6",
            "Head-Slot1\tEmpty\t0\t0\t0",
            "General 1\tLaurion's Satchel\t151186\t1\t42",
            "General 1-Slot5\tSpell: Blessing of Piety\t30411\t20\t6",
            "SharedBank4-Slot6\tSpell: Judgment\t7608\t2\t6",
            "KeyRing\tName\tID\t",
            "Mount\tDesiccated Hedgerow Leaf\t52021");
        WriteOutputFile(h, "Reamiiv_frostreaver-CLR-Spellbook", "1\tCourage", "2\tStun");
        WriteOutputFile(h, "Reamiiv_frostreaver-CLR-MissingSpells", "6\tSanctuary", "15\tBlessing of Piety", "25\tJudgment");

        h.Vm.OpenInventoryCommand.Execute(reamiiv);
        h.Vm.OpenMissingSpellsCommand.Execute(reamiiv);
        h.Vm.OpenSpellbookCommand.Execute(reamiiv);

        Assert.Empty(h.CharacterPresenter.Messages);
        Assert.Equal(
            new[] { CharacterDataViewModel.InventoryTab, CharacterDataViewModel.MissingSpellsTab, CharacterDataViewModel.SpellbookTab },
            h.CharacterPresenter.Shown.Select(v => v.SelectedTabIndex));

        CharacterDataViewModel vm = h.CharacterPresenter.Shown[0];
        Assert.Equal("Reamiiv_Frostreaver", vm.Key);
        Assert.Contains("Reamiiv", vm.Title);
        Assert.Equal(
            new[] { "Head", "General 1", "General 1 · slot 5", "Shared bank 4 · slot 6" },
            vm.InventoryRows.Select(r => r.Where));
        Assert.Equal(new[] { "Courage", "Stun" }, vm.SpellbookRows.Select(r => r.Name));
        Assert.Equal("Desiccated Hedgerow Leaf", Assert.Single(vm.KeyRingRows).Name);

        // Scrolls in the bags and the shared bank are matched to the missing spells.
        Assert.Equal(
            new[] { ("Sanctuary", "—"), ("Blessing of Piety", "Spell: Blessing of Piety · General 1 · slot 5"),
                    ("Judgment", "Spell: Judgment · Shared bank 4 · slot 6") },
            vm.MissingRows.Select(r => (r.Name, r.Scroll)));
        Assert.Contains("Scrolls on hand for missing spells: 2", vm.MissingSummary);
        vm.OnlyWithScroll = true;
        Assert.Equal(new[] { "Blessing of Piety", "Judgment" }, vm.MissingRows.Select(r => r.Name));

        // The filter narrows the tables; Reamiiv's files are not Ream's.
        vm.OnlyWithScroll = false;
        vm.FilterText = "satchel";
        Assert.Equal("Laurion's Satchel", Assert.Single(vm.InventoryRows).Item);
        Assert.NotEqual("No data yet", reamiiv.DataText);
        Assert.Equal("No data yet", ream.DataText);

        h.Vm.OpenInventoryCommand.Execute(ream);
        Assert.Contains("Ream", h.CharacterPresenter.Messages.Single().Title);
        Assert.Equal(3, h.CharacterPresenter.Shown.Count);
    }

    [Fact]
    public async Task Refreshing_the_window_picks_up_a_newer_file()
    {
        using var h = new Harness();
        (WatchedLogConfig reamiiv, _) = await WatchReamCharactersAsync(h);
        WriteOutputFile(h, "Reamiiv_frostreaver-CLR-Spellbook", "1\tCourage");
        h.Vm.OpenSpellbookCommand.Execute(reamiiv);
        CharacterDataViewModel vm = Assert.Single(h.CharacterPresenter.Shown);
        Assert.Single(vm.SpellbookRows);

        string path = Path.Combine(h.Dir, "Reamiiv_frostreaver-CLR-Spellbook");
        WriteOutputFile(h, "Reamiiv_frostreaver-CLR-Spellbook", "1\tCourage", "2\tStun");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        vm.RefreshCommand.Execute(null);

        Assert.Equal(2, vm.SpellbookRows.Count);
    }
}
