using EQOverwatch.Core.Discord;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V115Tests
{
    private static readonly DateTime Kill = new(2026, 11, 18, 2, 27, 0, DateTimeKind.Utc);

    [Fact]
    public void Raid_keys_are_found_in_raid_chat_with_their_kind()
    {
        string key = RaidKey.New(HookKind.Unlooted);
        Assert.StartsWith("OWU-", key, StringComparison.Ordinal);
        Assert.True(RaidKey.IsValid(key, HookKind.Unlooted));
        Assert.False(RaidKey.IsValid(key, HookKind.Kills));

        var found = RaidKey.Find("loot here owk-7qm2-xpah and OWL-H3LW-9TFD please").ToList();
        Assert.Equal(new[] { ("OWK-7QM2-XPAH", HookKind.Kills), ("OWL-H3LW-9TFD", HookKind.Loot) }, found);
    }

    [Fact]
    public void A_characters_keys_end_when_it_leaves_the_raid()
    {
        var sink = new RaidDiscordSink();
        sink.OnLine("Tankard", "Mendwell tells the raid, 'kills go to OWK-7QM2-XPAH'", Kill);
        sink.OnLine("Gearbox", "You tell your raid, 'OWK-7QM2-XPAH'", Kill);
        Assert.Equal(new[] { "OWK-7QM2-XPAH" }, sink.Keys.Active(HookKind.Kills));

        sink.OnLine("Tankard", "You were removed from the raid.", Kill);
        Assert.Single(sink.Keys.Active(HookKind.Kills)); // Gearbox is still in it
        sink.OnLine("Gearbox", "Your raid was disbanded.", Kill);
        Assert.Empty(sink.Keys.Active(HookKind.Kills));
    }

    [Fact]
    public void Loot_list_lines_are_split_and_counted()
    {
        Assert.Equal(new[] { ("Steel Skin Armplates", 3), ("Bracer of Black Blood", 1), ("Cloak of the Black Wind", 1), ("Cap of the Silent Star", 2) },
            RaidLootLines.LootList("Steel Skin Armplates x3// Bracer of Black Blood // Cloak of the Black Wind / Cap of the Silent Star x2"));
        Assert.Empty(RaidLootLines.LootList("2-2-2 CAST NOW"));
        Assert.Empty(RaidLootLines.LootList("Elias, Tironicus, Ream, Bandaids"));
    }

    [Fact]
    public void A_burst_of_loot_lines_from_one_player_is_one_post_with_repeats_counted()
    {
        var watch = new RaidLootWatch();
        watch.OnRaidBossKill("Lord Inquisitor Seru", Kill);
        watch.OnLine("Tankard", "Porthios tells the raid, 'Thorny Chain Helm // Gebron's Demented Cloak'", Kill.AddSeconds(30));
        watch.OnLine("Gearbox", "Porthios tells the raid, 'Thorny Chain Helm // Gebron's Demented Cloak'", Kill.AddSeconds(30)); // another box's log
        watch.OnLine("Tankard", "Porthios tells the raid, 'Thorny Chain Helm // Wand of Tranquility x2'", Kill.AddSeconds(31));
        Assert.Empty(watch.Tick(Kill.AddSeconds(35)));

        LootListPost post = Assert.IsType<LootListPost>(Assert.Single(watch.Tick(Kill.AddSeconds(42))));
        Assert.Equal("Lord Inquisitor Seru", post.Mob);
        Assert.Equal("Porthios", post.Poster);
        Assert.Equal(new[] { ("Thorny Chain Helm", 2), ("Gebron's Demented Cloak", 1), ("Wand of Tranquility", 2) }, post.Items);
    }

    [Fact]
    public void Loot_long_after_a_raid_boss_kill_is_not_posted()
    {
        var watch = new RaidLootWatch();
        watch.OnLine("Tankard", "Porthios tells the raid, 'Thorny Chain Helm // Gebron's Demented Cloak'", Kill);
        watch.OnRaidBossKill("Lord Inquisitor Seru", Kill);
        watch.OnLine("Tankard", "A Meteor Dust was given to Mendwell.", Kill.AddMinutes(16));
        Assert.Empty(watch.Tick(Kill.AddMinutes(20)).OfType<UnlootedPost>());
    }

    [Fact]
    public void An_item_given_and_not_looted_within_a_minute_is_posted_once()
    {
        var watch = new RaidLootWatch();
        watch.OnRaidBossKill("Lord Inquisitor Seru", Kill);
        watch.OnLine("Tankard", "A Head of the Inquisitor was given to Shieldwall.", Kill.AddSeconds(60));
        watch.OnLine("Tankard", "A Ring of Flamewarding was given to Mendwell.", Kill.AddSeconds(61));
        watch.OnLine("Tankard", "--Mendwell has looted a Ring of Flamewarding from Lord Inquisitor Seru's corpse.--", Kill.AddSeconds(62));
        Assert.Empty(watch.Tick(Kill.AddSeconds(100)));

        UnlootedPost post = Assert.IsType<UnlootedPost>(Assert.Single(watch.Tick(Kill.AddSeconds(121))));
        Assert.Equal(("Head of the Inquisitor", "Shieldwall"), (post.Item, post.Player));
        Assert.Empty(watch.Tick(Kill.AddSeconds(200)));
    }

    [Fact]
    public void The_reminder_at_14_minutes_lists_what_is_still_on_the_corpse_and_is_skipped_when_all_was_looted()
    {
        var watch = new RaidLootWatch();
        watch.OnRaidBossKill("Lord Inquisitor Seru", Kill);
        watch.OnLine("Tankard", "No one was interested in the 1 item(s): Glowing Orb of Luclinite. These items can be randomed again or will be available to everyone after the corpse unlocks.", Kill.AddSeconds(50));
        watch.OnLine("Tankard", "A Head of the Inquisitor was given to you.", Kill.AddSeconds(60));
        watch.Tick(Kill.AddMinutes(5));

        LootReminderPost reminder = Assert.IsType<LootReminderPost>(Assert.Single(watch.Tick(Kill.AddMinutes(14))));
        Assert.Equal(new[] { ("Head of the Inquisitor", 1), ("Glowing Orb of Luclinite", 1) }, reminder.Items);

        var allLooted = new RaidLootWatch();
        allLooted.OnRaidBossKill("Lord Inquisitor Seru", Kill);
        allLooted.OnLine("Tankard", "A Head of the Inquisitor was given to you.", Kill.AddSeconds(60));
        allLooted.OnLine("Tankard", "--You have looted a Head of the Inquisitor from Lord Inquisitor Seru's corpse.--", Kill.AddSeconds(61));
        Assert.Empty(allLooted.Tick(Kill.AddMinutes(14)));
    }

    [Fact]
    public void The_server_kill_announcement_names_the_raid_boss()
    {
        Assert.True(RaidBossAnnouncementParser.TryParse(
            "Lord Inquisitor Seru has been defeated by a group of hardy adventurers! Please join us in congratulating Abundancei along with everyone else who participated in this achievement!",
            out string boss));
        Assert.Equal("Lord Inquisitor Seru", boss);
        Assert.False(RaidBossAnnouncementParser.TryParse("Lord Inquisitor Seru was slain by Slingi!", out _));
    }

    [Fact]
    public void Posts_carry_only_the_fields_the_server_writes_the_message_from()
    {
        var post = new LootReminderPost("Lord Inquisitor Seru", new[] { ("Head of the Inquisitor", 1) }, Kill);
        Dictionary<string, object> payload = HttpDiscordRelay.Payload(post);
        Assert.Equal("reminder", payload["type"]);
        Assert.Equal(new DateTimeOffset(Kill).ToUnixTimeSeconds(), payload["t"]);
        Assert.True(HttpDiscordRelay.IsWebhook("https://discord.com/api/webhooks/123456789012/abcdefghijklmnopqrstuvwxyz_-0123"));
        Assert.False(HttpDiscordRelay.IsWebhook("https://example.com/api/webhooks/123456789012/abcdefghijklmnopqrstuvwxyz"));
    }
}
