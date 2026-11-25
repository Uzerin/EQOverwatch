using System.IO;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    private static readonly DateTime FightStart = new(2026, 11, 16, 21, 18, 0);

    /// <summary>A harness watching Stackprime (a warrior), Plethora and Myriad, with every spoken text collected.</summary>
    private static (Harness H, List<string> Spoken) BoxHarness()
    {
        var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime"), h.CreateLog("Plethora"), h.CreateLog("Myriad") };
        h.Vm.AddLogCommand.Execute(null);
        h.Vm.Logs.Single(l => l.CharacterName == "Stackprime").ClassName = "Warrior";
        var spoken = new List<string>();
        h.Pipeline.Triggers.TriggerFired += e =>
        {
            if (e.AudioText is { } text)
            {
                spoken.Add(text);
            }
        };
        h.Vm.BoxAlertClock = () => FightStart;
        return (h, spoken);
    }

    private static void StartFight(Harness h, string mob = "Emperor Ssraeshza", int second = 0)
    {
        h.Pipeline.LiveDps.OnLogOwner("Stackprime");
        h.Pipeline.LiveDps.OnCombatEvent(new CombatEvent(FightStart.AddSeconds(second), CombatEventKind.Damage, "Stackprime", mob, 100, DamageKind.Melee, "slash"));
    }

    private static void MobHits(Harness h, string mob, string player, int second, bool rampage = false)
    {
        h.Pipeline.LiveDps.OnLogOwner(player);
        h.Pipeline.LiveDps.OnCombatEvent(new CombatEvent(
            FightStart.AddSeconds(second), CombatEventKind.Damage, mob, player, 300, DamageKind.Melee, "punches", IsRampage: rampage));
    }

    [Fact]
    public void A_mob_attacking_a_box_that_is_not_a_tank_is_spoken_and_shown_once_every_fifteen_seconds()
    {
        (Harness h, List<string> spoken) = BoxHarness();
        using (h)
        {
            StartFight(h);
            MobHits(h, "Emperor Ssraeshza", "Stackprime", 1); // the tank: no alert
            MobHits(h, "Emperor Ssraeshza", "Plethora", 1, rampage: true); // a rampage: no alert
            MobHits(h, "Emperor Ssraeshza", "Plethora", 2);
            MobHits(h, "Emperor Ssraeshza", "Plethora", 3); // too soon after the last one
            MobHits(h, "Emperor Ssraeshza", "Ugli", 3); // not one of your characters

            Assert.Equal(new[] { "Plethora is being attacked by Emperor Ssraeshza" }, spoken);
            Assert.Contains(h.Pipeline.Triggers.Snapshot.Timers, t => t.Text == "Plethora is being attacked by Emperor Ssraeshza");

            h.Vm.BoxAlertClock = () => FightStart.AddSeconds(16);
            StartFight(h, second: 10); // the tank keeps the fight going
            StartFight(h, second: 16);
            MobHits(h, "Emperor Ssraeshza", "Plethora", 16);
            Assert.Equal(2, spoken.Count);

            h.Vm.Logs.Single(l => l.CharacterName == "Plethora").IsTank = true; // ticked on the Characters tab
            h.Vm.BoxAlertClock = () => FightStart.AddSeconds(40);
            StartFight(h, second: 24);
            StartFight(h, second: 32);
            StartFight(h, second: 40);
            MobHits(h, "Emperor Ssraeshza", "Plethora", 40);
            Assert.Equal(2, spoken.Count);

            h.Vm.AggroAlert = false;
            MobHits(h, "Emperor Ssraeshza", "Myriad", 41);
            Assert.Equal(2, spoken.Count);

            AppSettings saved = h.Saved();
            Assert.False(saved.AggroAlert);
            Assert.True(saved.WatchedLogs.Single(l => l.CharacterName == "Plethora").IsTank);
            Assert.Null(saved.WatchedLogs.Single(l => l.CharacterName == "Stackprime").IsTank); // still following its class
        }
    }

    [Fact]
    public void A_box_that_cannot_reach_its_target_is_spoken_once_every_twenty_seconds()
    {
        (Harness h, List<string> spoken) = BoxHarness();
        using (h)
        {
            StartFight(h, "a gnome");
            h.Pipeline.LiveDps.OnLogOwner("Myriad");
            h.Pipeline.LiveDps.OnNonCombatMessage("Your target is too far away, get closer!");
            h.Pipeline.LiveDps.OnNonCombatMessage("Your target is too far away, get closer!");
            h.Vm.BoxAlertClock = () => FightStart.AddSeconds(21);
            h.Pipeline.LiveDps.OnNonCombatMessage("You cannot see your target.");

            Assert.Equal(new[] { "Myriad out of range", "Myriad can't see the target" }, spoken);
        }
    }

    [Fact]
    public void Warriors_paladins_and_shadow_knights_are_tanks_until_changed()
    {
        Assert.True(WatchedLogConfig.IsTankClass("Shadow Knight"));
        Assert.True(WatchedLogConfig.IsTankClass("Paladin"));
        Assert.False(WatchedLogConfig.IsTankClass("Ranger"));

        var log = new WatchedLogConfig(Path.Combine(Path.GetTempPath(), "eqlog_Stackprime_frostreaver.txt")) { ClassName = "Warrior" };
        Assert.True(log.IsTank);
        log.IsTank = false;
        Assert.False(log.IsTank);
        Assert.False(log.TankChoice);
    }

    [Fact]
    public void Conning_a_mob_the_app_knows_shows_its_card_and_an_unknown_mob_shows_none()
    {
        (Harness h, _) = BoxHarness();
        using (h)
        {
            h.BossHistory.Record(new[]
            {
                Fight("Emperor Ssraeshza", FightStart.AddDays(-7), 372, 420_000, killed: true),
                Fight("Emperor Ssraeshza", FightStart.AddDays(-1), 400, 300_000, killed: false),
            });
            var cards = new List<MobCardInfo>();
            h.Vm.MobCardReady += cards.Add;

            h.Pipeline.LiveDps.OnLogOwner("Stackprime");
            h.Pipeline.LiveDps.OnNonCombatMessage("Emperor Ssraeshza scowls at you, ready to attack -- looks like he would wipe the floor with you! (Lvl: 70)");
            h.Pipeline.LiveDps.OnNonCombatMessage("A gnome regards you indifferently -- looks like quite a gamble. (Lvl: 10)");

            MobCardInfo card = Assert.Single(cards);
            Assert.Equal("Emperor Ssraeshza · level 70", card.Heading);
            Assert.Contains(card.Lines, l => l.StartsWith("Frostreaver: 1 kill, 1 attempt · fastest 6:12", StringComparison.Ordinal));

            h.Vm.IsMobCardOverlayVisible = false;
            h.Pipeline.LiveDps.OnNonCombatMessage("Emperor Ssraeshza scowls at you, ready to attack -- looks like he would wipe the floor with you! (Lvl: 70)");
            Assert.Single(cards);
        }
    }

    [Fact]
    public void The_mob_card_leaves_out_the_regen_estimate()
    {
        (Harness h, _) = BoxHarness();
        using (h)
        {
            h.BossHistory.Record(new[]
            {
                Fight("Xi Vius", FightStart.AddDays(-3), 40, 8_800, killed: true),
                Fight("Xi Vius", FightStart.AddDays(-2), 80, 9_600, killed: true),
                Fight("Xi Vius", FightStart.AddDays(-1), 120, 10_400, killed: true),
            });
            var cards = new List<MobCardInfo>();
            h.Vm.MobCardReady += cards.Add;

            h.Pipeline.LiveDps.OnLogOwner("Stackprime");
            h.Pipeline.LiveDps.OnNonCombatMessage("Xi Vius scowls at you, ready to attack -- looks kind of dangerous. (Lvl: 60)");

            MobCardInfo card = Assert.Single(cards);
            Assert.DoesNotContain(card.Lines, l => l.StartsWith("Regen", StringComparison.Ordinal));
            Assert.Contains(card.Lines, l => l.Contains("3 kills", StringComparison.Ordinal));
        }
    }

    private static EncounterSnapshot Fight(string mob, DateTime start, int seconds, long damage, bool killed) =>
        new(1, mob, start, start.AddSeconds(seconds), seconds, damage, false, Array.Empty<ParticipantSnapshot>(), Killed: killed) { Server = "Frostreaver" };

    [Fact]
    public void The_dps_overlay_shows_how_far_along_a_fight_is_from_two_recorded_kills()
    {
        using var h = new Harness();
        var live = new EncounterSnapshot(5, "Emperor Ssraeshza", FightStart, FightStart.AddSeconds(100), 100, 225_000, true,
            Array.Empty<ParticipantSnapshot>()) { Server = "Frostreaver" };
        h.BossHistory.Record(new[] { Fight("Emperor Ssraeshza", FightStart.AddDays(-7), 400, 450_000, killed: true) });
        Assert.Null(h.Vm.FightProgressFor(live));

        h.BossHistory.Record(new[] { Fight("Emperor Ssraeshza", FightStart.AddDays(-3), 380, 450_000, killed: true) });
        Assert.Equal("~50% · about 1:40 left (from 2 kills)", h.Vm.FightProgressFor(live)!.Describe());

        h.Vm.ShowFightProgress = false;
        Assert.Null(h.Vm.FightProgressFor(live));
    }

    [Fact]
    public void The_mob_card_goes_away_after_ten_seconds()
    {
        DateTime now = FightStart;
        var vm = new MobCardOverlayViewModel(() => now);
        vm.Show(new MobCardInfo("a gnome", "a gnome · level 10", new[] { "Resisted: Fire 50%" }));
        Assert.True(vm.HasCard);
        now = now.AddSeconds(9);
        vm.Tick();
        Assert.True(vm.HasCard);
        now = now.AddSeconds(2);
        vm.Tick();
        Assert.False(vm.HasCard);
    }
}
