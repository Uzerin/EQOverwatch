using System.Globalization;
using EQOverwatch.Core.Leveling;
using EQOverwatch.Core.Maps;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release78Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 20, 0, 0);

    private static LevelingScanner Scanner(int level = 51, string zone = "The Ruins of Sebilis", int legacy = 0) =>
        new(new LevelingScanState(0, zone, level, legacy));

    [Fact]
    public void Leveling_time_and_experience_come_from_gains_with_power_leveling_bursts_left_out()
    {
        LevelingScanner scanner = Scanner();
        for (int i = 0; i < 20; i++)
        {
            scanner.Feed(T0.AddSeconds(i * 60), "You gain party experience! (1.000%)");
        }

        // a power-leveling burst: 9 gains within 30 seconds (its 5 minutes either side reach back to 1200 s, after the last kill above)
        for (int i = 0; i < 9; i++)
        {
            scanner.Feed(T0.AddSeconds(1500 + i), "You gain party experience! (0.500%)");
        }

        scanner.Feed(T0.AddSeconds(1600), "You gained raid experience (with a bonus)! (3.000%)"); // raid: left out
        LevelingStretch stretch = Assert.Single(scanner.Finish());
        Assert.Equal(("The Ruins of Sebilis", 51, true), (stretch.Zone, stretch.Level, stretch.Group));
        Assert.Equal(20, stretch.Kills);
        Assert.Equal(20.0, stretch.Xp, 3);
        Assert.Equal(1140, stretch.Seconds); // 19 gaps of a minute; the burst's time is not counted
    }

    [Fact]
    public void A_stretch_that_is_mostly_power_leveling_is_dropped()
    {
        LevelingScanner scanner = Scanner();
        scanner.Feed(T0, "You gain experience! (1.000%)");
        scanner.Feed(T0.AddSeconds(120), "You gain experience! (1.000%)");
        scanner.Feed(T0.AddSeconds(240), "You gain experience! (1.000%)");
        for (int i = 0; i < 9; i++)
        {
            scanner.Feed(T0.AddSeconds(300 + i), "You gain experience! (2.000%)");
        }

        Assert.Empty(scanner.Finish()); // 9 gains in 30 seconds, and the 3 before it are within 5 minutes
        Assert.All(LevelingScanner.Bursts(Enumerable.Range(0, 8).Select(i => T0.AddSeconds(i)).ToList()), Assert.False); // 8 in 30 s is a real group
        Assert.All(LevelingScanner.Bursts(Enumerable.Range(0, 9).Select(i => T0.AddSeconds(i * 3)).ToList()), Assert.True);
        Assert.All(LevelingScanner.Bursts(Enumerable.Range(0, 13).Select(i => T0.AddSeconds(i * 4.5)).ToList()), Assert.True); // 13 in a minute
    }

    [Fact]
    public void Zone_level_and_legacy_bonus_are_followed_and_split_solo_from_group()
    {
        var scanner = new LevelingScanner();
        scanner.Feed(T0, "You gain experience! (1.000%)"); // level not known yet: left out
        scanner.Feed(T0.AddSeconds(1), "You have entered The Dreadlands.");
        scanner.Feed(T0.AddSeconds(2), "You have gained a level! Welcome to level 52!");
        scanner.Feed(T0.AddSeconds(3), "Your legacy server experience bonus for reaching the maximum level has increased to 100%!");
        for (int i = 1; i <= 4; i++)
        {
            scanner.Feed(T0.AddSeconds(i * 60), "You gain experience! (2.000%)");
            scanner.Feed(T0.AddSeconds((i * 60) + 30), "You gain party experience! (1.000%)");
        }

        scanner.Feed(T0.AddSeconds(400), "You have entered Firiona Vie.");
        IReadOnlyList<LevelingStretch> found = scanner.Finish();
        LevelingStretch solo = found.Single(s => !s.Group);
        LevelingStretch group = found.Single(s => s.Group);
        Assert.Equal(("The Dreadlands", 52, 4.0, 180.0), (solo.Zone, solo.Level, Math.Round(solo.Xp, 3), solo.Seconds)); // 2% halved by the bonus
        Assert.Equal(2.0, group.Xp, 3);
        Assert.Equal("Firiona Vie", scanner.Zone);
        Assert.Equal(100, scanner.LegacyBonus);
    }

    [Fact]
    public void A_log_is_read_a_little_at_a_time_from_where_it_stopped()
    {
        string path = Path.Combine(Path.GetTempPath(), "eqo-lvl-" + Guid.NewGuid().ToString("N") + ".txt");
        string Line(int second, string message) => "[" + T0.AddSeconds(second).ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "] " + message + "\r\n";
        try
        {
            File.WriteAllText(path, Line(0, "You have entered Chardok.") + Line(1, "You have gained a level! Welcome to level 57!")
                + string.Concat(Enumerable.Range(1, 5).Select(i => Line(i * 60, "You gain party experience! (1.500%)"))) + "[Fri Nov 20 20:10:00 2026] You gain");
            (IReadOnlyList<LevelingStretch> first, LevelingScanState state) = LevelingScanner.ScanLog(path, null);
            Assert.Equal(("Chardok", 57, 5), (Assert.Single(first).Zone, first[0].Level, first[0].Kills));
            Assert.Equal(("Chardok", 57), (state.Zone, state.Level));

            File.AppendAllText(path, " party experience! (1.500%)\r\n" + string.Concat(Enumerable.Range(11, 4).Select(i => Line(i * 60, "You gain party experience! (1.500%)"))));
            (IReadOnlyList<LevelingStretch> next, LevelingScanState after) = LevelingScanner.ScanLog(path, state);
            Assert.Equal(5, Assert.Single(next).Kills); // the half-written line, then four more
            Assert.Equal(new FileInfo(path).Length, after.Offset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Zones_rank_by_the_median_player_once_there_is_enough_data()
    {
        var rows = new List<LevelingRow>();
        void Add(string zone, int band, string player, double hours, double xp) => rows.Add(new LevelingRow(zone, band, true, player, hours * 3600, xp, (int)(hours * 40)));
        Add("Chardok", 56, "a", 2, 100); Add("Chardok", 56, "b", 2, 80); Add("Chardok", 56, "c", 2, 120);          // 50, 40, 60 %/h
        Add("Kael Drakkel", 56, "a", 3, 240); Add("Kael Drakkel", 56, "b", 3, 300);                                // 2 players: not enough
        Add("Skyshrine", 56, "a", 1, 30); Add("Skyshrine", 56, "b", 1, 30); Add("Skyshrine", 56, "c", 1, 30);     // 3 h: not enough
        Add("Sebilis", 51, "a", 3, 200); Add("Sebilis", 51, "b", 3, 200); Add("Sebilis", 51, "c", 3, 200);

        IReadOnlyList<ZoneRank> ranked = LevelingRanking.Rank(rows, 56, true);
        Assert.Equal(new[] { "Chardok", "Kael Drakkel", "Skyshrine" }, ranked.Select(r => r.Zone));
        Assert.Equal((50.0, 3, 6.0, true), (ranked[0].XpPerHour, ranked[0].Players, ranked[0].Hours, ranked[0].Enough));
        Assert.False(ranked[1].Enough);
        Assert.Empty(LevelingRanking.Rank(rows, 56, false)); // solo: none

        var path = LevelingRanking.Path(rows, 53, true);
        Assert.Equal(new[] { (51, "Sebilis"), (56, "Chardok") }, path.Select(p => (p.Band, p.Best!.Zone)));
        Assert.Equal((51, 56, "51–55"), (LevelingRanking.BandOf(55), LevelingRanking.BandOf(56), LevelingRanking.BandText(51)));
        Assert.Equal("Kael Drakkel", LevelingRanking.Rank(rows, 56, true, z => z != "Chardok").First().Zone);
    }

    [Fact]
    public void A_servers_expansions_are_every_one_up_to_the_newest_zone_seen()
    {
        var zones = new ZoneDirectory(new[]
        {
            new ZoneInfo(1, "Qeynos Hills", "qeytoqrg", 1), new ZoneInfo(2, "Chardok", "chardok", 2),
            new ZoneInfo(3, "Kael Drakkel", "kael", 3), new ZoneInfo(4, "Velketor's Labyrinth", "velketor", 3),
            new ZoneInfo(5, "Velketor's Labyrinth", "velketor_tov", 27),
        });

        Assert.Equal(new[] { 1, 2 }, LevelingRanking.InferExpansions(zones, new[] { "Qeynos Hills", "Chardok", "Nowhere" }).OrderBy(e => e));
        Assert.Equal(new[] { 1, 2, 3 }, LevelingRanking.InferExpansions(zones, new[] { "Chardok", "Velketor's Labyrinth" }).OrderBy(e => e)); // shared name: its oldest
        Assert.Equal(3, LevelingRanking.ExpansionOf(zones, "Velketor's Labyrinth"));
        Assert.Null(LevelingRanking.ExpansionOf(zones, "Nowhere"));
    }

    [Fact]
    public void Sharing_is_turned_on_once_for_older_settings_and_stays_off_when_switched_off_later()
    {
        string path = Path.Combine(Path.GetTempPath(), "eqo-share-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"UseSharedDrops\":false,\"ShareMyDrops\":false,\"CountMeIn\":false}"); // before 1.0.78
            var service = new EQOverwatch.Core.Settings.AppSettingsService(path);
            EQOverwatch.Core.Settings.AppSettings on = service.Load();
            Assert.True(on.UseSharedDrops && on.ShareMyDrops && on.UseSharedLeveling && on.ShareMyLeveling && on.CountMeIn);

            on.ShareMyLeveling = false; // switched off after the update
            service.Save(on);
            Assert.False(new EQOverwatch.Core.Settings.AppSettingsService(path).Load().ShareMyLeveling);
            Assert.True(new EQOverwatch.Core.Settings.AppSettings().ShareMyDrops); // a new install
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Shared_leveling_is_packed_without_names_and_read_back()
    {
        byte[] gz = HttpLevelingService.Pack(new[] { new LevelingStretch("Chardok", 57, true, 600, 3.5, 12, 1_790_000_000) });
        using var input = new System.IO.Compression.GZipStream(new MemoryStream(gz), System.IO.Compression.CompressionMode.Decompress);
        string json = new StreamReader(input).ReadToEnd();
        Assert.Contains("\"z\":\"Chardok\"", json);
        Assert.Contains("EQOverwatchLeveling", json);

        SharedLeveling shared = HttpLevelingService.Parse("{\"rows\":[{\"z\":\"Chardok\",\"b\":56,\"g\":1,\"p\":2,\"s\":7200,\"x\":90.5,\"k\":80}],\"zones\":[\"Chardok\"],\"players\":1}");
        Assert.Equal(new LevelingRow("Chardok", 56, true, "p2", 7200, 90.5, 80), Assert.Single(shared.Rows));
        Assert.Equal((1, "Chardok"), (shared.Players, Assert.Single(shared.Zones)));
    }
}
