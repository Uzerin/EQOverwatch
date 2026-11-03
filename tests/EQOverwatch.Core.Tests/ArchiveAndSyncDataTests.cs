using System.IO.Compression;
using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Backup;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Loot;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Sync;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public class ArchiveAndSyncDataTests
{
    private const string Boss = "Lord Inquisitor Seru";
    private static readonly DateTime Now = new(2026, 10, 23, 20, 8, 49, DateTimeKind.Utc);

    private static string TempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ───────────────────────── server on loot and money ─────────────────────────

    [Fact]
    public void New_loot_and_money_are_filed_under_the_logs_server()
    {
        var loot = new LootTracker { ServerLookup = name => name == "Reamiiv" ? "frostreaver" : string.Empty };
        loot.OnLine("Reamiiv", $"--Warshield has looted a Rusty Dagger from {Boss}'s corpse.--", Now);
        loot.OnLine("Reamiiv", "You receive 1 gold as your split.", Now);
        loot.OnLine("Reamiiv", $"{Boss}'s corpse looks positively amazed at this turn of events.", Now);

        Assert.Equal("frostreaver", Assert.Single(loot.Entries).Server);
        Assert.Equal("frostreaver", Assert.Single(loot.Money).Server);
    }

    [Fact]
    public void A_scanned_log_takes_the_server_from_its_file_name()
    {
        string dir = TempDir("eqo-server-");
        try
        {
            string log = Path.Combine(dir, "eqlog_Reamiiv_oakwynd.txt");
            File.WriteAllLines(log, new[] { $"[Mon Oct 26 19:48:52 2026] --Reamiiv has looted a Rusty Dagger from {Boss}'s corpse.--" });
            var loot = new LootTracker();
            loot.ScanLog(log);

            Assert.Equal("oakwynd", Assert.Single(loot.Entries).Server);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ───────────────────────── merging what another PC sent ─────────────────────────

    [Fact]
    public void Merged_loot_and_money_skip_what_is_already_recorded()
    {
        var loot = new LootTracker();
        loot.OnLine("Reamiiv", $"--Warshield has looted a Rusty Dagger from {Boss}'s corpse.--", Now);
        DateTime local = loot.Entries[0].Time;

        int added = loot.Merge(
            new[]
            {
                new LootEntry(local.AddSeconds(1), "Warshield", "Rusty Dagger", Boss), // the same drop seen from another PC
                new LootEntry(local.AddMinutes(5), "Nasti", "Silk Robe", Boss),
            },
            new[] { new MoneyEntry(local, "Ream", 596, Boss) });

        Assert.Equal(2, added);
        Assert.Equal(new[] { "Rusty Dagger", "Silk Robe" }, loot.Entries.Select(e => e.Item));
        Assert.Single(loot.Money);
        Assert.Equal(0, loot.Merge(loot.Entries, loot.Money)); // sending it all again adds nothing
    }

    [Fact]
    public void Merged_raid_records_must_have_a_real_dump_name_and_are_added_once()
    {
        var store = new RaidRosterStore();
        var members = new List<RaidMember> { new(1, "Nasti", 125, "Cleric", "") };
        var good = new RaidAttendance { Server = "frostreaver", Taken = new DateTime(2026, 10, 29, 18, 1, 39), FileName = "RaidRoster_frostreaver-20261029-180139", Members = members };
        var bad = new RaidAttendance { Server = "x", Taken = DateTime.Now, FileName = @"..\..\evil", Members = members };

        Assert.Equal(1, store.Merge(new[] { good, bad }));
        Assert.Equal(0, store.Merge(new[] { good }));
        Assert.Single(store.Records());
    }

    [Fact]
    public async Task A_sender_passes_loot_money_and_raids_to_the_host()
    {
        string dir = TempDir("eqo-bundle-");
        const string code = "ABCDE-FGHJK";
        using var host = new CharacterSyncHost(dir, () => code, tcpPort: 0, discoveryPort: 0);
        try
        {
            var got = new TaskCompletionSource<SyncBundle>();
            host.BundleReceived += (_, bundle) => got.TrySetResult(bundle);
            host.Start();

            var bundle = new SyncBundle(
                "PC2", new List<SyncFile>(),
                new List<LootEntry> { new(Now, "Nasti", "Silk Robe", Boss, Server: "frostreaver") },
                new List<MoneyEntry> { new(Now, "Ream", 596, Boss, "frostreaver") },
                new List<RaidAttendance> { new() { Server = "frostreaver", FileName = "RaidRoster_frostreaver-20261029-180139", Members = new List<RaidMember> { new(1, "Nasti", 125, "Cleric", "") } } });

            Assert.Equal(SyncSendResult.Sent, await CharacterSyncSender.SendAsync("127.0.0.1", code, bundle, CancellationToken.None, host.Port));

            SyncBundle received = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Silk Robe", Assert.Single(received.Loot!).Item);
            Assert.Equal(596, Assert.Single(received.Money!).Copper);
            Assert.Equal("Nasti", Assert.Single(Assert.Single(received.Attendance!).Members).Name);
        }
        finally
        {
            host.Stop();
            Directory.Delete(dir, recursive: true);
        }
    }

    // ───────────────────────── boss history ─────────────────────────

    [Fact]
    public void A_fight_that_ends_with_the_boss_dying_is_marked_killed()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Nasti", Boss, 100, "11:45:00"));
        tracker.OnCombatEvent(Hit("Nasti", Boss, 100, "11:45:06"));
        tracker.OnCombatEvent(Death(Boss, "Nasti", "11:45:08"));

        Assert.True(Assert.Single(tracker.Snapshot.Completed).Killed);
    }

    [Fact]
    public void A_real_slain_line_with_a_capital_first_letter_marks_the_kill()
    {
        const string logName = "Ream";

        Assert.True(EQOverwatch.Core.Parsing.CombatLineParser.TryParse(
            "[Wed Oct 21 19:46:21 2026] A Praetorian Pius was slain by Pecos!", At("19:46:21"), logName, out CombatEvent slain));
        Assert.Equal(CombatEventKind.Death, slain.Kind);
        Assert.Equal("A Praetorian Pius", slain.Target);
        Assert.Equal("Pecos", slain.Source);

        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Pecos", "a Praetorian Pius", 100, "19:46:10"));
        tracker.OnCombatEvent(Hit("Pecos", "a Praetorian Pius", 100, "19:46:18"));
        tracker.OnCombatEvent(slain);

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Completed);
        Assert.Equal("a Praetorian Pius", fight.TargetName);
        Assert.True(fight.Killed);
    }

    [Fact]
    public void A_capitalised_spelling_of_the_mob_is_the_same_fight_and_is_marked_killed()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Pecos", "a Praetorian Pius", 100, "19:46:10"));
        tracker.OnCombatEvent(Hit("Pecos", "A Praetorian Pius", 100, "19:46:14")); // e.g. a damage-over-time line starting the sentence
        tracker.OnCombatEvent(Death("A Praetorian Pius", "Pecos", "19:46:18"));

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Completed);
        Assert.Empty(tracker.Snapshot.Active);
        Assert.True(fight.Killed);
        Assert.Equal(200, fight.TotalDamage);
    }

    [Fact]
    public void A_kill_replaces_older_pieces_of_the_same_fight_whatever_the_capitals()
    {
        var store = new BossHistoryStore();
        DateTime start = new(2026, 10, 23, 19, 46, 0);
        // As an older build saved one fight: an "a" piece and an "A" piece, neither a kill.
        store.Record(new[]
        {
            Snap("a Praetorian Pius", start, 20, killed: false),
            Snap("A Praetorian Pius", start.AddSeconds(4), 14, killed: false),
        });
        Assert.Single(BossHistoryStore.Summarize(store.Fights(), 1));

        store.Record(new[] { Snap("a Praetorian Pius", start, 20, killed: true) });

        BossSummary summary = Assert.Single(BossHistoryStore.Summarize(store.Fights(), 1));
        Assert.Equal(1, summary.Kills);
        Assert.Equal(0, summary.Attempts);

        // A shorter read that sees no death must not undo the kill.
        store.Record(new[] { Snap("a Praetorian Pius", start, 12, killed: false) });
        Assert.Equal(1, Assert.Single(BossHistoryStore.Summarize(store.Fights(), 1)).Kills);
    }

    private static EncounterSnapshot Snap(string boss, DateTime start, int seconds, bool killed)
    {
        var tracker = new LiveDpsTracker();
        tracker.BeginBatch();
        for (int t = 0; t < seconds; t += 5) // a hit at least every 5 s, so the fight does not time out
        {
            tracker.OnCombatEvent(new CombatEvent(start.AddSeconds(t), CombatEventKind.Damage, "Pecos", boss, 100, DamageKind.Melee, "hit"));
        }

        tracker.OnCombatEvent(new CombatEvent(start.AddSeconds(seconds), CombatEventKind.Damage, "Pecos", boss, 100, DamageKind.Melee, "hit"));
        if (killed)
        {
            tracker.OnCombatEvent(new CombatEvent(start.AddSeconds(seconds), CombatEventKind.Death, "Pecos", boss, 0, DamageKind.None, null));
        }

        tracker.EndBatch();
        return tracker.Snapshot.Completed[0];
    }

    [Fact]
    public void The_kill_report_lists_kills_fastest_first_and_sets_attempts_aside()
    {
        var fights = new[]
        {
            new BossFight(Boss, new DateTime(2026, 10, 9, 20, 22, 0), 330, 3_980_000, 21, 3, true),
            new BossFight(Boss, new DateTime(2026, 10, 16, 20, 5, 0), 192, 3_410_000, 24, 0, true),
            new BossFight("a Praetorian Pius", new DateTime(2026, 10, 16, 20, 40, 0), 100, 2_000_000, 24, 0, true), // another boss
            new BossFight("LORD INQUISITOR SERU", new DateTime(2026, 10, 9, 21, 2, 0), 72, 500_000, 20, 4, false),
            new BossFight(Boss, new DateTime(2026, 10, 2, 20, 14, 0), 245, 3_620_000, 4, 2, true), // a small fight the filter hides
        };

        BossKillReport report = BossHistoryStore.KillReport(fights, Boss, minPlayers: 6);

        Assert.Equal(new[] { 16, 9 }, report.Kills.Select(k => k.Start.Day)); // fastest first (3:12, then 5:30)
        Assert.Single(report.Attempts);
        Assert.Equal(192, report.FastestSeconds);
        Assert.Equal(261, report.AverageSeconds);
        Assert.Equal(3_410_000 / 192.0, report.BestDps, 3);

        BossKillReport none = BossHistoryStore.KillReport(fights, "nobody", 1);
        Assert.Empty(none.Kills);
        Assert.Equal(0, none.BestDps);
    }

    [Fact]
    public void Kills_are_sorted_from_the_fastest_clear_time_to_the_slowest_whatever_the_date()
    {
        var fights = new[]
        {
            new BossFight(Boss, new DateTime(2026, 10, 2, 20, 0, 0), 400, 4_000_000, 24, 0, true), // oldest, slowest
            new BossFight(Boss, new DateTime(2026, 10, 9, 20, 0, 0), 200, 4_000_000, 24, 0, true), // fastest
            new BossFight(Boss, new DateTime(2026, 10, 16, 20, 0, 0), 300, 4_000_000, 24, 0, true), // newest
        };

        BossKillReport report = BossHistoryStore.KillReport(fights, Boss, 1);

        Assert.Equal(new[] { 200.0, 300.0, 400.0 }, report.Kills.Select(k => k.Seconds));
    }

    [Fact]
    public void Fights_are_tagged_with_the_log_they_came_from_and_two_characters_logs_stay_apart()
    {
        var store = new BossHistoryStore();
        DateTime start = new(2026, 10, 23, 20, 0, 0);

        Assert.Equal(1, store.Record(new[] { Snap("a Praetorian Pius", start, 20, killed: true) }, "Pecos", "frostreaver"));
        Assert.Equal(1, store.Record(new[] { Snap("a Praetorian Pius", start, 20, killed: true) }, "Oodles", "frostreaver"));
        Assert.Equal(2, store.Fights().Count); // the same fight seen from two logs

        Assert.Equal(0, store.Record(new[] { Snap("a Praetorian Pius", start, 20, killed: true) }, "Pecos", "frostreaver")); // read again: no change
        Assert.Equal(2, store.Fights().Count);

        Assert.Single(BossHistoryStore.ForLog(store.Fights(), "Pecos", "frostreaver"));
        Assert.Single(BossHistoryStore.ForLog(store.Fights(), "oodles", "FROSTREAVER")); // capitals do not matter
        Assert.Empty(BossHistoryStore.ForLog(store.Fights(), "Nobody", "frostreaver"));
        Assert.Equal(2, BossHistoryStore.ForLog(store.Fights(), null, null).Count);
    }

    [Fact]
    public void A_fight_saved_by_an_older_build_counts_for_every_log_until_that_log_is_read_again()
    {
        var store = new BossHistoryStore();
        DateTime start = new(2026, 10, 23, 20, 0, 0);
        store.Record(new[] { Snap("a Praetorian Pius", start, 20, killed: true) }); // no character: how older builds saved it

        Assert.Single(BossHistoryStore.ForLog(store.Fights(), "Pecos", "frostreaver"));
        Assert.Single(BossHistoryStore.ForLog(store.Fights(), "Oodles", "frostreaver"));

        store.Record(new[] { Snap("a Praetorian Pius", start, 20, killed: true) }, "Pecos", "frostreaver");

        BossFight only = Assert.Single(store.Fights()); // Pecos's log replaced the untagged copy
        Assert.Equal("Pecos", only.Character);
        Assert.Empty(BossHistoryStore.ForLog(store.Fights(), "Oodles", "frostreaver"));
    }

    [Fact]
    public void The_character_and_server_tag_survives_saving_and_loading()
    {
        string path = Path.Combine(Path.GetTempPath(), "eqo-boss-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = new BossHistoryStore();
            first.SetStoragePath(path);
            first.Record(new[] { Snap("a Praetorian Pius", new DateTime(2026, 10, 23, 20, 0, 0), 20, killed: true) }, "Pecos", "frostreaver");

            var second = new BossHistoryStore();
            second.SetStoragePath(path);
            BossFight fight = Assert.Single(second.Fights());
            Assert.Equal("Pecos", fight.Character);
            Assert.Equal("frostreaver", fight.Server);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_mob_list_covers_every_log_but_the_figures_come_only_from_the_loaded_one()
    {
        var all = new[]
        {
            new BossFight("a Praetorian Pius", new DateTime(2026, 10, 23, 20, 0, 0), 200, 4_000_000, 24, 0, true, "Pecos", "frostreaver"),
            new BossFight("Lord Inquisitor Seru", new DateTime(2026, 10, 24, 20, 0, 0), 300, 4_000_000, 24, 0, true, "Oodles", "frostreaver"),
            new BossFight("a small thing", new DateTime(2026, 10, 24, 21, 0, 0), 30, 1_000, 2, 0, true, "Oodles", "frostreaver"), // below the size filter
        };

        IReadOnlyList<BossFight> loaded = BossHistoryStore.ForLog(all, "Pecos", "frostreaver");
        IReadOnlyList<BossSummary> rows = BossHistoryStore.SummarizeMobs(all, loaded, 6);

        Assert.Equal(new[] { "a Praetorian Pius", "Lord Inquisitor Seru" }, rows.Select(r => r.Boss));
        Assert.Equal(1, rows[0].Kills);
        Assert.Equal(0, rows[1].Kills); // fought only in another character's log
        Assert.Equal(0, rows[1].Attempts);
    }

    [Fact]
    public void Con_lines_with_a_level_give_the_mob_and_its_level()
    {
        Assert.True(EQOverwatch.Core.Parsing.ConLineParser.TryParse(
            "A Praetorian Pius scowls at you, ready to attack -- looks like he would wipe the floor with you! (Lvl: 62)", out string mob, out int level));
        Assert.Equal("a Praetorian Pius", mob);
        Assert.Equal(62, level);

        Assert.True(EQOverwatch.Core.Parsing.ConLineParser.TryParse(
            "Lord Inquisitor Seru regards you indifferently -- what would you like your tombstone to say? (Lvl: 65)", out mob, out level));
        Assert.Equal("Lord Inquisitor Seru", mob);
        Assert.Equal(65, level);

        Assert.False(EQOverwatch.Core.Parsing.ConLineParser.TryParse("You slash a frost giant for 100 points of damage.", out _, out _));
        Assert.False(EQOverwatch.Core.Parsing.ConLineParser.TryParse("Pecos tells you, 'see you at 8 (Lvl: 62)'", out _, out _));
    }

    [Fact]
    public void Mob_levels_keep_the_lowest_and_highest_seen_ignoring_capitals_and_survive_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), "eqo-levels-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new MobLevelStore();
            store.SetStoragePath(path);
            Assert.True(store.Observe("a Praetorian Pius", 62));
            Assert.True(store.Observe("A PRAETORIAN PIUS", 60));
            Assert.True(store.Observe("a praetorian pius", 64));
            Assert.False(store.Observe("a Praetorian Pius", 63)); // inside the range already

            MobLevels range = store.Find("a Praetorian Pius")!;
            Assert.Equal(60, range.Lowest);
            Assert.Equal(64, range.Highest);
            Assert.Null(store.Find("nobody"));

            var again = new MobLevelStore();
            again.SetStoragePath(path);
            MobLevels loaded = again.Find("A PRAETORIAN PIUS")!;
            Assert.Equal(60, loaded.Lowest);
            Assert.Equal(64, loaded.Highest);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_tracker_reports_the_level_on_a_con_line_it_reads()
    {
        var tracker = new LiveDpsTracker();
        var seen = new List<(string Mob, int Level)>();
        tracker.MobLevelSeen = (mob, level) => seen.Add((mob, level));

        tracker.OnNonCombatMessage("A Praetorian Pius scowls at you, ready to attack -- looks like he would wipe the floor with you! (Lvl: 62)");
        tracker.OnNonCombatMessage("You say, 'hello (Lvl: 3)'");

        Assert.Equal(("a Praetorian Pius", 62), Assert.Single(seen));
    }

    [Fact]
    public void Best_dps_falls_back_to_the_best_attempt_when_there_is_no_kill()
    {
        var fights = new[] { new BossFight(Boss, new DateTime(2026, 10, 22, 19, 0, 0), 100, 250_000, 30, 1, false) };
        Assert.Equal(2500, Assert.Single(BossHistoryStore.Summarize(fights)).BestDps);
    }

    [Fact]
    public void Loot_saved_without_a_server_learns_it_when_the_log_is_read_again()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string line = "[Wed Oct 21 19:50:00 2026] --Ream has looted a Shiny Thing from a Praetorian Pius's corpse.--\n";
            string bare = Path.Combine(dir, "plain.txt");
            File.WriteAllText(bare, line);
            string named = Path.Combine(dir, "eqlog_Ream_frostreaver.txt");
            File.WriteAllText(named, line);

            var tracker = new LootTracker();
            tracker.ScanLog(bare);
            Assert.Equal(string.Empty, Assert.Single(tracker.Entries).Server);

            Assert.Equal(0, tracker.ScanLog(named)); // nothing new...
            Assert.Equal("frostreaver", Assert.Single(tracker.Entries).Server, ignoreCase: true); // ...but the server is filled in
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_fight_that_fades_out_is_not_a_kill()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Nasti", Boss, 100, "11:45:00"));
        tracker.OnCombatEvent(Hit("Nasti", Boss, 100, "11:45:06"));
        tracker.OnCombatEvent(Hit("Nasti", "a frost giant", 100, "11:46:30")); // much later: the first fight timed out

        Assert.False(tracker.Snapshot.Completed.Single(e => e.TargetName == Boss).Killed);
    }

    [Fact]
    public void The_summary_counts_kills_and_attempts_and_times_the_kills()
    {
        var fights = new[]
        {
            new BossFight(Boss, new DateTime(2026, 10, 20, 20, 0, 0), 240, 480_000, 30, 2, true),
            new BossFight(Boss, new DateTime(2026, 10, 22, 20, 0, 0), 180, 540_000, 32, 0, true),
            new BossFight(Boss, new DateTime(2026, 10, 22, 19, 0, 0), 90, 100_000, 30, 5, false),
            new BossFight("a frost giant", new DateTime(2026, 10, 22, 19, 30, 0), 20, 5_000, 2, 0, true),
        };

        BossSummary summary = Assert.Single(BossHistoryStore.Summarize(fights, minPlayers: 6));

        Assert.Equal((Boss, 2, 1), (summary.Boss, summary.Kills, summary.Attempts));
        Assert.Equal(180, summary.FastestSeconds);
        Assert.Equal(210, summary.AverageSeconds);
        Assert.Equal(3000, summary.BestDps);
        Assert.Equal(2, BossHistoryStore.Summarize(fights, minPlayers: 1).Count);
        Assert.Equal("3:00", BossHistoryStore.FormatDuration(180));
        Assert.Equal("—", BossHistoryStore.FormatDuration(0));
    }

    [Fact]
    public void Recorded_fights_are_saved_and_not_added_twice()
    {
        string dir = TempDir("eqo-history-");
        try
        {
            string file = Path.Combine(dir, "bosshistory.json");
            var tracker = new LiveDpsTracker();
            tracker.OnCombatEvent(Hit("Nasti", Boss, 100, "11:45:00"));
            tracker.OnCombatEvent(Hit("Nasti", Boss, 100, "11:45:08"));
            tracker.OnCombatEvent(Death(Boss, "Nasti", "11:45:10"));

            var first = new BossHistoryStore();
            first.SetStoragePath(file);
            Assert.Equal(1, first.Record(tracker.Snapshot.Completed));
            Assert.Equal(0, first.Record(tracker.Snapshot.Completed));

            var second = new BossHistoryStore();
            second.SetStoragePath(file);
            BossFight saved = Assert.Single(second.Fights());
            Assert.True(saved.Killed);
            Assert.Equal(Boss, saved.Boss);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ───────────────────────── backup and restore ─────────────────────────

    [Fact]
    public void A_backup_holds_the_data_but_not_the_pairing_code_or_other_pcs_files()
    {
        string data = TempDir("eqo-data-");
        string zipDir = TempDir("eqo-zip-");
        try
        {
            File.WriteAllText(Path.Combine(data, "settings.json"), "{\"HistoricalDaysLimit\":7,\"SyncCode\":\"ABCDE-FGHJK\"}");
            File.WriteAllText(Path.Combine(data, "loot.json"), "[]");
            Directory.CreateDirectory(Path.Combine(data, "characters"));
            File.WriteAllText(Path.Combine(data, "characters", "Nasti_frostreaver.json"), "{}");
            Directory.CreateDirectory(Path.Combine(data, "remote", "PC2"));
            File.WriteAllText(Path.Combine(data, "remote", "PC2", "x.txt"), "other pc");
            File.WriteAllText(Path.Combine(data, "half.tmp"), "temp");

            string zipPath = Path.Combine(zipDir, "backup.zip");
            int files = BackupService.Export(data, zipPath, "v1.0.27");

            Assert.Equal(3, files);
            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            Assert.Equal(
                new[] { "backup-info.txt", "characters/Nasti_frostreaver.json", "loot.json", "settings.json" },
                zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
            using var reader = new StreamReader(zip.GetEntry("settings.json")!.Open());
            string settings = reader.ReadToEnd();
            Assert.DoesNotContain("ABCDE", settings);
            Assert.Contains("HistoricalDaysLimit", settings);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
            Directory.Delete(zipDir, recursive: true);
        }
    }

    [Fact]
    public void A_restore_is_staged_then_applied_and_keeps_this_pcs_pairing_code()
    {
        string source = TempDir("eqo-src-");
        string target = TempDir("eqo-dst-");
        string zipDir = TempDir("eqo-zip-");
        try
        {
            File.WriteAllText(Path.Combine(source, "settings.json"), "{\"HistoricalDaysLimit\":30,\"SyncCode\":\"\"}");
            File.WriteAllText(Path.Combine(source, "loot.json"), "[1]");
            string zipPath = Path.Combine(zipDir, "backup.zip");
            BackupService.Export(source, zipPath, "v1.0.27");

            File.WriteAllText(Path.Combine(target, "settings.json"), "{\"HistoricalDaysLimit\":7,\"SyncCode\":\"MYCODE-12345\"}");
            File.WriteAllText(Path.Combine(target, "loot.json"), "[]");

            Assert.Equal(2, BackupService.StageRestore(zipPath, target));
            Assert.True(BackupService.HasPendingRestore(target));
            Assert.Contains("\"HistoricalDaysLimit\":7", File.ReadAllText(Path.Combine(target, "settings.json"))); // nothing changes until the next start

            Assert.Equal(2, BackupService.ApplyPendingRestore(target));

            Assert.False(BackupService.HasPendingRestore(target));
            string settings = File.ReadAllText(Path.Combine(target, "settings.json"));
            Assert.Contains("\"HistoricalDaysLimit\":30", settings);
            Assert.Contains("MYCODE-12345", settings);
            Assert.Equal("[1]", File.ReadAllText(Path.Combine(target, "loot.json")));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(target, recursive: true);
            Directory.Delete(zipDir, recursive: true);
        }
    }

    [Fact]
    public void A_zip_that_is_not_a_backup_or_points_outside_the_folder_is_not_restored()
    {
        string dir = TempDir("eqo-bad-");
        try
        {
            string notBackup = Path.Combine(dir, "other.zip");
            using (ZipArchive zip = ZipFile.Open(notBackup, ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(zip.CreateEntry("readme.txt").Open());
                w.Write("hello");
            }

            Assert.Throws<InvalidDataException>(() => BackupService.StageRestore(notBackup, dir));

            string sneaky = Path.Combine(dir, "sneaky.zip");
            using (ZipArchive zip = ZipFile.Open(sneaky, ZipArchiveMode.Create))
            {
                using (var a = new StreamWriter(zip.CreateEntry("settings.json").Open()))
                {
                    a.Write("{}");
                }

                using var b = new StreamWriter(zip.CreateEntry("../escaped.txt").Open());
                b.Write("no");
            }

            BackupService.StageRestore(sneaky, dir);
            Assert.False(File.Exists(Path.Combine(Directory.GetParent(dir)!.FullName, "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "escaped.txt")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
