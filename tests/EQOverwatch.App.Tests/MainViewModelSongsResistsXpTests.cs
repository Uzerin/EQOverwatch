using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Resists;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Spells;
using Xunit;

namespace EQOverwatch.App.Tests;

/// <summary>The all-bards and charm switches, resists on the Mob History tab, and the Experience tab.</summary>
public partial class MainViewModelTests
{
    [Fact]
    public void The_all_bards_and_charm_switches_reach_the_pipeline_and_are_saved()
    {
        using var h = new Harness((s, _) =>
        {
            s.ShowAllBardSongs = true;
            s.CharmWatchEnabled = false;
        });

        Assert.True(h.Vm.ShowAllBardSongs);
        Assert.True(h.Pipeline.Songs.ShowAllBards);
        Assert.False(h.Pipeline.Charm.IsEnabled);

        h.Vm.ShowAllBardSongs = false;
        h.Vm.CharmWatchEnabled = true;

        AppSettings saved = h.Saved();
        Assert.False(saved.ShowAllBardSongs);
        Assert.True(saved.CharmWatchEnabled);
        Assert.False(h.Pipeline.Songs.ShowAllBards);
        Assert.True(h.Pipeline.Charm.IsEnabled);
    }

    [Fact]
    public void Mob_history_shows_resist_percent_by_type_mine_or_raid_and_shares_a_token()
    {
        using var h = new Harness();
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < 4; i++)
        {
            h.Pipeline.ResistStore.AddOwn("Trakanon", "Ream", SpellResist.Poison, resisted: i == 0, now);
        }

        h.Pipeline.ResistStore.ApplyShare(new ResistShare("Trakanon", "Trick", now, new() { [SpellResist.Poison] = new ResistCount(6, 0) }));
        h.Vm.BossHistorySizeIndex = 0; // "Any size": mobs with only resist numbers are listed
        h.Vm.Refresh();

        BossHistoryRow row = Assert.Single(h.Vm.BossHistoryRows, r => r.Boss == "Trakanon");
        Assert.Equal(25.0.ToString("0.0") + "%", row.Poison);
        Assert.Equal("–", row.Magic);
        Assert.Equal("1 player", row.SharedBy);

        h.Vm.SelectedBossHistoryRow = row;
        Assert.Equal("Poison", Assert.Single(h.Vm.ResistTypeRows).Type);
        Assert.Contains("your resists", h.Vm.ResistPanelTitle);

        h.Vm.ResistRaidView = true;
        Assert.False(h.Vm.ResistMineView);
        BossHistoryRow raid = Assert.Single(h.Vm.BossHistoryRows, r => r.Boss == "Trakanon");
        Assert.Equal(10.0.ToString("0.0") + "%", raid.Poison); // 1 of 10
        Assert.Equal(2, h.Vm.ResistContributorRows.Count);

        h.Vm.SelectedBossHistoryRow = raid;
        h.Vm.ShareResistsCommand.Execute(null);
        Assert.Equal("{EQO:R1:Trakanon:P3-1}", h.Clipboard.Texts[^1]); // your own numbers only

        h.Vm.ShareAllResistsCommand.Execute(null);
        Assert.Equal("{EQO:R1:Trakanon:P3-1}", h.Clipboard.Texts[^1]);
    }

    [Fact]
    public void The_experience_tab_lists_what_each_character_gained()
    {
        using var h = new Harness();
        DateTime now = DateTime.UtcNow;
        h.Pipeline.Experience.OnLine("Ream", "You gain party experience (with a bonus)! (5.2%)", now);
        h.Pipeline.Experience.OnLine("Ream", "You have gained an ability point!  You now have 3 ability points.", now);

        h.Vm.Refresh();

        ExperienceRowView ream = Assert.Single(h.Vm.ExperienceRows);
        Assert.Equal("Ream", ream.Character);
        Assert.Equal(1, ream.Kills);
        Assert.Equal(1, ream.AaGained);
        Assert.Equal("1", h.Vm.ExperienceAaGained);

        h.Vm.ResetExperienceCommand.Execute(null);
        Assert.Empty(h.Vm.ExperienceRows);
    }
}
