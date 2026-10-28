using System.IO;
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
}
