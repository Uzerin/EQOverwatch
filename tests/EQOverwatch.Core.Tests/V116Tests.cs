using EQOverwatch.Core.Discord;
using EQOverwatch.Core.Progress;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V116Tests
{
    private static readonly DateTime Now = new(2026, 11, 26, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_raid_key_ends_6_hours_after_it_was_said_or_a_raid_boss_died_whichever_is_later()
    {
        var sink = new RaidDiscordSink();
        sink.OnLine("Tankard", "Mendwell tells the raid, 'OWK-7QM2-XPAH'", Now);
        for (int minutes = 20; minutes <= 7 * 60; minutes += 20)
        {
            sink.OnLine("Tankard", "Mendwell tells the guild, 'still here'", Now.AddMinutes(minutes)); // an AFK character's log keeps writing
        }

        Assert.Single(sink.Keys.Active(HookKind.Kills, Now.AddHours(5)));
        sink.Keys.BossKilled(Now.AddHours(5));
        Assert.Single(sink.Keys.Active(HookKind.Kills, Now.AddHours(7)));
        Assert.Empty(sink.Keys.Active(HookKind.Kills, Now.AddHours(11.5)));
    }

    [Fact]
    public void A_raid_key_ends_when_the_character_joins_a_raid_logs_in_or_its_log_goes_quiet()
    {
        foreach (string line in new[] { "You have joined the raid.", "Welcome to EverQuest!" })
        {
            var sink = new RaidDiscordSink();
            sink.OnLine("Tankard", "Mendwell tells the raid, 'OWL-H3LW-9TFD'", Now);
            sink.OnLine("Tankard", line, Now.AddMinutes(1));
            Assert.Empty(sink.Keys.Active(HookKind.Loot, Now.AddMinutes(2)));
        }

        var quiet = new RaidDiscordSink();
        quiet.OnLine("Tankard", "Mendwell tells the raid, 'OWL-H3LW-9TFD'", Now);
        Assert.Single(quiet.Keys.Active(HookKind.Loot, Now.AddMinutes(29)));
        Assert.Empty(quiet.Keys.Active(HookKind.Loot, Now.AddMinutes(31)));
    }

    [Fact]
    public void Zoning_and_camping_are_reported_for_the_loot_unlock_timer()
    {
        var sink = new RaidDiscordSink();
        sink.OnLine("Tankard", "You have entered Ssraeshza Temple.", Now);
        sink.OnLine("Gearbox", "It will take you about 30 seconds to prepare your camp.", Now);
        Assert.True(sink.Moves.TryDequeue(out var zoned) && zoned == ("Tankard", "Ssraeshza Temple"));
        Assert.True(sink.Moves.TryDequeue(out var camped) && camped == ("Gearbox", null));
    }

    [Fact]
    public void Unlooted_items_stay_on_the_overlay_until_looted_or_the_corpse_unlocks()
    {
        var watch = new RaidLootWatch();
        watch.OnRaidBossKill("Lord Inquisitor Seru", Now);
        watch.OnLine("Tankard", "A Head of the Inquisitor was given to Shieldwall.", Now.AddSeconds(30));
        var shown = Assert.Single(watch.CurrentUnlooted(Now.AddMinutes(5)));
        Assert.Equal(("Head of the Inquisitor", "Shieldwall", Now.AddMinutes(15)), (shown.Item, shown.Player, shown.Unlocks));
        Assert.Empty(watch.CurrentUnlooted(Now.AddMinutes(15)));

        watch.OnLine("Tankard", "--Shieldwall has looted a Head of the Inquisitor from Lord Inquisitor Seru's corpse.--", Now.AddMinutes(6));
        Assert.Empty(watch.CurrentUnlooted(Now.AddMinutes(7)));

        var feed = new OverlayNoticeFeed();
        feed.Sync("unlooted", new[] { ("Not looted: Head of the Inquisitor → Shieldwall", Now, Now.AddMinutes(15)) });
        Assert.Single(feed.Current(Now.AddMinutes(10)));
        feed.Sync("unlooted", Array.Empty<(string, DateTime, DateTime)>());
        Assert.Empty(feed.Current(Now.AddMinutes(10)));
    }
}
