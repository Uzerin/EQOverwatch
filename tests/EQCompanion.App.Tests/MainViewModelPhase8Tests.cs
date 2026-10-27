using EQCompanion.App.ViewModels;
using EQCompanion.Core.Models;
using Xunit;

namespace EQCompanion.App.Tests;

public partial class MainViewModelTests
{
    /// <summary>Types <paramref name="name"/> into the tracker's search box and returns the matching suggestion.</summary>
    private static SpellSuggestion Find(Harness h, string name)
    {
        h.Vm.SelectedSuggestion = null;
        h.Vm.TrackerSearchText = string.Empty;
        h.Vm.TrackerSearchText = name;
        return h.Vm.TrackerSuggestions.First(s => s.Name == name);
    }

    /// <summary>Picks <paramref name="name"/> in the search box, chooses the overlay and presses Add to Tracker.</summary>
    private static void AddTracked(Harness h, string name, TimerOverlayCategory overlay)
    {
        h.Vm.SelectedSuggestion = Find(h, name);
        h.Vm.TrackerOverlay = overlay;
        h.Vm.AddTrackedSpellCommand.Execute(null);
    }

    // ───────────────────────────── tracker activity ─────────────────────────────

    [Fact]
    public async Task The_activity_line_reports_bars_started_by_tracked_spells()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);
        AddTracked(h, "Spirit of Wolf", TimerOverlayCategory.Buffs);
        h.Vm.Refresh();
        Assert.Contains("No tracked buff", h.Vm.TrackerActivity);

        h.Pipeline.Buffs.OnLine("Stackprime", "You feel quick as a wolf.", DateTime.UtcNow);
        h.Vm.Refresh();

        Assert.Contains("1 bar", h.Vm.TrackerActivity);
        Assert.Contains("Stackprime: Spirit of Wolf", h.Vm.TrackerActivity);
    }

    [Fact]
    public async Task The_picked_spells_landing_and_fade_text_is_previewed()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);

        h.Vm.SelectedSuggestion = Find(h, "Selo's Accelerando");

        Assert.Contains("You feel your pace quicken.", h.Vm.TrackerPreview);
        Assert.Contains("pace returns to normal.", h.Vm.TrackerPreview);
    }

    // ───────────────────────────── raid triggers: boss drop-down ─────────────────────────────

    [Fact]
    public void The_boss_list_holds_mobs_from_ended_fights_with_counts_and_filters_as_you_type()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 1000, 1));
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", "a gnoll", 50, 30)); // 29 seconds later: the giant fight has ended

        h.Vm.Refresh();

        KnownMobOption giant = Assert.Single(h.Vm.MobSuggestions); // the gnoll fight is still in progress
        Assert.Equal(Giant, giant.Name);
        Assert.Contains("1 fight", giant.Detail);

        h.Vm.RaidMobName = "gia";
        Assert.Equal(Giant, Assert.Single(h.Vm.MobSuggestions).Name);

        h.Vm.RaidMobName = "zzz";
        Assert.Empty(h.Vm.MobSuggestions);
        Assert.Contains("New name", h.Vm.RaidMobHint);

        h.Vm.RaidMobName = Giant; // picking an entry
        Assert.Contains("1 fight", h.Vm.RaidMobHint);
    }

    [Fact]
    public void A_custom_boss_name_can_be_used_for_a_first_time_fight()
    {
        using var h = new Harness();
        h.Vm.RaidMobName = "Lord Nagafen";
        h.Vm.RaidSpellName = "Dragon Roar";
        h.Vm.RaidMatchText = "blasted by a roar";
        h.Vm.RaidRecastSeconds = 48;

        h.Vm.AddRaidTriggerCommand.Execute(null);

        RaidTimerRuleViewModel row = Assert.Single(h.Vm.RaidRules);
        Assert.Equal("Lord Nagafen", row.MobText);
        Assert.Equal("Lord Nagafen", Assert.Single(h.Saved().RaidBossTimers).MobName);
    }

    [Fact]
    public void An_empty_boss_box_means_any_boss()
    {
        using var h = new Harness();

        Assert.Contains("any boss", h.Vm.RaidMobHint);
    }
}
