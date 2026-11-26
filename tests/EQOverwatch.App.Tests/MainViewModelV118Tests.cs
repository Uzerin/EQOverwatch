using System.Windows.Data;
using EQOverwatch.App.ViewModels;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Raid_triggers_are_grouped_and_a_group_turns_on_and_off_together()
    {
        using var h = new Harness();
        foreach ((string mob, string spell) in new[] { ("Lord Nagafen", "Dragon Roar"), ("Lady Vox", "Frost Breath") })
        {
            h.Vm.RaidMobName = mob;
            h.Vm.RaidSpellName = spell;
            h.Vm.RaidMatchText = spell + " hits";
            h.Vm.RaidRecastSeconds = 45;
            h.Vm.AddRaidTriggerCommand.Execute(null);
        }

        var group = Assert.IsAssignableFrom<CollectionViewGroup>(Assert.Single(h.Vm.RaidRulesView.Groups!));
        Assert.Equal(MainViewModel.UnknownZoneGroup, group.Name); // neither boss was fought yet
        h.Vm.DisableRaidTriggerGroupCommand.Execute(group);
        Assert.All(h.Vm.RaidRules, r => Assert.False(r.IsEnabled));
        h.Vm.EnableRaidTriggerGroupCommand.Execute(group);
        Assert.All(h.Vm.RaidRules, r => Assert.True(r.IsEnabled));
    }
}
