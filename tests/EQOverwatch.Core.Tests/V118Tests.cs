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
}
