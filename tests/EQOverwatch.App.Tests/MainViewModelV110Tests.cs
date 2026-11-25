using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_fight_list_can_be_searched_by_mob_name()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Tankard", "a gnome", 100, 1));
        h.Vm.Refresh();

        h.Vm.EncounterNameFilter = "GNO";
        Assert.Single(h.Vm.Encounters);
        h.Vm.EncounterNameFilter = "bat";
        Assert.Empty(h.Vm.Encounters);
        Assert.Equal("0 of 1 fight", h.Vm.EncounterCountText);

        h.Vm.ResetEncounterDatesCommand.Execute(null);
        Assert.Equal(string.Empty, h.Vm.EncounterNameFilter);
        Assert.Single(h.Vm.Encounters);
    }

    [Fact]
    public void Retiring_hides_a_character_and_turns_its_triggers_off()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Tankard", Array.Empty<string>()) };
        h.Vm.AddLogCommand.Execute(null);
        var log = Assert.Single(h.Vm.Logs);
        Assert.Contains(log, h.Vm.SortedCharacters);

        h.Vm.RetireLogCommand.Execute(log);
        Assert.True(log.IsRetired);
        Assert.False(log.IsTriggerEnabled);
        Assert.DoesNotContain(log, h.Vm.SortedCharacters);
        Assert.Equal("Unretire", log.RetireText);

        h.Vm.ShowRetired = true;
        Assert.Contains(log, h.Vm.SortedCharacters);
        h.Vm.RetireLogCommand.Execute(log);
        Assert.False(log.IsRetired);
        Assert.True(log.IsTriggerEnabled);
    }

    [Fact]
    public void Triggers_group_by_zone_or_by_your_folders()
    {
        using var h = new Harness(configure: (s, _) => s.Triggers = new List<TriggerRule>
        {
            new() { Name = "Any", Pattern = "x" },
            new() { Name = "Folder one", Pattern = "y", Folder = "Raids", Zone = "Vex Thal" },
        });

        TriggerRuleViewModel any = h.Vm.Rules.Single(r => r.Name == "Any");
        TriggerRuleViewModel raid = h.Vm.Rules.Single(r => r.Name == "Folder one");
        Assert.Equal(MainViewModel.AnyZoneGroup, any.GroupTop);
        Assert.Equal("Vex Thal", raid.GroupSub);

        h.Vm.GroupTriggersByFolder = true;
        Assert.Equal(MainViewModel.DefaultFolder, any.GroupTop);
        Assert.Equal("Raids", raid.GroupTop);
        Assert.Equal(string.Empty, raid.GroupSub);
    }

    [Fact]
    public void Search_everything_finds_characters_mobs_and_triggers()
    {
        using var h = new Harness(configure: (s, _) => s.Triggers = new List<TriggerRule> { new() { Name = "Gnome alarm", Pattern = "gnome" } });
        h.Dialogs.LogFiles = new[] { h.CreateLog("Tankard", Array.Empty<string>()) };
        h.Vm.AddLogCommand.Execute(null);
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Tankard", "a gnome", 100, 1));
        h.Vm.Refresh();

        Assert.Contains(h.Vm.SearchEverything("tank"), s => s.Kind == "Characters" && s.Text == "Tankard");
        IReadOnlyList<SearchHit> gnome = h.Vm.SearchEverything("gnome");
        SearchHit mob = Assert.Single(gnome, s => s.Kind == "Mobs and fights");
        Assert.Contains(gnome, s => s.Kind == "Triggers" && s.Text == "Gnome alarm");

        mob.Open();
        Assert.Equal("a gnome", h.Vm.EncounterNameFilter);
        Assert.Empty(h.Vm.SearchEverything("g"));
    }
}
