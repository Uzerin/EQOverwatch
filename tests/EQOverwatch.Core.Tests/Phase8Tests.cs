using System.IO;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Spells;
using Xunit;
using static EQOverwatch.Core.Tests.SpellTestData;

namespace EQOverwatch.Core.Tests;

public sealed class Phase8Tests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqo-" + Guid.NewGuid().ToString("N"));
    private readonly SpellDatabaseService _spells = new();
    private readonly TriggerEngineService _engine = new();
    private readonly BuffTrackerService _tracker;

    public Phase8Tests()
    {
        Directory.CreateDirectory(_dir);
        _spells.Load(WriteFile(Path.Combine(_dir, "spells_us.txt"),
            LegacyRow(100, "Spirit of Wolf", "You feel quick as a wolf.", " looks faster.", "Your speed returns to normal.", 1500, 11, 150),
            LegacyRow(101, "Selo's Accelerando", "You feel your pace quicken.", " moves faster.", "pace returns to normal.", 0, 11, 30)));
        _tracker = new BuffTrackerService(_spells, _engine);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private IReadOnlyList<ActiveTimerSnapshot> Timers => _engine.Snapshot.Timers;

    // ───────────────────────── per-spell overlays ─────────────────────────

    [Fact]
    public void The_chosen_overlay_decides_where_the_bar_appears()
    {
        _tracker.SetTrackedSpellOverlays(new[]
        {
            new KeyValuePair<int, TimerOverlayCategory>(100, TimerOverlayCategory.Songs),
            new KeyValuePair<int, TimerOverlayCategory>(101, TimerOverlayCategory.Debuffs),
        });

        _tracker.OnLine("Stackprime", "You begin singing Spirit of Wolf.", T0); // a Songs-overlay spell starts on the sing line
        _tracker.OnLine("Stackprime", "You feel your pace quicken.", T0.AddSeconds(1));

        Assert.Equal(TimerOverlayCategory.Songs, Timers.Single(t => t.Text.EndsWith("Spirit of Wolf")).Category);
        Assert.Equal(TimerOverlayCategory.Debuffs, Timers.Single(t => t.Text.EndsWith("Selo's Accelerando")).Category);
    }

    [Fact]
    public void A_sung_song_still_follows_the_chosen_overlay()
    {
        _tracker.SetTrackedSpellOverlays(new[] { new KeyValuePair<int, TimerOverlayCategory>(101, TimerOverlayCategory.Buffs) });

        _tracker.OnLine("Stackprime", "You begin singing Selo's Accelerando.", T0);
        _tracker.OnLine("Stackprime", "You feel your pace quicken.", T0.AddSeconds(1));

        Assert.Equal(TimerOverlayCategory.Buffs, Assert.Single(Timers).Category);
    }

    [Fact]
    public void Plain_id_tracking_forgets_earlier_overlay_choices()
    {
        _tracker.SetTrackedSpellOverlays(new[] { new KeyValuePair<int, TimerOverlayCategory>(100, TimerOverlayCategory.RaidTriggers) });

        _tracker.SetTrackedSpells(new[] { 100 });
        _tracker.OnLine("Stackprime", "You feel quick as a wolf.", T0);

        Assert.NotEqual(TimerOverlayCategory.RaidTriggers, Assert.Single(Timers).Category);
    }

    [Fact]
    public void The_tracker_counts_the_bars_it_starts()
    {
        _tracker.SetTrackedSpellOverlays(new[] { new KeyValuePair<int, TimerOverlayCategory>(100, TimerOverlayCategory.Buffs) });
        Assert.Equal(0, _tracker.TimersStarted);

        _tracker.OnLine("Stackprime", "You feel quick as a wolf.", T0);
        _tracker.OnLine("Stackprime", "You feel your pace quicken.", T0); // not tracked

        Assert.Equal(1, _tracker.TimersStarted);
        Assert.Equal("Stackprime: Spirit of Wolf", _tracker.LastStarted);
    }

    // ───────────────────────── settings ─────────────────────────

    [Fact]
    public void Tracked_spells_round_trip_and_are_cleaned_up_on_load()
    {
        string path = Path.Combine(_dir, "settings.json");
        var settings = new AppSettings();
        settings.TrackedSpells.Add(new TrackedSpellSetting { SpellId = 100, SpellName = "Spirit of Wolf", Overlay = TimerOverlayCategory.Songs });
        settings.TrackedSpells.Add(new TrackedSpellSetting { SpellId = 100, SpellName = "Duplicate", Overlay = TimerOverlayCategory.Debuffs });
        settings.TrackedSpells.Add(new TrackedSpellSetting { SpellId = 0, SpellName = "Bad id" });
        settings.TrackedSpells.Add(new TrackedSpellSetting { SpellId = 5, SpellName = new string('x', 500), IsEnabled = false });

        new AppSettingsService(path).Save(settings);
        AppSettings loaded = new AppSettingsService(path).Load();

        Assert.Equal(new[] { 100, 5 }, loaded.TrackedSpells.Select(t => t.SpellId));
        Assert.Equal(TimerOverlayCategory.Songs, loaded.TrackedSpells[0].Overlay);
        Assert.Equal("Spirit of Wolf", loaded.TrackedSpells[0].SpellName);
        Assert.False(loaded.TrackedSpells[1].IsEnabled);
        Assert.Equal(TrackedSpellSetting.MaxNameLength, loaded.TrackedSpells[1].SpellName.Length);
    }

    [Fact]
    public void Settings_files_without_the_tracked_spell_list_load_with_an_empty_one()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "TrackedSpellIds": [100, 101] }""");

        AppSettings loaded = new AppSettingsService(path).Load();

        Assert.Empty(loaded.TrackedSpells);
        Assert.Equal(new[] { 100, 101 }, loaded.TrackedSpellIds.OrderBy(i => i));
    }
}
