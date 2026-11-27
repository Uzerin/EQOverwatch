using System.Text;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>1.0.127: fights read in short windows get their real zone, boss zone scans skip mentions from elsewhere, and boss spell summaries.</summary>
public sealed class V127Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc127-" + Guid.NewGuid().ToString("N"));

    public V127Tests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Stamp(DateTime t) => "[" + t.ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture) + "] ";

    [Fact]
    public void The_last_zone_line_before_an_offset_is_found_and_area_messages_are_passed_over()
    {
        string text = string.Join("\n",
            "[Wed Nov 25 18:00:00 2026] You have entered The Nexus.",
            "[Wed Nov 25 19:00:00 2026] You have entered Sanctus Seru.",
            "[Wed Nov 25 19:05:00 2026] You have entered an Arena (PvP) area.",
            "[Wed Nov 25 19:06:00 2026] Tankard says, 'ready'",
            "[Wed Nov 25 21:00:00 2026] You have entered The Nexus.") + "\n";
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        long offset = text.IndexOf("[Wed Nov 25 21:00:00", StringComparison.Ordinal);
        using var stream = new MemoryStream(bytes);

        Assert.Equal("[Wed Nov 25 19:00:00 2026] You have entered Sanctus Seru.", LogSeek.LastZoneLineBefore(stream, offset));
        Assert.Null(LogSeek.LastZoneLineBefore(stream, 0));
    }

    [Fact]
    public void A_zone_line_that_crosses_a_one_megabyte_read_is_still_found()
    {
        const string zone = "[Wed Nov 25 19:00:00 2026] You have entered Sanctus Seru.\n";
        int filler = (1 << 20) - 49; // the last 1 MB read then starts inside the zone line, before its "You have entered"
        string text = zone + "[Wed Nov 25 19:00:01 2026] " + new string('x', filler - 28) + "\n";
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        using var stream = new MemoryStream(bytes);

        Assert.Equal(zone.TrimEnd('\n'), LogSeek.LastZoneLineBefore(stream, bytes.Length));
    }

    [Fact]
    public async Task A_fight_read_in_a_short_window_gets_the_zone_entered_hours_before()
    {
        var t0 = new DateTime(2026, 11, 25, 10, 0, 0);
        var lines = new List<string> { Stamp(t0) + "You have entered Sanctus Seru." };
        for (int i = 1; i < 40_000; i++)
        {
            lines.Add(Stamp(t0.AddSeconds(i)) + "Tankard says, 'waiting for the raid to form up, any minute now'");
        }

        DateTime fight = t0.AddHours(12);
        lines.Add(Stamp(fight) + "You slash a gnome for 100 points of damage.");
        lines.Add(Stamp(fight.AddSeconds(2)) + "You slash a gnome for 100 points of damage.");
        string path = Path.Combine(_dir, "eqlog_Tankard_frostreaver.txt");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        Assert.True(new FileInfo(path).Length > 1_000_000);

        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Tankard");
        await new HistoricalLogImporter(tracker).ImportAsync(new[] { path }, fight.AddMinutes(-1), fight.AddMinutes(1));

        EncounterSnapshot encounter = Assert.Single(tracker.Snapshot.All);
        Assert.Equal("Sanctus Seru", encounter.Zone);
    }

    [Fact]
    public void Faction_lines_and_kill_announcements_do_not_place_a_boss()
    {
        string log = Path.Combine(_dir, "eqlog_Mendwell_frostreaver.txt");
        File.WriteAllLines(log, new[]
        {
            "[Wed Nov 25 19:00:00 2026] You have entered Sanctus Seru.",
            "[Wed Nov 25 19:10:00 2026] Lord Inquisitor Seru hits YOU for 900 points of damage.",
            "[Wed Nov 25 20:00:00 2026] You have entered The Nexus.",
            "[Wed Nov 25 20:01:00 2026] Your faction standing with Lord Inquisitor Seru got worse.",
            "[Wed Nov 25 20:01:01 2026] Your faction standing with Lord Inquisitor Seru got worse.",
            "[Wed Nov 25 20:02:00 2026] Lord Inquisitor Seru has been defeated by a group of hardy adventurers! Bravo!",
            "[Wed Nov 25 20:02:01 2026] Lord Inquisitor Seru has been defeated by a group of hardy adventurers! Bravo!",
            "[Wed Nov 25 20:03:00 2026] Porter tells you, 'Lord Inquisitor Seru down'",
        });

        Assert.Equal("Sanctus Seru", BossZoneScanner.Scan(new[] { log }, new[] { "Lord Inquisitor Seru" })["Lord Inquisitor Seru"]);
        Assert.False(BossZoneScanner.NamesBossFromAnywhere("Lord Inquisitor Seru has been slain by Shieldwall!"));
    }

    [Fact]
    public void A_bosss_spells_count_each_cast_once_with_the_shortest_time_between_casts()
    {
        var start = new DateTime(2026, 11, 25, 19, 0, 0);
        BossCastSnapshot Cast(int seconds, string spell) => new(start.AddSeconds(seconds), spell);
        var first = new EncounterSnapshot(1, "Vyzh`dra the Cursed", start, start.AddMinutes(15), 900, 1, false, Array.Empty<ParticipantSnapshot>(),
            BossCasts: new[]
            {
                Cast(10, "Mass Insanity"), Cast(11, "Mass Insanity"), // one cast seen a second apart
                Cast(23, "Mass Insanity"), Cast(40, "Mass Insanity"),
                Cast(30, "Caustic Mist"), Cast(54, "Caustic Mist"),
            });
        var later = first with
        {
            Id = 2,
            StartTime = start.AddHours(2),
            BossCasts = new[] { Cast(7300, "Mass Insanity") }, // the next fight: far apart, not a recast
        };
        var other = first with { Id = 3, TargetName = "a gnome" };

        IReadOnlyList<BossSpellSeen> spells = BossSpellSummary.For(new[] { first, later, other }, "Vyzh`dra the Cursed");

        Assert.Equal(new[] { "Mass Insanity", "Caustic Mist" }, spells.Select(s => s.Spell));
        Assert.Equal(new BossSpellSeen("Mass Insanity", 4, 13), spells[0]);
        Assert.Equal(new BossSpellSeen("Caustic Mist", 2, 24), spells[1]);
        Assert.Empty(BossSpellSummary.For(new[] { first }, "Lady Vox"));
    }
}
