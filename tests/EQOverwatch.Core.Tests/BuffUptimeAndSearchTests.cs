using System.IO.Compression;
using EQOverwatch.Core.Backup;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Loot;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Spells;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class BuffUptimeAndSearchTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 2, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Local0 = new(2026, 11, 2, 21, 0, 0);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static EncounterSnapshot Fight(DateTime start, double seconds) =>
        new(1, "a frost giant", start, start.AddSeconds(seconds), seconds, 1000, false, new List<ParticipantSnapshot>());

    // ───────────────────────── uptime ─────────────────────────

    [Fact]
    public void A_buff_that_covers_part_of_a_fight_counts_only_that_part()
    {
        var uptime = new BuffUptimeTracker();
        uptime.OnLanded("Stackprime", "Spirit of Wolf", 30, Local0.AddSeconds(10));

        SpellUptimeRow row = Assert.Single(uptime.Uptime(Fight(Local0, 60)));
        Assert.Equal("Stackprime", row.Target);
        Assert.Equal("Spirit of Wolf", row.Spell);
        Assert.Equal(30, row.Seconds, 3);
        Assert.Equal(50, row.Percent, 3);
    }

    [Fact]
    public void A_spell_that_runs_past_the_end_of_the_fight_is_cut_off_at_the_end()
    {
        var uptime = new BuffUptimeTracker();
        uptime.OnLanded("a frost giant", "Tashan", 120, Local0.AddSeconds(40));

        Assert.Equal(20, Assert.Single(uptime.Uptime(Fight(Local0, 60))).Seconds, 3);
    }

    [Fact]
    public void Fading_early_ends_the_stretch_and_landing_again_while_up_extends_it()
    {
        var uptime = new BuffUptimeTracker();
        uptime.OnLanded("Stackprime", "Spirit of Wolf", 100, Local0);
        uptime.OnFaded("Stackprime", "Spirit of Wolf", Local0.AddSeconds(20));
        Assert.Equal(20, Assert.Single(uptime.Uptime(Fight(Local0, 60))).Seconds, 3);

        var again = new BuffUptimeTracker();
        again.OnLanded("a frost giant", "Tashan", 30, Local0);
        again.OnLanded("a frost giant", "Tashan", 30, Local0.AddSeconds(20)); // re-cast before it ran out
        Assert.Equal(50, Assert.Single(again.Uptime(Fight(Local0, 60))).Seconds, 3);
    }

    [Fact]
    public void A_spell_that_was_up_outside_the_fight_is_not_listed()
    {
        var uptime = new BuffUptimeTracker();
        uptime.OnLanded("Stackprime", "Spirit of Wolf", 30, Local0.AddMinutes(-10));

        Assert.Empty(uptime.Uptime(Fight(Local0, 60)));
    }

    [Fact]
    public void The_longest_uptime_comes_first()
    {
        var uptime = new BuffUptimeTracker();
        uptime.OnLanded("a frost giant", "Tashan", 15, Local0);
        uptime.OnLanded("Stackprime", "Spirit of Wolf", 45, Local0);

        Assert.Equal(new[] { "Spirit of Wolf", "Tashan" }, uptime.Uptime(Fight(Local0, 60)).Select(r => r.Spell));
    }

    // ───────────────────────── tracker events ─────────────────────────

    private (BuffTrackerService Tracker, BuffUptimeTracker Uptime, TriggerEngineService Engine) Buffs(params int[] ids)
    {
        var spells = new SpellDatabaseService();
        spells.Load(BuffTrackerServiceTests.WriteSpellFile(Path.Combine(_dir, "spells_us.txt")));
        var engine = new TriggerEngineService();
        var tracker = new BuffTrackerService(spells, engine);
        tracker.SetTrackedSpells(ids);
        return (tracker, new BuffUptimeTracker(tracker), engine);
    }

    [Fact]
    public void A_landing_and_a_fade_reach_the_uptime_tracker()
    {
        var (tracker, uptime, _) = Buffs(100);
        DateTime start = DateTime.SpecifyKind(Local0, DateTimeKind.Unspecified);

        tracker.OnLine("Stackprime", "You feel quick as a wolf.", start);
        tracker.OnLine("Stackprime", "Your speed returns to normal.", start.AddSeconds(20));

        SpellUptimeRow row = Assert.Single(uptime.Uptime(Fight(start, 60)));
        Assert.Equal("Spirit of Wolf", row.Spell);
        Assert.Equal(20, row.Seconds, 3);
    }

    [Fact]
    public void A_debuff_on_the_mob_being_fought_is_counted_but_gets_no_bar_and_other_bystanders_are_still_ignored()
    {
        var (tracker, uptime, engine) = Buffs(100);
        tracker.SetAllowedTargets(new[] { "Stackprime" });
        tracker.SetBystanderProbe(name => name == "a frost giant");
        DateTime start = DateTime.SpecifyKind(Local0, DateTimeKind.Unspecified);

        tracker.OnLine("Stackprime", "a frost giant looks faster.", start);
        tracker.OnLine("Stackprime", "Oodles looks faster.", start);

        Assert.Empty(engine.Snapshot.Timers);
        SpellUptimeRow row = Assert.Single(uptime.Uptime(Fight(start, 60)));
        Assert.Equal("a frost giant", row.Target);
    }

    // ───────────────────────── backups ─────────────────────────

    [Fact]
    public void A_backup_leaves_out_the_discord_webhook_address_and_a_restore_keeps_this_pcs_own()
    {
        Directory.CreateDirectory(_dir);
        string data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"SyncCode\":\"\",\"DiscordWebhookUrl\":\"https://discord.com/api/webhooks/1/secret\",\"DiscordPostWipes\":true}");
        string zipPath = Path.Combine(_dir, "backup.zip");

        BackupService.Export(data, zipPath, "v1.0.43");

        using (ZipArchive zip = ZipFile.OpenRead(zipPath))
        using (var reader = new StreamReader(zip.GetEntry("settings.json")!.Open()))
        {
            string text = reader.ReadToEnd();
            Assert.DoesNotContain("secret", text);
            Assert.Contains("DiscordPostWipes", text);
        }

        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "settings.json"), "{\"DiscordWebhookUrl\":\"https://discord.com/api/webhooks/9/mine\"}");
        BackupService.StageRestore(zipPath, target);
        BackupService.ApplyPendingRestore(target);

        string restored = File.ReadAllText(Path.Combine(target, "settings.json"));
        Assert.Contains("mine", restored);
        Assert.Contains("DiscordPostWipes", restored);
    }

    // ───────────────────────── boss trend ─────────────────────────

    private static BossFight Kill(int day, double seconds, long damage = 600_000, bool killed = true) =>
        new("a Praetorian Pius", new DateTime(2026, 10, day, 21, 0, 0), seconds, damage, 24, 0, killed);

    [Fact]
    public void The_trend_compares_the_latest_kill_with_the_first_and_recent_kills_with_earlier_ones()
    {
        var kills = new[] { Kill(1, 200), Kill(8, 190), Kill(15, 180), Kill(22, 170), Kill(29, 160) };

        BossTrend trend = BossHistoryStore.Trend(kills.Reverse())!; // order given does not matter

        Assert.Equal(5, trend.Kills);
        Assert.Equal(200, trend.FirstSeconds);
        Assert.Equal(160, trend.LatestSeconds);
        Assert.Equal(3, trend.RecentCount);
        Assert.Equal(170, trend.RecentAverageSeconds, 3);
        Assert.Equal(195, trend.EarlierAverageSeconds, 3);
        Assert.Equal(new[] { 200.0, 190, 180, 170, 160 }, trend.Chronological.Select(k => k.Seconds));
        Assert.StartsWith("Latest kill 2:40, 0:40 faster than the first (3:20).", trend.Describe());
        Assert.Contains("The last 3 kills averaged 2:50 against 3:15 before", trend.Describe());
        Assert.Contains("raid DPS up", trend.Describe());
    }

    [Fact]
    public void Attempts_are_left_out_of_the_trend_and_a_single_kill_has_nothing_to_compare()
    {
        Assert.Null(BossHistoryStore.Trend(new[] { Kill(1, 100, killed: false) }));

        BossTrend one = BossHistoryStore.Trend(new[] { Kill(1, 100), Kill(2, 50, killed: false) })!;
        Assert.Equal(1, one.Kills);
        Assert.False(one.CanCompare);
        Assert.Equal("Latest kill 1:40, about the same as the first (1:40).", one.Describe());
    }

    // ───────────────────────── loot search ─────────────────────────

    private static LootEntry Loot(string item, string looter, string mob, int day = 1) =>
        new(new DateTime(2026, 11, day, 20, 0, 0), looter, item, mob);

    [Fact]
    public void Every_word_has_to_match_the_item_the_looter_or_the_mob()
    {
        LootEntry sword = Loot("Sword of Seru", "Warshield", "a Praetorian Pius");
        LootEntry staff = Loot("Staff of Khati Sha", "Nasti", "Lord Inquisitor Seru");

        IReadOnlyList<string> terms = LootSearch.Terms("seru warshield");
        Assert.True(LootSearch.Matches(sword, terms));
        Assert.False(LootSearch.Matches(staff, terms));
        Assert.True(LootSearch.Matches(staff, LootSearch.Terms("staff INQUISITOR")));
        Assert.True(LootSearch.Matches(staff, LootSearch.Terms(string.Empty))); // no words: everything
    }

    [Fact]
    public void A_quoted_phrase_stays_together()
    {
        Assert.Equal(new[] { "khati sha", "nasti" }, LootSearch.Terms("\"khati sha\" nasti"));
        Assert.Equal(new[] { "a", "b" }, LootSearch.Terms("  a   b "));
        Assert.Empty(LootSearch.Terms(null));
    }

    [Fact]
    public void The_time_range_limits_how_far_back_the_search_looks()
    {
        DateTime now = new(2026, 11, 30, 12, 0, 0);

        Assert.True(LootSearch.InRange(now.AddDays(-400), now, 0));
        Assert.True(LootSearch.InRange(now.AddDays(-6), now, 7));
        Assert.False(LootSearch.InRange(now.AddDays(-8), now, 7));
        Assert.Equal(LootSearch.RangeNames.Length, LootSearch.RangeDays.Length);
    }

    [Fact]
    public void A_search_that_finds_one_item_says_who_looted_it_last()
    {
        var matches = new[] { Loot("Mnemonic of Khati Sha", "Nasti", "Seru", day: 1), Loot("Mnemonic of Khati Sha", "Warshield", "Seru", day: 2) };

        string line = LootSearch.LastLootedLine(matches);

        Assert.Contains("looted 2 times", line);
        Assert.Contains("last by Warshield", line);
        Assert.Equal(string.Empty, LootSearch.LastLootedLine(new[] { matches[0], Loot("Other", "X", "Y") }));
        Assert.Equal(string.Empty, LootSearch.LastLootedLine(Array.Empty<LootEntry>()));
    }
}
