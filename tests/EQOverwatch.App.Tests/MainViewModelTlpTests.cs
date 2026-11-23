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
