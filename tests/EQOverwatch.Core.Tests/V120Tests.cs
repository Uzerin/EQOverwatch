using System.Globalization;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Spells;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V120Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Reloading_the_buff_bars_replaces_old_bars_with_ones_started_from_the_logs()
    {
        var spells = new SpellDatabaseService();
        spells.Load(BuffTrackerServiceTests.WriteSpellFile(Path.Combine(_dir, "spells_us.txt")));
        var engine = new TriggerEngineService();
        var tracker = new BuffTrackerService(spells, engine);
        tracker.SetTrackedSpells(new[] { 100 });
        int landings = 0;
        tracker.Landed += (_, _, _, _) => landings++;

        var cast = new DateTime(2026, 11, 25, 19, 13, 31, DateTimeKind.Local);
        DateTime castUtc = cast.ToUniversalTime();
        engine.StartTimer(BuffTrackerService.TimerSourceId, "Buffs & songs", "Tankard", "Tankard: Spirit of Wolf", 900, castUtc.AddMinutes(-60),
            TimerOverlayCategory.Buffs, note: "+30% from an old version");

        string log = Path.Combine(_dir, "eqlog_Tankard_frostreaver.txt");
        File.WriteAllLines(log, new[]
        {
            Line(cast.AddHours(-5), "You begin casting Spirit of Wolf."), // before the window
            Line(cast, "You begin casting Spirit of Wolf."),
            Line(cast.AddSeconds(2), "You feel quick as a wolf."),
        });

        var lines = BuffBarReload.ReadRecent(new[] { (log, "Tankard", "frostreaver") }, cast.AddHours(-3));
        Assert.Equal(2, lines.Count);
        Assert.Equal(1, BuffBarReload.Replay(tracker, engine, lines, castUtc.AddMinutes(5)));

        ActiveTimerSnapshot bar = Assert.Single(engine.Snapshot.Timers);
        Assert.Equal(string.Empty, bar.Note);
        Assert.Equal(castUtc.AddSeconds(2 + 900), bar.ExpiresUtc); // the time it has left, counted from the cast in the log
        Assert.Equal(0, landings); // past fights' uptime is not counted again
    }

    private static string Line(DateTime local, string message) =>
        "[" + local.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "] " + message;
}
