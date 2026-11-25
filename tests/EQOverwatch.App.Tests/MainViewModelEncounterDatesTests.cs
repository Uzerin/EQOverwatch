using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_fight_list_can_be_limited_to_days_and_reset()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Tankard", "a gnome", 100, 1)); // Oct 21
        h.Vm.Refresh();
        Assert.Single(h.Vm.Encounters);
        Assert.Equal(string.Empty, h.Vm.EncounterCountText);

        h.Vm.EncounterFrom = new DateTime(2026, 10, 22);
        Assert.Empty(h.Vm.Encounters);
        Assert.Equal("0 of 1 fight", h.Vm.EncounterCountText);

        h.Vm.EncounterFrom = new DateTime(2026, 10, 21);
        h.Vm.EncounterTo = new DateTime(2026, 10, 21);
        Assert.Single(h.Vm.Encounters); // both days included
        Assert.Equal("1 of 1 fight", h.Vm.EncounterCountText);

        h.Vm.EncounterTo = new DateTime(2026, 10, 20);
        Assert.Empty(h.Vm.Encounters);

        h.Vm.ResetEncounterDatesCommand.Execute(null);
        Assert.Null(h.Vm.EncounterFrom);
        Assert.Null(h.Vm.EncounterTo);
        Assert.Single(h.Vm.Encounters);
        Assert.Equal(string.Empty, h.Vm.EncounterCountText);
    }
}
