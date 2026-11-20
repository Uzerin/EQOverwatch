using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Follow_and_pet_buff_alerts_are_spoken_for_your_characters_and_rolls_and_linkdead_go_to_the_tells_overlay()
    {
        (Harness h, List<string> spoken) = BoxHarness();
        using (h)
        {
            var toasts = new List<(NoticeInfo Notice, bool OnlyWhenAway)>();
            h.Vm.LiveNoticeRequested += (n, away) => toasts.Add((n, away));
            h.Pipeline.LiveDps.OnLogOwner("Myriad");
            h.Pipeline.LiveDps.OnNonCombatMessage("You are no longer auto-following Stackprime.");
            h.Pipeline.LiveDps.OnNonCombatMessage("You are no longer auto-following Stackprime."); // too soon
            h.Pipeline.LiveDps.OnNonCombatMessage("Your pet's Storm Strength spell has worn off."); // off by default
            h.Pipeline.LiveDps.OnNonCombatMessage("Beatrixe has gone Linkdead.");
            h.Pipeline.LiveDps.OnNonCombatMessage("**A Magic Die is rolled by Honshu. It could have been any number from 0 to 100, but this time it turned up a 66.");
            h.Pipeline.LiveDps.OnLogOwner("Plethora");
            h.Pipeline.LiveDps.OnNonCombatMessage("**A Magic Die is rolled by Honshu. It could have been any number from 0 to 100, but this time it turned up a 66.");
            h.Pipeline.LiveDps.OnNonCombatMessage("You have gained a level! Welcome to level 58!");
            h.Vm.PetBuffAlert = true;
            h.Pipeline.LiveDps.OnNonCombatMessage("Your pet's Storm Strength spell has worn off.");

            Assert.Equal(new[] { "Myriad stopped following", "pet Storm Strength faded" }, spoken);
            Assert.Equal(new[] { "Roll  Honshu  66  (0–100)", "Beatrixe went linkdead" },
                h.Pipeline.Notices.Current(MainViewModelTests.FightStart.AddSeconds(1)).Select(n => n.Text));
            (NoticeInfo notice, bool away) = Assert.Single(toasts);
            Assert.Equal(("Plethora reached level 58", true), (notice.Title, away));

            AppSettings saved = h.Saved();
            Assert.True(saved.PetBuffAlert);
            Assert.True(saved.FollowAlert && saved.LevelUpToast && saved.TellsOverlayNotices);
        }
    }

    [Fact]
    public void Bonus_xp_shows_on_the_leveling_tab_and_notifies_once()
    {
        using var h = new Harness();
        var toasts = new List<NoticeInfo>();
        h.Vm.LiveNoticeRequested += (n, _) => toasts.Add(n);
        for (int i = 0; i < 4; i++)
        {
            h.Vm.BonusXp.Observe(1.6, 5, FightStart.AddMinutes(i));
        }

        Assert.StartsWith("Bonus XP looks active (+60%", h.Vm.BonusXpText, StringComparison.Ordinal);
        Assert.Equal("Bonus XP looks active", Assert.Single(toasts).Title);
    }

    [Fact]
    public void Only_killed_mobs_that_are_no_known_player_are_shared()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime") };
        h.Vm.AddLogCommand.Execute(null);
        var killed = new HashSet<string>(new[] { "Lord Vyemm", "Stackprime`s warder" }, StringComparer.OrdinalIgnoreCase);
        Assert.True(h.Vm.IsShareableMob("lord vyemm", killed));
        Assert.False(h.Vm.IsShareableMob("a sarnak dragoon", killed)); // never killed by the raid
        Assert.False(h.Vm.IsShareableMob("Marfen Binkdirple", killed));
        Assert.False(h.Vm.IsShareableMob("Stackprime`s warder", killed)); // a pet
    }
}
