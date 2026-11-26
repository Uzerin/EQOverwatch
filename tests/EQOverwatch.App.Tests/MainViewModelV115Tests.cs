using EQOverwatch.Core.Discord;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void The_kill_announcement_ticks_a_raid_boss_unless_the_player_unticked_it()
    {
        using var h = new Harness();
        Assert.False(h.Vm.IsRaidBoss("Lord Inquisitor Seru"));

        h.Pipeline.LiveDps.OnNonCombatMessage("Lord Inquisitor Seru has been defeated by a group of hardy adventurers! Please join us in congratulating Tankard along with everyone else who participated in this achievement!");
        h.Vm.Refresh();
        Assert.True(h.Vm.IsRaidBoss("Lord Inquisitor Seru"));

        h.Vm.SetRaidBoss("Lord Inquisitor Seru", false);
        h.Pipeline.LiveDps.OnNonCombatMessage("Lord Inquisitor Seru has been defeated by a group of hardy adventurers! Please join us in congratulating Tankard along with everyone else who participated in this achievement!");
        h.Vm.Refresh();
        Assert.False(h.Vm.IsRaidBoss("Lord Inquisitor Seru"));

        h.Vm.SetRaidBoss("Praesertum Vantorus", true);
        var saved = h.Vm.BuildSettings();
        Assert.Equal(new[] { "Praesertum Vantorus" }, saved.RaidBosses);
        Assert.Equal(new[] { "Lord Inquisitor Seru" }, saved.RaidBossesUnticked);
    }

    [Fact]
    public void Each_webhook_has_its_own_key_and_share_automatically_starts_off()
    {
        using var h = new Harness();
        var saved = h.Vm.BuildSettings();
        Assert.True(RaidKey.IsValid(saved.DiscordKillsKey, HookKind.Kills));
        Assert.True(RaidKey.IsValid(saved.DiscordLootKey, HookKind.Loot));
        Assert.True(RaidKey.IsValid(saved.DiscordUnlootedKey, HookKind.Unlooted));
        Assert.False(saved.DiscordKillsAuto || saved.DiscordLootAuto || saved.DiscordUnlootedAuto);
    }
}
