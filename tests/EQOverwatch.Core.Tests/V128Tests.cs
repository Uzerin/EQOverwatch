using System.Globalization;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>1.0.128: stun, lost-control and food lines, lost control in fights, and raid boss DPS by week.</summary>
public sealed class V128Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc128-" + Guid.NewGuid().ToString("N"));

    public V128Tests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Stamp(DateTime t) => "[" + t.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "] ";

    [Theory]
    [InlineData("You are stunned!", NoticeKind.Stunned, "")]
    [InlineData("You are no longer stunned.", NoticeKind.StunEnded, "")]
    [InlineData("You lose control of yourself!", NoticeKind.LostControl, "")]
    [InlineData("You have control of yourself again.", NoticeKind.ControlBack, "")]
    [InlineData("You are low on food and drink.", NoticeKind.FoodLow, "low on food and drink")]
    [InlineData("You are out of food and drink.", NoticeKind.FoodOut, "out of food and drink")]
    [InlineData("You are out of drink and low on food.", NoticeKind.FoodOut, "out of drink and low on food")]
    [InlineData("You are out of food.", NoticeKind.FoodOut, "out of food")]
    [InlineData("You are encumbered!", NoticeKind.None, "")]
    [InlineData("Tankard says, 'You are out of food.'", NoticeKind.None, "")]
    public void Stun_control_and_food_lines_are_read(string message, NoticeKind kind, string name)
    {
        NoticeLine notice = NoticeLineParser.Parse(message);
        Assert.Equal(kind, notice.Kind);
        if (kind != NoticeKind.None)
        {
            Assert.Equal(name, notice.Name);
        }
    }

    [Fact]
    public async Task A_charm_in_a_fight_is_kept_with_its_spell_caster_and_length()
    {
        var t0 = new DateTime(2026, 11, 22, 16, 54, 20);
        var lines = new List<string>();
        for (int s = 0; s <= 140; s += 5)
        {
            lines.Add(Stamp(t0.AddSeconds(s)) + "Criare Sunmane hits YOU for 300 points of damage.");
            lines.Add(Stamp(t0.AddSeconds(s)) + "You slash Criare Sunmane for 100 points of damage.");
            if (s == 5)
            {
                lines.Add(Stamp(t0.AddSeconds(9)) + "Criare Sunmane begins casting Phoenix Charm.");
                lines.Add(Stamp(t0.AddSeconds(9)) + "You lose control of yourself!");
            }

            if (s == 130)
            {
                lines.Add(Stamp(t0.AddSeconds(133)) + "You have control of yourself again.");
                lines.Add(Stamp(t0.AddSeconds(134)) + "You lose control of yourself!"); // no cast seen this time
            }
        }

        string path = Path.Combine(_dir, "eqlog_Tankard_frostreaver.txt");
        File.WriteAllLines(path, lines);
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Tankard");
        await new HistoricalLogImporter(tracker).ImportAsync(new[] { path }, null, null);

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.All);
        Assert.Equal(2, fight.LostControl.Count);
        Assert.Equal(new LostControlSnapshot("Tankard", t0.AddSeconds(9), t0.AddSeconds(133), "Phoenix Charm", "Criare Sunmane"), fight.LostControl[0]);
        Assert.Equal(new LostControlSnapshot("Tankard", t0.AddSeconds(134), null, string.Empty, string.Empty), fight.LostControl[1]);
    }

    private static EncounterSnapshot BossFight(string boss, DateTime start, params (string Name, double Dps)[] players) =>
        new(1, boss, start, start.AddMinutes(5), 300, 100_000, false,
            players.Select(p => new ParticipantSnapshot(p.Name, (long)(p.Dps * 300), 10, p.Dps, p.Dps, 100, 10, 1, 0.9, 0, 0, 0)).ToArray())
        { Server = "frostreaver" };

    [Fact]
    public void Raid_boss_dps_is_kept_per_character_and_shown_by_week()
    {
        var store = new RaidDpsStore();
        store.SetStoragePath(Path.Combine(_dir, "raiddps.json"));
        var today = new DateTime(2026, 11, 27, 12, 0, 0); // a Friday; its week starts Monday Nov 23
        Func<string, bool> boss = m => m != "a gnome";
        Func<string, bool> own = n => n is "Tankard" or "Mendwell";

        store.Record(new[]
        {
            BossFight("Lady Vox", new DateTime(2026, 10, 19, 20, 0, 0), ("Tankard", 600), ("Porter", 900)),
            BossFight("Lord Nagafen", new DateTime(2026, 10, 20, 21, 0, 0), ("Tankard", 700)),
            BossFight("Lady Vox", new DateTime(2026, 11, 23, 20, 0, 0), ("Tankard", 800), ("Mendwell", 50)),
            BossFight("a gnome", new DateTime(2026, 11, 24, 20, 0, 0), ("Tankard", 5000)),
            BossFight("Lady Vox", new DateTime(2026, 8, 1, 20, 0, 0), ("Tankard", 100)), // older than 12 weeks
        }, boss, own);
        // The same fight read again (a fuller read) replaces the first entry.
        store.Record(new[] { BossFight("Lady Vox", new DateTime(2026, 11, 23, 20, 0, 30), ("Tankard", 820)) }, boss, own);
        store.Flush();

        var reloaded = new RaidDpsStore();
        reloaded.SetStoragePath(Path.Combine(_dir, "raiddps.json"));
        Assert.Equal(5, reloaded.Entries().Count); // Vox x3, Nagafen, Mendwell's Vox (the gnome and Porter are left out)

        RaidDpsTrend trend = RaidDpsTrend.For(reloaded.Entries(), "Tankard", today, "frostreaver")!;
        Assert.Equal(new[] { new RaidDpsWeek(new DateTime(2026, 10, 19), 650, 2), new RaidDpsWeek(new DateTime(2026, 11, 23), 820, 1) }, trend.Weeks);
        Assert.Equal(820, trend.RecentDps);
        Assert.Equal(650, trend.EarlierDps);
        Assert.Equal("Last 4 weeks 820 DPS against 650 the 4 weeks before (+26%) · 3 raid boss fights", trend.Describe(CultureInfo.InvariantCulture));
        Assert.Null(RaidDpsTrend.For(reloaded.Entries(), "Mendwell", today)); // one week only
    }

    [Fact]
    public void A_recorded_boss_fight_is_remembered_once_read_back()
    {
        var store = new RaidDpsStore();
        store.SetStoragePath(Path.Combine(_dir, "raiddps.json"));
        var fight = new BossFight("Lady Vox", new DateTime(2026, 11, 23, 20, 0, 0), 300, 1, 1, 0, true);
        Assert.False(store.WasReadBack(fight));
        store.MarkReadBack(fight);
        store.Flush();

        var reloaded = new RaidDpsStore();
        reloaded.SetStoragePath(Path.Combine(_dir, "raiddps.json"));
        Assert.True(reloaded.WasReadBack(fight));
    }
}
