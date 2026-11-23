using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.ResourceHunter;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class ResourceHunterTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc); // 12:00 Pacific
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqo-rh-" + Guid.NewGuid().ToString("N"));

    public ResourceHunterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("improving the experiences of all who venture here.", ResourceBonus.Experience)]
    [InlineData("improving the experience tied to abilities for all who venture here.", ResourceBonus.Aa)]
    [InlineData("multiplying the loot that may be found here.", ResourceBonus.Loot)]
    [InlineData("multiplying the coin that may be found here.", ResourceBonus.Coin)]
    [InlineData("which has drawn many creatures of exceptional rarity here.", ResourceBonus.RareSpawn)]
    [InlineData("which has drawn numerous mundane creatures here.", ResourceBonus.Respawn)]
    [InlineData("improving the rate at which proficiency is gained while adventuring here.", ResourceBonus.Skill)]
    [InlineData("magnifying the recognition one may receive from their adventures here.", ResourceBonus.Faction)]
    public void The_bonus_lines_from_real_logs_are_read(string rest, ResourceBonus bonus) =>
        Assert.Equal(bonus, ResourceHunterText.Parse("This land is saturated with unstable magic, " + rest));

    [Fact]
    public void Other_lines_zone_names_and_the_pacific_day()
    {
        Assert.Null(ResourceHunterText.Parse("Fonts of unstable magic are erupting and receding in response to some nameless power."));
        Assert.Null(ResourceHunterText.Parse("Raytlen tells General:1, 'This land is saturated with unstable magic, multiplying the loot'"));
        Assert.Equal(ResourceHunterText.ZoneKey("Akheva Ruins"), ResourceHunterText.ZoneKey("The Akheva Ruins"));
        Assert.Equal(ResourceHunterText.ZoneKey("Nagafen's Lair"), ResourceHunterText.ZoneKey("Nagafen's Lair (SolB)"));
        Assert.Equal("2026-11-20", ResourceHunterText.Day(new DateTime(2026, 11, 21, 7, 59, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-11-21", ResourceHunterText.Day(new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(ResourceBonus.Skill, ResourceHunterText.FromKey("tradeskill"));
        Assert.Equal("rare", ResourceHunterText.Key(ResourceBonus.RareSpawn));
        Assert.Equal("https://fangbreaker.zone", ResourceHunterText.WebsiteFor("Fangbreaker"));
        Assert.Null(ResourceHunterText.WebsiteFor("vaniki")); // other servers use EQ Overwatch data only
    }

    [Fact]
    public void A_live_bonus_line_is_told_once_per_zone_entry_and_kept_for_the_day()
    {
        var store = new ResourceHunterStore();
        var told = new List<string>();
        store.Found += (c, s, z, b) => told.Add($"{c} {z} {b}");
        store.OnLine("Ream", "frostreaver", "You have entered The Akheva Ruins.", Noon);
        store.OnLine("Ream", "frostreaver", "This land is saturated with unstable magic, multiplying the loot that may be found here.", Noon);
        store.OnLine("Ream", "frostreaver", "This land is saturated with unstable magic, multiplying the loot that may be found here.", Noon); // a kill repeats it
        store.OnLine("Ream", "frostreaver", "You have entered The Akheva Ruins.", Noon);
        store.OnLine("Ream", "frostreaver", "This land is saturated with unstable magic, multiplying the loot that may be found here.", Noon);

        Assert.Equal(new[] { "Ream The Akheva Ruins Loot", "Ream The Akheva Ruins Loot" }, told);
        ResourceZoneBonus today = Assert.Single(store.Today("Frostreaver", Noon));
        Assert.Equal(("The Akheva Ruins", ResourceBonus.Loot, ResourceBonusSource.Logs), (today.Zone, today.Bonus, today.Source));
        Assert.Empty(store.Today("frostreaver", Noon.AddDays(1)));
        Assert.Empty(store.Today("fangbreaker", Noon)); // server specific
    }

    [Fact]
    public void Logs_are_read_for_history_and_reports_fill_in_the_rest_of_today()
    {
        string log = Path.Combine(_dir, "eqlog_Ream_frostreaver.txt");
        File.WriteAllLines(log, new[]
        {
            "[Wed Nov 18 12:00:00 2026] You have entered The Grey.",
            "[Wed Nov 18 12:00:10 2026] This land is saturated with unstable magic, improving the experience tied to abilities for all who venture here.",
            "[Thu Nov 19 12:00:00 2026] You have entered The Grey.",
            "[Thu Nov 19 12:00:10 2026] This land is saturated with unstable magic, improving the experience tied to abilities for all who venture here.",
        });
        var store = new ResourceHunterStore(Path.Combine(_dir, "resourcehunter.json"));
        Assert.Equal(2, store.Scan(new[] { (log, "frostreaver") }, Noon));
        File.AppendAllLines(log, new[]
        {
            "[Fri Nov 20 12:00:10 2026] This land is saturated with unstable magic, multiplying the coin that may be found here.", // still in The Grey
        });
        Assert.Equal(1, new ResourceHunterStore(Path.Combine(_dir, "resourcehunter.json")).Scan(new[] { (log, "frostreaver") }, Noon));

        var again = new ResourceHunterStore(Path.Combine(_dir, "resourcehunter.json"));
        Assert.Equal(new[] { (ResourceBonus.Aa, 2), (ResourceBonus.Coin, 1) }, again.History("frostreaver", "Grey", Noon));
        again.SetReported("frostreaver", ResourceBonusSource.Website, "2026-11-20",
            new[] { new ResourceZoneBonus("Grey", ResourceBonus.Loot, ResourceBonusSource.Website), new ResourceZoneBonus("Karnor's Castle", ResourceBonus.Aa, ResourceBonusSource.Website) });
        again.SetReported("frostreaver", ResourceBonusSource.Shared, "2026-11-20",
            new[] { new ResourceZoneBonus("Karnor's Castle", ResourceBonus.Aa, ResourceBonusSource.Shared, 3) });

        IReadOnlyList<ResourceZoneBonus> today = again.Today("frostreaver", Noon);
        Assert.Equal(new[] { ("Karnor's Castle", ResourceBonusSource.Shared), ("The Grey", ResourceBonusSource.Logs) }, today.Select(z => (z.Zone, z.Source)));
        Assert.Equal(ResourceBonus.Coin, again.TodayIn("frostreaver", "Grey", Noon)!.Bonus); // your logs beat the website

        ResourceFinding finding = Assert.Single(again.Unshared("frostreaver", Noon));
        again.MarkShared("frostreaver", new[] { finding });
        Assert.Empty(again.Unshared("frostreaver", Noon));
    }

    [Fact]
    public void The_website_and_the_shared_list_are_read()
    {
        const string site = """
            {"ok":true,"data":[
              {"name":"Acrylia Caverns","bonus":"experience","state":"ACTIVE","bonusDate":"2026-11-20","minLevel":40,"maxLevel":60},
              {"name":"Ak'Anon","bonus":"unconfirmed","state":"UNCONFIRMED","bonusDate":"2026-11-20"},
              {"name":"Dreadlands","bonus":"tradeskill","state":"ACTIVE","bonusDate":"2026-11-20"},
              {"name":"Cabilis","bonus":"none","state":"ACTIVE","bonusDate":"2026-11-20"},
              {"name":"Old","bonus":"loot","state":"ACTIVE","bonusDate":"2026-11-19"}]}
            """;
        var parsed = HttpResourceHunterService.ParseWebsite(site, "2026-11-20");
        Assert.Equal(new[] { ("Acrylia Caverns", ResourceBonus.Experience), ("Ak'Anon", ResourceBonus.Unknown), ("Dreadlands", ResourceBonus.Skill), ("Cabilis", ResourceBonus.None) },
            parsed.Select(z => (z.Zone, z.Bonus)));
        Assert.Equal((40, 60), (parsed[0].MinLevel, parsed[0].MaxLevel));

        ResourceZoneBonus shared = Assert.Single(HttpResourceHunterService.ParseShared(
            """{"day":"2026-11-20","zones":[{"z":"Karnor's Castle","b":"aa","n":4},{"z":"Bad","b":"nope","n":1}]}""", "2026-11-20"));
        Assert.Equal(("Karnor's Castle", ResourceBonus.Aa, 4), (shared.Zone, shared.Bonus, shared.Reports));
        Assert.Empty(HttpResourceHunterService.ParseShared("""{"day":"2026-11-19","zones":[{"z":"A","b":"aa","n":1}]}""", "2026-11-20"));
        Assert.Contains("\"b\":\"rare\"", HttpResourceHunterService.Pack(new[] { new ResourceFinding("Howling Stones", ResourceBonus.RareSpawn, "2026-11-20") }));
    }

    private static EncounterSnapshot Fight(string mob, int minute, params (string Name, double Dps)[] who) =>
        new(minute, mob, Noon.AddMinutes(minute), Noon.AddMinutes(minute).AddSeconds(60), 60, 1000, false,
            who.Select(w => new ParticipantSnapshot(w.Name, (long)(w.Dps * 60), 50, w.Dps, w.Dps, 100, 10, 1, 90)).ToList())
        { Server = "frostreaver" };

    [Fact]
    public void Personal_bests_start_quietly_and_are_reported_when_beaten()
    {
        var bests = new PersonalBestStore();
        bests.SetStoragePath(Path.Combine(_dir, "personalbests.json"));
        bool Own(string name) => name == "Ream";
        Assert.Empty(bests.Check(Fight("a sarnak dragoon", 0, ("Ream", 500), ("Other", 900)), Own)); // the first sets it quietly
        PersonalBest overall = Assert.Single(bests.Check(Fight("a sarnak dragoon", 5, ("Ream", 600)), Own));
        Assert.Equal((true, 600.0, 500.0), (overall.Overall, overall.Dps, overall.Previous));
        Assert.Empty(bests.Check(Fight("a sarnak dragoon", 5, ("Ream", 600)), Own)); // the same fight again
        Assert.Empty(bests.Check(Fight("a froglok krup", 10, ("Ream", 300)), Own)); // the first against that mob
        PersonalBest mob = Assert.Single(bests.Check(Fight("a froglok krup", 15, ("Ream", 400)), Own));
        Assert.Equal((false, "a froglok krup", 300.0), (mob.Overall, mob.Mob, mob.Previous));
        Assert.Empty(bests.Check(Fight("a sarnak dragoon", 20, ("Ream", 9000)) with { DurationSeconds = 10 }, Own)); // too short

        var reloaded = new PersonalBestStore();
        reloaded.SetStoragePath(Path.Combine(_dir, "personalbests.json"));
        Assert.True(reloaded.HoldsBest(Fight("a sarnak dragoon", 5, ("Ream", 600)), "Ream"));
        Assert.False(reloaded.HoldsBest(Fight("a sarnak dragoon", 0, ("Ream", 500)), "Ream"));
    }
}
