using EQOverwatch.Core.History;
using EQOverwatch.Core.Leveling;
using EQOverwatch.Core.Pipeline;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release80Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 20, 0, 0);

    [Fact]
    public void Stretches_carry_coin_and_ability_points_and_level_ups_are_kept()
    {
        var scanner = new LevelingScanner(new LevelingScanState(0, "Chardok", 59, 0));
        for (int i = 0; i < 6; i++)
        {
            scanner.Feed(T0.AddSeconds(i * 60), "You gain party experience! (1.000%)");
            scanner.Feed(T0.AddSeconds((i * 60) + 5), "You receive 2 platinum and 5 gold as your split.");
        }

        scanner.Feed(T0.AddSeconds(330), "You have gained an ability point!");
        scanner.Feed(T0.AddSeconds(400), "You have gained a level! Welcome to level 60!");
        LevelingStretch stretch = Assert.Single(scanner.Finish());
        Assert.Equal((12_000 + 3000L, 1), (stretch.Copper, stretch.Aa)); // 6 x 2.5 platinum
        LevelEvent up = Assert.Single(scanner.TakeLevels());
        Assert.Equal(60, up.Level);
    }

    [Fact]
    public void Aa_mode_ranks_by_ability_points_over_solo_and_group_and_plat_per_hour_is_the_median_players()
    {
        var rows = new List<LevelingRow>();
        foreach (string p in new[] { "a", "b", "c" })
        {
            rows.Add(new LevelingRow("Kael Drakkel", 56, true, p, 3600 * 2, 0, 80, Copper: 400_000, Aa: 6, AaOnly: true));
            rows.Add(new LevelingRow("Sleeper's Tomb", 56, false, p, 3600 * 2, 0, 40, Copper: 800_000, Aa: 2, AaOnly: true));
            rows.Add(new LevelingRow("Chardok", 56, false, p, 3600 * 2, 40, 60, Copper: 100_000));
        }

        IReadOnlyList<ZoneRank> aa = LevelingRanking.Rank(rows, 56, LevelingMode.Aa);
        Assert.Equal(("Kael Drakkel", 3.0, 200.0), (aa[0].Zone, aa[0].XpPerHour, aa[0].PlatPerHour));
        Assert.Equal(2, aa.Count); // solo and group together
        Assert.Equal("Chardok", Assert.Single(LevelingRanking.Rank(rows, 56, LevelingMode.Solo)).Zone); // AA-only time is left out
        Assert.Equal(56, LevelingRanking.TopBand(rows));

        SharedLeveling shared = HttpLevelingService.Parse("{\"rows\":[{\"z\":\"Chardok\",\"b\":56,\"g\":1,\"p\":2,\"s\":7200,\"x\":90.5,\"k\":80,\"c\":500000,\"a\":3}],\"zones\":[],\"players\":1}");
        Assert.Equal((500_000L, 3), (shared.Rows[0].Copper, shared.Rows[0].Aa));
    }

    [Fact]
    public void Shared_mob_stats_give_regen_levels_by_zone_resists_and_best_kills()
    {
        SharedMobStats stats = HttpMobStatsService.Parse(
            "{\"regen\":[{\"m\":\"a sarnak dragoon\",\"r\":3.1,\"hp\":9800,\"k\":40}]," +
            "\"levels\":[{\"m\":\"a sarnak dragoon\",\"z\":\"Chardok\",\"lo\":54,\"hi\":57},{\"m\":\"a sarnak dragoon\",\"z\":\"Old Sebilis\",\"lo\":45,\"hi\":47}," +
            "{\"m\":\"a chardok cleric\",\"z\":\"Chardok\",\"lo\":51,\"hi\":55}]," +
            "\"resists\":[{\"m\":\"a sarnak dragoon\",\"r\":2,\"l\":70,\"x\":30}]," +
            "\"best\":[{\"m\":\"Lord Vyemm\",\"f\":312.5,\"dps\":9840}],\"updated\":1795000000}");

        MobRegen regen = stats.Regen("A SARNAK DRAGOON")!;
        Assert.Equal((3.1, 9800L, 40), (regen.RegenPerSecond, regen.HitPoints, regen.Kills));
        Assert.Equal((54, 57), (stats.LevelRange("a sarnak dragoon", "Chardok")!.Lowest, stats.LevelRange("a sarnak dragoon", "Chardok")!.Highest));
        Assert.Equal((45, 57), (stats.LevelRange("a sarnak dragoon", "Nowhere")!.Lowest, stats.LevelRange("a sarnak dragoon", "Nowhere")!.Highest));
        Assert.Equal((51, 57), stats.ZoneRange("chardok"));
        Assert.Equal((70, 30), stats.Resists("a sarnak dragoon")[2]);
        Assert.Equal(9840, stats.Best("Lord Vyemm")!.BestDps);
        Assert.Null(stats.Regen("nobody"));

        string body = HttpMobStatsService.Pack(new MobStatsUpload(
            new[] { new SharedKill("Lord Vyemm", 312.5, 3_000_000, 40, 1_795_000_000) },
            new[] { new SharedLevel("a sarnak dragoon", "Chardok", 54, 57) },
            new[] { new SharedResist("a sarnak dragoon", 2, 70, 30) }));
        Assert.Contains("\"format\":\"EQOverwatchMobs\"", body);
        Assert.Contains("\"lo\":54", body);
    }

    [Fact]
    public void The_most_recent_logged_in_character_is_found_with_its_server()
    {
        var activity = new CharacterActivityService();
        ILogActivityObserver observer = activity;
        DateTime now = new(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);
        observer.OnLogLine("Ream", "frostreaver", now.AddSeconds(-200)); // logged out by now
        observer.OnLogLine("Stackprime", "frostreaver", now.AddSeconds(-30));
        observer.OnLogLine("Ream", "vaniki", now.AddSeconds(-5));
        Assert.Equal(("Ream", "vaniki"), activity.MostRecentActive(now));
        Assert.Null(activity.MostRecentActive(now.AddMinutes(5)));
    }
}
