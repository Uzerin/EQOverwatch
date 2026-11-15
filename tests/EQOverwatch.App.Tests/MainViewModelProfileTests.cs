using System.IO;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Models;
using Xunit;

namespace EQOverwatch.App.Tests;

/// <summary>The Inventory / Profile window (with Skills, Tradeskills and Faction) and the Tells tab.</summary>
public partial class MainViewModelTests
{
    [Fact]
    public async Task Inventory_profile_opens_on_inventory_with_skills_tradeskills_faction_and_forget()
    {
        using var h = new Harness();
        await h.Vm.ApplyEqFolderAsync(h.Dir);
        Directory.CreateDirectory(Path.Combine(h.Dir, "Resources"));
        File.WriteAllLines(Path.Combine(h.Dir, "Resources", "skillcaps.txt"), new[] { "2^31^60^235^0^", "2^33^60^200^0^" });
        h.Dialogs.LogFiles = new[] { h.CreateLog("Ream") };
        h.Vm.AddLogCommand.Execute(null);
        WatchedLogConfig ream = h.Vm.Logs.Single();
        string server = ream.ServerName;
        h.Pipeline.Roster.LearnPlayer("Ream", "Cleric", 60, server);

        DateTime now = DateTime.UtcNow;
        h.Pipeline.Progress.OnLine("Ream", server, "You have become better at Offense! (199)", now);
        h.Pipeline.Progress.OnLine("Ream", server, "You have become better at Meditate! (235)", now);
        h.Pipeline.Progress.OnLine("Ream", server, "You have fashioned the items together to create something new: Ring of the Shissar.", now);
        h.Pipeline.Progress.OnLine("Ream", server, "Your faction standing with Clerics of Tunare has been adjusted by 50.", now);

        h.Vm.OpenProfileCommand.Execute(ream);

        CharacterDataViewModel window = Assert.Single(h.CharacterPresenter.Shown); // opens even with no /outputfile data yet
        Assert.Empty(h.CharacterPresenter.Messages);
        Assert.Equal(CharacterDataViewModel.InventoryTab, window.SelectedTabIndex);
        Assert.True(window.CanForget);

        window.SelectedTabIndex = CharacterDataViewModel.SkillsTab;
        Assert.Equal(new[] { "Meditate", "Offense" }, window.SkillRows.Select(r => r.Skill)); // A to Z, like the game
        Assert.Equal(("235", true), (window.SkillRows[0].Max, window.SkillRows[0].IsMaxed));
        Assert.Equal(("200", "+1"), (window.SkillRows[1].Max, window.SkillRows[1].Today));
        window.OnlyUnmaxedSkills = true;
        Assert.Equal("Offense", window.SkillRows.Single().Skill);

        TradeskillRowView recipe = window.TradeskillRows.Single();
        Assert.Equal(("Ring of the Shissar", 1, "100%"), (recipe.Recipe, recipe.Made, recipe.Success));
        FactionRowView faction = window.FactionRows.Single();
        Assert.Equal(("Clerics of Tunare", "+50", "+50", "+50"), (faction.Faction, faction.Session, faction.AllTime, faction.UsualHit));
    }

    [Fact]
    public void The_tells_tab_lists_unanswered_tells_and_keeps_them_for_the_days_chosen()
    {
        using var h = new Harness();
        DateTime now = DateTime.UtcNow;
        h.Pipeline.TellInbox.OnLine("Stackprime", "frostreaver", "Mommiessubby tells you, 'can you buy me enduring breath'", now);
        h.Pipeline.TellInbox.OnLine("Reamii", "frostreaver", "Kelanor tells you, 'lfg?'", now.AddSeconds(5));
        h.Pipeline.TellInbox.OnLine("Reamii", "frostreaver", "You told Kelanor, 'not now'", now.AddSeconds(9));

        h.Vm.TellShow = MainViewModel.TellsAll;
        Assert.Equal(new[] { "Kelanor", "Mommiessubby" }, h.Vm.TellRows.Select(r => r.From));
        Assert.Equal(new[] { "Yes", "" }, h.Vm.TellRows.Select(r => r.Answered));

        h.Vm.TellShow = MainViewModel.TellsUnanswered;
        TellRowView waiting = Assert.Single(h.Vm.TellRows);
        Assert.Equal(("Stackprime", "Mommiessubby"), (waiting.To, waiting.From));
        Assert.StartsWith("1 unanswered tell.", h.Vm.TellStatus, StringComparison.Ordinal);
        Assert.Contains("Reamii", h.Vm.TellCharacters);

        h.Vm.MarkAllTellsReadCommand.Execute(null);
        Assert.Empty(h.Vm.TellRows);
        Assert.Equal("No unanswered tells.", h.Vm.TellStatus);

        h.Vm.TellKeepIndex = 3; // Always
        Assert.Equal(0, h.Pipeline.TellInbox.KeepDays);
        Assert.Equal(0, h.Saved().TellKeepDays);
    }
}
