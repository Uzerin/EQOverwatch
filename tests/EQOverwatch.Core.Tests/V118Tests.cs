using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Spells;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V118Tests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 26, 3, 13, 31, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Another_box_seeing_your_characters_buff_land_keeps_the_casters_aa_bonus()
    {
        var spells = new SpellDatabaseService();
        spells.Load(BuffTrackerServiceTests.WriteSpellFile(Path.Combine(_dir, "spells_us.txt")));
        var engine = new TriggerEngineService();
        var tracker = new BuffTrackerService(spells, engine);
        tracker.SetTrackedSpells(new[] { 100 });
        var bonus = new DurationProfile(null, null, 0, new[] { new AaDurationBonus { Percent = 30, Type = 1 } });
        tracker.SetDurationProvider(name => name == "Mendwell" ? new CasterDuration(-1, bonus) : null); // only your own characters have one

        // Mendwell's own log, then Tankard's box sees the same cast land on Mendwell.
        tracker.OnLine("Mendwell", "You begin casting Spirit of Wolf.", T0);
        tracker.OnLine("Mendwell", "You feel quick as a wolf.", T0.AddSeconds(2));
        tracker.OnLine("Tankard", "Mendwell begins casting Spirit of Wolf.", T0);
        tracker.OnLine("Tankard", "Mendwell looks faster.", T0.AddSeconds(2));
        Assert.All(engine.Snapshot.Timers, t => Assert.Equal(1170, t.DurationSeconds)); // 15:00 + 30%

        // A player you don't monitor gets the base duration.
        engine.ClearTimers();
        tracker.OnLine("Tankard", "Porter begins casting Spirit of Wolf.", T0.AddMinutes(1));
        tracker.OnLine("Tankard", "Gearbox looks faster.", T0.AddMinutes(1).AddSeconds(2));
        Assert.Equal(900, Assert.Single(engine.Snapshot.Timers).DurationSeconds);
    }

    [Fact]
    public void A_bosss_zone_is_where_its_fight_lines_were_not_where_someone_talked_about_it()
    {
        Directory.CreateDirectory(_dir);
        string log = Path.Combine(_dir, "eqlog_Tankard_frostreaver.txt");
        File.WriteAllLines(log, new[]
        {
            "[Wed Nov 25 19:00:00 2026] You have entered Ssraeshza Temple.",
            "[Wed Nov 25 19:10:00 2026] Emperor Ssraeshza hits YOU for 900 points of damage.",
            "[Wed Nov 25 19:10:02 2026] You slash Emperor Ssraeshza for 120 points of damage.",
            "[Wed Nov 25 20:00:00 2026] You have entered The Nexus.",
            "[Wed Nov 25 20:01:00 2026] Mendwell tells the guild, 'Emperor Ssraeshza tomorrow at 8'",
            "[Wed Nov 25 20:02:00 2026] Mendwell begins casting Emperor Ssraeshza.",
        });

        Dictionary<string, string> zones = BossZoneScanner.Scan(new[] { log }, new[] { "Emperor Ssraeshza", "Lady Vox" });
        Assert.Equal("Ssraeshza Temple", Assert.Single(zones, z => z.Key == "Emperor Ssraeshza").Value);
        Assert.DoesNotContain("Lady Vox", zones.Keys);
    }
}
