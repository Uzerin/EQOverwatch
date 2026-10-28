using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.App.Tests;

public class DpsOverlayViewModelTests
{
    private const string Giant = "a frost giant";

    private static CombatEvent Hit(string source, string target, int amount, int second) =>
        new(new DateTime(2026, 10, 21, 11, 45, second), CombatEventKind.Damage, source, target, amount, DamageKind.Melee, "hit");

    [Fact]
    public void Shows_header_and_ranked_rows_with_relative_fill()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 1000, 1));
        tracker.OnCombatEvent(Hit("Oodles", Giant, 500, 2));
        tracker.OnCombatEvent(Hit("Ream", Giant, 500, 3));
        var vm = new DpsOverlayViewModel(() => tracker.Snapshot);

        vm.Tick();

        Assert.Equal(Giant, vm.TargetName);
        Assert.Equal("00:02", vm.DurationText);
        Assert.Equal("1K", vm.GroupDpsText); // 2000 damage over 2s
        Assert.True(vm.IsLive);

        Assert.Equal(new[] { "1. Stackprime", "2. Oodles", "3. Ream" }, vm.Rows.Select(r => r.RankName));

        DpsRowViewModel top = vm.Rows[0];
        Assert.Equal("500", top.SdpsText);   // 1000 over the 2s encounter
        Assert.Equal("1K", top.TotalText);
        Assert.Equal("50%", top.PercentText);
        Assert.Equal(1.0, top.Fill, 3);

        DpsRowViewModel second = vm.Rows[1];
        Assert.Equal("500", second.TotalText);
        Assert.Equal("25%", second.PercentText);
        Assert.Equal(0.5, second.Fill, 3);
    }

    [Fact]
    public void Shows_the_idle_state_when_there_is_no_encounter()
    {
        var vm = new DpsOverlayViewModel(() => DpsSnapshot.Empty);

        vm.Tick();

        Assert.Equal(DpsOverlayViewModel.IdleTitle, vm.TargetName);
        Assert.Equal("00:00", vm.DurationText);
        Assert.Equal("0", vm.GroupDpsText);
        Assert.False(vm.IsLive);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Falls_back_to_the_latest_completed_encounter_and_marks_it_not_live()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 400, 1));
        tracker.OnLogClock(new DateTime(2026, 10, 21, 11, 45, 30)); // timeout closes the encounter
        var vm = new DpsOverlayViewModel(() => tracker.Snapshot);

        vm.Tick();

        Assert.Equal(Giant, vm.TargetName);
        Assert.False(vm.IsLive);
        Assert.Equal("1. Stackprime", Assert.Single(vm.Rows).RankName);
    }

    [Fact]
    public void Prefers_the_most_recently_active_encounter()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, 1));
        tracker.OnCombatEvent(Hit("Stackprime", "a gnoll", 50, 3));
        var vm = new DpsOverlayViewModel(() => tracker.Snapshot);

        vm.Tick();

        Assert.Equal("a gnoll", vm.TargetName);
    }

    [Fact]
    public void Limits_the_list_to_the_top_eight_participants()
    {
        string[] names = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel", "India", "Juliet" };
        var tracker = new LiveDpsTracker();
        for (int i = 0; i < names.Length; i++)
        {
            tracker.OnCombatEvent(Hit(names[i], Giant, 1000 - i * 10, 1));
        }

        var vm = new DpsOverlayViewModel(() => tracker.Snapshot);
        vm.Tick();

        Assert.Equal(DpsOverlayViewModel.MaxRows, vm.Rows.Count);
        Assert.Equal("1. Alpha", vm.Rows[0].RankName);
        Assert.Equal("8. Hotel", vm.Rows[7].RankName);
    }

    [Fact]
    public void Rows_are_updated_in_place_when_the_snapshot_changes_and_ignored_when_it_does_not()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Stackprime", Giant, 100, 1));
        var vm = new DpsOverlayViewModel(() => tracker.Snapshot);

        vm.Tick();
        DpsRowViewModel row = Assert.Single(vm.Rows);
        Assert.Equal("100", row.TotalText);

        vm.Tick(); // unchanged snapshot: nothing to do
        Assert.Same(row, Assert.Single(vm.Rows));

        tracker.OnCombatEvent(Hit("Stackprime", Giant, 900, 2));
        tracker.OnCombatEvent(Hit("Oodles", Giant, 100, 2));
        vm.Tick();

        Assert.Same(row, vm.Rows[0]);
        Assert.Equal("1K", row.TotalText);
        Assert.Equal(2, vm.Rows.Count);
    }
}
