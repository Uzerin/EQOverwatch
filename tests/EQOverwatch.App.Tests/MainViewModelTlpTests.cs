using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Tlp;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Ruleset_lines_say_what_is_next_and_what_is_in_effect()
    {
        (string next, string now) = MainViewModel.RulesetLines(TlpServers.Find("frostreaver"), new DateOnly(2026, 11, 22));
        Assert.StartsWith("Next: The Planes of Power + The Legacy of Ykesha on ", next);
        Assert.Contains("(74 days) · level cap 65", next);
        Assert.Equal("Now: level cap 60 · EXP 100% · Loot 125% · Faction 200% · AA EXP 175% · hot zones with The Serpent's Spine · mercenaries with Seeds of Destruction", now);
        Assert.Equal((string.Empty, string.Empty), MainViewModel.RulesetLines(TlpServers.Find("bristlebane"), new DateOnly(2026, 11, 22)));
    }

    [Fact]
    public void Legacy_bonus_comes_from_the_log_or_another_character_on_the_account()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Ream"), h.CreateLog("Mendwell"), h.CreateLog("Porter") };
        h.Vm.AddLogCommand.Execute(null);
        string server = h.Vm.Logs.First().ServerName;
        DateTime seen = new(2026, 11, 18, 19, 0, 0, DateTimeKind.Utc);
        h.Pipeline.Progress.OnLine("Ream", server, "Your legacy server experience bonus for reaching the maximum level has increased to 100%!", seen);
        h.Vm.SetAccount("Ream", server, "Main");
        h.Vm.SetAccount("Mendwell", server, "Main");

        string date = seen.ToLocalTime().ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
        Assert.Equal($"100% ({date})", h.Vm.LegacyBonusText("Ream"));
        Assert.Equal($"100% (from Ream, {date})", h.Vm.LegacyBonusText("Mendwell"));
        Assert.Equal("—", h.Vm.LegacyBonusText("Porter")); // no line and no account
    }
}

public partial class MainViewModelTests
{
    [Fact]
    public void Challenges_show_per_character_from_its_own_achievements()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Ream"), h.CreateLog("Mendwell") };
        h.Vm.AddLogCommand.Execute(null);
        string server = h.Vm.Logs.First().ServerName;
        h.Pipeline.Progress.OnLine("Ream", server, "You have completed achievement: Cazic-Thule Challenge", new DateTime(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc));

        var ream = h.Vm.ChallengeRowsFor("Ream", server);
        Assert.Equal(33, ream.Count);
        Assert.StartsWith("Done (", ream.Single(r => r.Goal == "Cazic-Thule").Status);
        Assert.Equal("Still possible", h.Vm.ChallengeRowsFor("Mendwell", server).Single(r => r.Goal == "Cazic-Thule").Status); // per character
    }

    [Fact]
    public void Tlp_tabs_go_newest_first_and_resource_hunter_lists_only_its_servers()
    {
        Assert.Equal(new[] { "lethar", "frostreaver", "fangbreaker" }, TlpServers.All.OrderByDescending(s => s.Launch).Take(3).Select(s => s.Key));
        using var h = new Harness();
        Assert.Equal(new[] { "frostreaver", "fangbreaker" }, h.Vm.ResourceServers.Order().Reverse());
        var log = new EQOverwatch.Core.Models.WatchedLogConfig("eqlog_Tankard_frostreaver.txt") { ClassName = "Paladin" };
        Assert.False(log.CanChainHeal);
        log.ClassName = "Cleric";
        Assert.True(log.CanChainHeal);
    }

    [Fact]
    public void A_new_draw_forgets_the_days_bonuses()
    {
        using var h = new Harness();
        DateTime now = DateTime.UtcNow;
        h.Pipeline.ResourceHunter.OnLine("Ream", "frostreaver", "You have entered The Akheva Ruins.", now);
        h.Pipeline.ResourceHunter.OnLine("Ream", "frostreaver", "This land is saturated with unstable magic, multiplying the loot that may be found here.", now);
        Assert.Single(h.Pipeline.ResourceHunter.Today("frostreaver", now));

        h.Pipeline.ResourceHunter.OnLine("Ream", "frostreaver", "Fonts of unstable magic are erupting and receding in response to some nameless power.", now);
        Assert.Empty(h.Pipeline.ResourceHunter.Today("frostreaver", now));
    }
}
