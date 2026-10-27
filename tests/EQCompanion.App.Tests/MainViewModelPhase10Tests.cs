using System.IO;
using EQCompanion.App.ViewModels;
using EQCompanion.Core.Models;
using EQCompanion.Core.Settings;
using Xunit;

namespace EQCompanion.App.Tests;

public partial class MainViewModelTests
{
    /// <summary>A split-layout spell file (column 6 numeric, 8 cast time, 10 recast ms, 11 formula, 12 ticks, 28 good, 43 Bard).</summary>
    private static string WriteAbilitySpellFile(string path)
    {
        static string Row(int id, string name, int recastMs, int formula, int ticks)
        {
            string[] c = Enumerable.Repeat("0", 130).ToArray();
            c[0] = id.ToString();
            c[1] = name;
            c[10] = recastMs.ToString();
            c[11] = formula.ToString();
            c[12] = ticks.ToString();
            c[28] = "1";
            c[43] = "255";
            return string.Join('^', c);
        }

        File.WriteAllText(path, string.Join("\n",
            Row(4499, "Defensive Discipline", 630000, 11, 30),
            Row(4506, "Trueshot Discipline", 4050000, 11, 20),
            Row(88, "Harm Touch", 0, 0, 0)) + "\n");
        return path;
    }

    private static async Task LoadAbilitySpellsAsync(Harness h)
    {
        h.Dialogs.SpellsFile = WriteAbilitySpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Searching_finds_discs_and_picking_one_fills_in_its_times()
    {
        using var h = new Harness();
        await LoadAbilitySpellsAsync(h);

        h.Vm.AbilitySearchText = "defensive";

        AbilitySuggestion suggestion = Assert.Single(h.Vm.AbilitySuggestions);
        Assert.Equal("Defensive Discipline", suggestion.Name);
        Assert.Equal("ID 4499 · lasts 3:00 · recast 10:30", suggestion.Detail);

        h.Vm.SelectedAbilitySuggestion = suggestion;
        Assert.Equal("3:00", h.Vm.AbilityActiveText);
        Assert.Equal("10:30", h.Vm.AbilityCooldownText);
    }

    [Fact]
    public async Task An_ability_can_be_added_with_its_own_scope_and_overlays_and_it_is_saved()
    {
        using var h = new Harness();
        await LoadAbilitySpellsAsync(h);
        h.Vm.AbilitySearchText = "defensive";
        h.Vm.SelectedAbilitySuggestion = h.Vm.AbilitySuggestions[0];
        h.Vm.AbilityScope = AbilityScope.Everyone;
        h.Vm.AbilityActiveOverlay = TimerOverlayCategory.Buffs;
        h.Vm.AbilityCooldownOverlay = TimerOverlayCategory.RaidTriggers;

        h.Vm.AddAbilityCommand.Execute(null);

        AbilityRowViewModel row = Assert.Single(h.Vm.TrackedAbilities);
        Assert.Equal("Defensive Discipline", row.Name);
        Assert.Equal(180, row.ActiveSeconds);
        Assert.Equal(630, row.CooldownSeconds);
        Assert.Equal("1 ability tracked (1 on)", h.Vm.AbilitySummary);
        Assert.Equal(1, h.Pipeline.Abilities.AbilityCount);
        Assert.Equal(string.Empty, h.Vm.AbilitySearchText); // ready for the next one
        Assert.Contains("Added Defensive Discipline", h.Vm.AbilityStatus);

        TrackedAbilitySetting saved = Assert.Single(h.Saved().TrackedAbilities);
        Assert.Equal("Defensive Discipline", saved.Name);
        Assert.Equal(4499, saved.SpellId);
        Assert.Equal(AbilityScope.Everyone, saved.Scope);
        Assert.Equal(row.Id, saved.Id);
    }

    [Fact]
    public void An_ability_that_is_not_in_the_spell_file_can_be_typed_in_with_its_times()
    {
        using var h = new Harness();
        h.Vm.AbilitySearchText = "Harm Touch";
        h.Vm.AbilityActiveText = string.Empty;
        h.Vm.AbilityCooldownText = "1:12:00";
        h.Vm.AbilityScope = AbilityScope.MyCharacters;

        h.Vm.AddAbilityCommand.Execute(null);

        AbilityRowViewModel row = Assert.Single(h.Vm.TrackedAbilities);
        Assert.Equal(0, row.ActiveSeconds);
        Assert.Equal(4320, row.CooldownSeconds);
        Assert.Equal(AbilityScope.MyCharacters, row.Scope);
        Assert.Equal(0, row.SpellId);
    }

    [Fact]
    public void Bad_input_is_explained_and_nothing_is_added()
    {
        using var h = new Harness();

        h.Vm.AddAbilityCommand.Execute(null);
        Assert.Contains("Pick an ability", h.Vm.AbilityStatus);

        h.Vm.AbilitySearchText = "Harm Touch";
        h.Vm.AddAbilityCommand.Execute(null);
        Assert.Contains("how long it lasts", h.Vm.AbilityStatus);

        h.Vm.AbilityCooldownText = "soon";
        h.Vm.AddAbilityCommand.Execute(null);
        Assert.Contains("Times look like", h.Vm.AbilityStatus);

        Assert.Empty(h.Vm.TrackedAbilities);
    }

    [Fact]
    public void Adding_an_ability_that_is_already_tracked_updates_it()
    {
        using var h = new Harness();
        h.Vm.AbilitySearchText = "Harm Touch";
        h.Vm.AbilityCooldownText = "10:00";
        h.Vm.AddAbilityCommand.Execute(null);

        h.Vm.AbilitySearchText = "harm touch";
        h.Vm.AbilityCooldownText = "5:00";
        h.Vm.AbilityScope = AbilityScope.MyCharacters;
        h.Vm.AddAbilityCommand.Execute(null);

        AbilityRowViewModel row = Assert.Single(h.Vm.TrackedAbilities);
        Assert.Equal(300, row.CooldownSeconds);
        Assert.Equal(AbilityScope.MyCharacters, row.Scope);
        Assert.Contains("already tracked", h.Vm.AbilityStatus);
    }

    [Fact]
    public void Editing_a_row_updates_the_tracker_and_the_saved_settings()
    {
        using var h = new Harness();
        h.Vm.AbilitySearchText = "Harm Touch";
        h.Vm.AbilityCooldownText = "10:00";
        h.Vm.AddAbilityCommand.Execute(null);
        AbilityRowViewModel row = Assert.Single(h.Vm.TrackedAbilities);

        row.CooldownText = "12:30";
        row.ActiveText = "nonsense"; // ignored; the old value is shown again
        row.Scope = AbilityScope.MyCharacters;
        row.CooldownOverlay = TimerOverlayCategory.Debuffs;
        row.IsEnabled = false;

        Assert.Equal(750, row.CooldownSeconds);
        Assert.Equal("0:00", row.ActiveText);
        Assert.Equal("1 ability tracked (0 on)", h.Vm.AbilitySummary);
        Assert.Equal(0, h.Pipeline.Abilities.AbilityCount); // disabled

        TrackedAbilitySetting saved = Assert.Single(h.Saved().TrackedAbilities);
        Assert.Equal(750, saved.CooldownSeconds);
        Assert.Equal(AbilityScope.MyCharacters, saved.Scope);
        Assert.Equal(TimerOverlayCategory.Debuffs, saved.CooldownOverlay);
        Assert.False(saved.IsEnabled);
    }

    [Fact]
    public void Removing_a_row_stops_tracking_it()
    {
        using var h = new Harness();
        h.Vm.AbilitySearchText = "Harm Touch";
        h.Vm.AbilityCooldownText = "10:00";
        h.Vm.AddAbilityCommand.Execute(null);

        h.Vm.RemoveAbilityCommand.Execute(h.Vm.TrackedAbilities[0]);

        Assert.Empty(h.Vm.TrackedAbilities);
        Assert.Equal(0, h.Pipeline.Abilities.AbilityCount);
        Assert.Empty(h.Saved().TrackedAbilities);
    }

    [Fact]
    public void Saved_abilities_come_back_when_the_app_opens()
    {
        using var h = new Harness((settings, _) => settings.TrackedAbilities.Add(new TrackedAbilitySetting
        {
            Name = "Trueshot Discipline", ActiveSeconds = 120, CooldownSeconds = 4050, Scope = AbilityScope.MyCharacters,
            ActiveOverlay = TimerOverlayCategory.Songs, CooldownOverlay = TimerOverlayCategory.Debuffs,
        }));

        AbilityRowViewModel row = Assert.Single(h.Vm.TrackedAbilities);
        Assert.Equal("Trueshot Discipline", row.Name);
        Assert.Equal("2:00", row.ActiveText);
        Assert.Equal("1:07:30", row.CooldownText);
        Assert.Equal(AbilityScope.MyCharacters, row.Scope);
        Assert.Equal(TimerOverlayCategory.Songs, row.ActiveOverlay);
        Assert.Equal(1, h.Pipeline.Abilities.AbilityCount);
    }

    [Theory]
    [InlineData("3:00", 180)]
    [InlineData("180", 180)]
    [InlineData("1:07:30", 4050)]
    [InlineData("", 0)]
    [InlineData("  10:30 ", 630)]
    public void Times_are_read_in_minutes_seconds_or_plain_seconds(string text, int seconds)
    {
        Assert.True(AbilityTime.TryParse(text, out int parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    [InlineData("25:00:00")]
    public void Bad_times_are_rejected(string text) => Assert.False(AbilityTime.TryParse(text, out _));
}
