using EQOverwatch.App.ViewModels;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Crash_log_entries_are_split_by_their_headers()
    {
        string log = "===== 2026-11-24 18:02:11 UTC  EQ Overwatch v1.0.110  (UI thread)\nSystem.Exception: one\n\n" +
                     "===== 2026-11-25 01:00:00 UTC  EQ Overwatch v1.0.110  (background thread)\nSystem.Exception: two\n";

        var entries = MainViewModel.CrashEntries(log);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new DateTime(2026, 11, 25, 1, 0, 0, DateTimeKind.Utc), entries[1].Utc);
        Assert.Contains("System.Exception: one", entries[0].Text);
        Assert.DoesNotContain("two", entries[0].Text);
    }

    [Fact]
    public void Problems_are_listed_newest_first_and_can_be_copied_and_cleared()
    {
        using var h = new Harness();
        h.Vm.AddProblem("first");
        h.Vm.AddProblem("second");

        Assert.True(h.Vm.HasProblems);
        Assert.EndsWith("second", h.Vm.Problems[0]);
        h.Vm.CopyProblemsCommand.Execute(null);
        Assert.Contains("first", h.Clipboard.Texts[^1]);
        h.Vm.ClearProblemsCommand.Execute(null);
        Assert.False(h.Vm.HasProblems);
    }
}

public partial class MainViewModelTests
{
    [Fact]
    public void The_compact_picture_has_only_the_damage_bars()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Tankard", "a gnome", 100, 1));
        h.Vm.Refresh();

        h.Vm.CopyCompactImageCommand.Execute(null);

        var image = Assert.Single(h.Clipboard.Images);
        Assert.Null(image.Encounter.Timeline);
        Assert.Empty(image.Encounter.TakenRows);
        Assert.Empty(image.Encounter.HealingRows);
        Assert.Single(image.Encounter.Participants);
    }
}
