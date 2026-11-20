using EQOverwatch.Core.Firsts;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Roster;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release79Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 21, 0, 0);

    // Made-up player names (no real players in fixtures).
    private static readonly string[] Names =
    {
        "Aldren", "Brisa", "Corvan", "Dellis", "Evara", "Fendal", "Garith", "Halvi", "Istra", "Jorund", "Kessa", "Lorne", "Maelis", "Norvik",
    };

    private static EncounterSnapshot Fight(string mob, int dealers, int tanks = 0, int healers = 0, long damage = 1_000_000, bool killed = true)
    {
        var participants = Names.Take(dealers).Select(n => new ParticipantSnapshot(n, damage / Math.Max(1, dealers), 0, 100, 100, 0, 0, 0, 0, Class: "WAR")).ToList();
        var taken = Names.Skip(dealers).Take(tanks).Select(n => new DamageTakenSnapshot(n, 5000, 0, 10, 900)).ToList();
        var heals = Names.Skip(dealers + tanks).Take(healers).Select(n => new HealingSnapshot(n, 8000, 0, 0, 10, 1200, 40, "Complete Heal")).ToList();
        return new EncounterSnapshot(1, mob, T0, T0.AddMinutes(5), 300, damage, false, participants, taken, Healing: heals, Killed: killed);
    }

    [Fact]
    public void A_fight_counts_with_more_than_12_players_on_a_named_boss_far_above_the_trash_before_it()
    {
        Assert.True(ServerFirstRules.Qualifies(Fight("Lord Vyemm", 10, 2, 1), Array.Empty<long>())); // 13 players, no trash before it
        Assert.False(ServerFirstRules.Qualifies(Fight("Lord Vyemm", 10, 1, 1), Array.Empty<long>())); // 12 players: not more than 12
        Assert.False(ServerFirstRules.Qualifies(Fight("a frost giant", 14), Array.Empty<long>()));       // trash name
        Assert.False(ServerFirstRules.Qualifies(Fight("an ice giant", 14), Array.Empty<long>()));
        Assert.True(ServerFirstRules.Qualifies(Fight("Lord Vyemm", 14, damage: 500_000), new long[] { 40_000, 50_000, 60_000 }));  // 10x the median trash
        Assert.False(ServerFirstRules.Qualifies(Fight("Lord Vyemm", 14, damage: 200_000), new long[] { 40_000, 50_000, 60_000 })); // 4x: still trash-sized
        Assert.False(ServerFirstRules.Qualifies(Fight("Lord Vyemm", 14) with { IsActive = true }, Array.Empty<long>()));
    }

    [Fact]
    public void The_shared_fight_has_every_player_with_guild_and_reads_back_as_a_picture()
    {
        EncounterSnapshot fight = Fight("Lord Vyemm", 10, 2, 2);
        FirstFight shared = ServerFirstRules.Build(fight, name => name is "Aldren" or "Brisa" ? "Iron Oath" : name == "Corvan" ? "Night Watch" : string.Empty);
        Assert.Equal(14, shared.Players.Count);
        Assert.Equal(("Lord Vyemm", true, 300.0), (shared.Mob, shared.Killed, shared.Seconds));
        Assert.Equal("Iron Oath", shared.Guild);
        FirstPlayer tank = shared.Players.Single(p => p.Name == "Kessa");
        Assert.Equal((0L, 5000L), (tank.Damage, tank.Taken));
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(T0, DateTimeKind.Local)).ToUnixTimeSeconds(), shared.Start);

        EncounterSnapshot back = ServerFirstRules.ToSnapshot(shared);
        Assert.Equal(10, back.Participants.Count);
        Assert.Equal(2, back.HealingRows.Count);
        Assert.Equal(2, back.TakenRows.Count);
        Assert.Equal(T0, back.StartTime);

        string json = HttpServerFirstService.Pack(new[] { shared });
        Assert.Contains("\"n\":\"Aldren\"", json);
        Assert.Contains("\"g\":\"Iron Oath\"", json);
    }

    [Fact]
    public void Guilds_come_from_who_lines_per_server()
    {
        var book = new GuildBook();
        book.OnLine("Ream", "frostreaver", "[60 Warlord (Warrior)] Brisa (Ogre) <Iron Oath> ZONE: The Bazaar (bazaar)", DateTime.UtcNow);
        book.OnLine("Ream", "frostreaver", "[60 Oracle (Shaman)] Corvan (Barbarian)  ZONE: The Bazaar (bazaar)", DateTime.UtcNow); // no guild
        book.OnLine("Ream", "vaniki", "[ANONYMOUS] Brisa  <Other Guild>", DateTime.UtcNow);
        Assert.Equal("Iron Oath", book.Get("Brisa", "frostreaver"));
        Assert.Equal("Other Guild", book.Get("Brisa", "Vaniki"));
        Assert.Equal(string.Empty, book.Get("Corvan", "frostreaver"));
    }

    [Fact]
    public void Benchmarks_rank_a_player_against_their_class_and_parse_from_the_service()
    {
        var bench = new ClassBenchmark("Lord Vyemm", "WAR", 8, new double[] { 100, 200, 300, 400, 500, 600, 700, 800, 900 }, 0, 50);
        Assert.Equal("Top 10% WAR", bench.Badge(950));
        Assert.Equal("Top 25% WAR", bench.Badge(750));
        Assert.Equal("Top 50% WAR", bench.Badge(520));
        Assert.Equal(string.Empty, bench.Badge(150));

        ServerFirsts firsts = HttpServerFirstService.Parse(
            "{\"firsts\":[{\"mob\":\"Lord Vyemm\",\"first\":1795000000,\"verified\":true,\"witnesses\":3,\"mine\":false," +
            "\"fight\":{\"m\":\"Lord Vyemm\",\"s\":1795000000,\"l\":300,\"k\":true,\"p\":[{\"n\":\"Brisa\",\"g\":\"Iron Oath\",\"c\":\"WAR\",\"d\":90000,\"h\":0,\"t\":4000,\"x\":0}]}}," +
            "{\"mob\":\"Aary\",\"first\":0,\"verified\":false,\"witnesses\":0,\"mine\":false,\"fight\":null}]," +
            "\"benchmarks\":[{\"mob\":\"Lord Vyemm\",\"class\":\"WAR\",\"fights\":6,\"dps\":[1,2,3,4,5,6,7,8,9],\"hps\":0,\"taken\":12}],\"updated\":1795000100}");
        ServerFirstEntry vyemm = firsts.Find("lord vyemm")!;
        Assert.Equal((true, 3, "Iron Oath", false), (vyemm.Verified, vyemm.Witnesses, vyemm.Fight!.Guild, vyemm.IsOpen));
        Assert.True(firsts.Find("Aary")!.IsOpen);
        Assert.Equal(6, firsts.Benchmark("Lord Vyemm", "war")!.Fights);
    }
}
