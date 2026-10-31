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
