using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Spells;
using Xunit;
using static EQOverwatch.Core.Tests.SpellTestData;

namespace EQOverwatch.Core.Tests;

/// <summary>Every timer carries a category, and each of the four overlays shows only its own.</summary>
public sealed class OverlayRoutingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static IEnumerable<string> Texts(TriggerEngineService engine, TimerOverlayCategory category) =>
        engine.Snapshot.GetActive(Now.AddSeconds(1), category).Select(t => t.Text);

    [Fact]
    public void User_trigger_rules_route_to_the_overlay_they_choose_and_default_to_raid_triggers()
    {
        var engine = new TriggerEngineService();
        var rules = new[]
        {
            new TriggerRule { Name = "b", Pattern = "^b$", AlertTextTemplate = "buff alert", OverlayCategory = TimerOverlayCategory.Buffs },
            new TriggerRule { Name = "d", Pattern = "^d$", AlertTextTemplate = "", TimerDurationSeconds = 20, TimerLabelTemplate = "debuff bar", OverlayCategory = TimerOverlayCategory.Debuffs },
            new TriggerRule { Name = "s", Pattern = "^s$", AlertTextTemplate = "song alert", OverlayCategory = TimerOverlayCategory.Songs },
            new TriggerRule { Name = "r", Pattern = "^r$", AlertTextTemplate = "raid alert" }, // default category
        };
        Assert.Empty(engine.SetTriggers(rules.SelectMany(r => r.ToDefinitions())));

        foreach (string line in new[] { "b", "d", "s", "r" })
        {
            engine.OnLine("Stackprime", line, Now);
        }

        Assert.Equal(new[] { "buff alert" }, Texts(engine, TimerOverlayCategory.Buffs));
        Assert.Equal(new[] { "debuff bar" }, Texts(engine, TimerOverlayCategory.Debuffs));
        Assert.Equal(new[] { "song alert" }, Texts(engine, TimerOverlayCategory.Songs));
        Assert.Equal(new[] { "raid alert" }, Texts(engine, TimerOverlayCategory.RaidTriggers));
        Assert.Equal(TimerOverlayCategory.RaidTriggers, new TriggerRule().OverlayCategory);
    }

    [Fact]
    public void A_rules_category_applies_to_both_its_alert_and_its_timer()
    {
        var rule = new TriggerRule
        {
            Pattern = "x", AlertTextTemplate = "alert", TimerDurationSeconds = 10, TimerLabelTemplate = "bar",
            OverlayCategory = TimerOverlayCategory.Songs,
        };

        Assert.All(rule.ToDefinitions(), d => Assert.Equal(TimerOverlayCategory.Songs, d.OverlayCategory));
        Assert.Equal(2, rule.ToDefinitions().Count);
    }

    [Fact]
    public void StartTimer_defaults_to_raid_triggers_and_accepts_any_category()
    {
        var engine = new TriggerEngineService();
        var source = Guid.NewGuid();

        engine.StartTimer(source, "src", "A", "default", 30, Now);
        engine.StartTimer(source, "src", "B", "songs", 30, Now, TimerOverlayCategory.Songs);

        Assert.Equal(TimerOverlayCategory.RaidTriggers, engine.Snapshot.Timers.Single(t => t.Text == "default").Category);
        Assert.Equal(TimerOverlayCategory.Songs, engine.Snapshot.Timers.Single(t => t.Text == "songs").Category);
    }

    [Fact]
    public void GetActive_by_category_excludes_other_categories_and_expired_timers()
    {
        var engine = new TriggerEngineService();
        var source = Guid.NewGuid();
        engine.StartTimer(source, "src", "A", "short buff", 5, Now, TimerOverlayCategory.Buffs);
        engine.StartTimer(source, "src", "A", "long buff", 60, Now, TimerOverlayCategory.Buffs);
        engine.StartTimer(source, "src", "A", "debuff", 60, Now, TimerOverlayCategory.Debuffs);

        Assert.Equal(new[] { "long buff", "short buff" }, engine.Snapshot.GetActive(Now.AddSeconds(1), TimerOverlayCategory.Buffs).Select(t => t.Text).OrderBy(t => t));
        Assert.Equal(new[] { "long buff" }, engine.Snapshot.GetActive(Now.AddSeconds(10), TimerOverlayCategory.Buffs).Select(t => t.Text));
        Assert.Equal(new[] { "debuff" }, engine.Snapshot.GetActive(Now.AddSeconds(10), TimerOverlayCategory.Debuffs).Select(t => t.Text));
        Assert.Empty(engine.Snapshot.GetActive(Now.AddSeconds(10), TimerOverlayCategory.Songs));
        Assert.Empty(engine.Snapshot.GetActive(Now.AddSeconds(10), TimerOverlayCategory.RaidTriggers));
    }

    [Fact]
    public void Tracked_spells_land_in_the_overlay_for_their_category_and_songs_sung_are_songs()
    {
        WriteFile(Path.Combine(_dir, "spells_us.txt"),
            LegacyRow(1, "Haste", "You feel quick.", " looks quick.", "Your quickness fades.", 1000, 11, 100, goodEffect: 1),
            LegacyRow(2, "Disease Cloud", "You feel sick.", " looks sick.", "Your sickness fades.", 1000, 11, 100, goodEffect: 0),
            LegacyRow(3, "Selo's Accelerando", "You pick up speed.", " moves fast.", "Your speed fades.", 0, 11, 3, goodEffect: 1, bardLevel: 5),
            // Not marked as a bard song in the file, but short enough to be one and sung with "sing":
            LegacyRow(4, "Mystery Tune", "You hum along.", " hums along.", "Your tune fades.", 0, 11, 3),
            // Sung but far too long to be a song: stays a buff.
            LegacyRow(5, "Long Hymn", "You feel hymned.", " feels hymned.", "Your hymn fades.", 0, 11, 100));
        var spells = new SpellDatabaseService();
        spells.Load(Path.Combine(_dir, "spells_us.txt"));
        var engine = new TriggerEngineService();
        var tracker = new BuffTrackerService(spells, engine);
        tracker.SetTrackedSpells(new[] { 1, 2, 3, 4, 5 });

        tracker.OnLine("Stackprime", "You feel quick.", Now);
        tracker.OnLine("Stackprime", "You feel sick.", Now);
        tracker.OnLine("Stackprime", "You begin singing Selo's Accelerando.", Now); // a bard song starts on the sing line only

        tracker.OnLine("Stackprime", "You begin singing Mystery Tune.", Now);
        tracker.OnLine("Stackprime", "You hum along.", Now.AddSeconds(1));

        tracker.OnLine("Stackprime", "You begin singing Long Hymn.", Now);
        tracker.OnLine("Stackprime", "You feel hymned.", Now.AddSeconds(1));

        CategoryOf("Stackprime: Haste", TimerOverlayCategory.Buffs);
        CategoryOf("Stackprime: Disease Cloud", TimerOverlayCategory.Debuffs);
        CategoryOf("Stackprime: Selo's Accelerando", TimerOverlayCategory.Songs);
        CategoryOf("Stackprime: Mystery Tune", TimerOverlayCategory.Songs);   // 18s and sung
        CategoryOf("Stackprime: Long Hymn", TimerOverlayCategory.Buffs);      // 600s, so not a song even when sung

        void CategoryOf(string text, TimerOverlayCategory expected) =>
            Assert.Equal(expected, engine.Snapshot.Timers.Single(t => t.Text == text).Category);

        // Cast with "casting" instead, the same short spell stays in its file category.
        engine.ClearTimers();
        tracker.OnLine("Stackprime", "You begin casting Mystery Tune.", Now);
        tracker.OnLine("Stackprime", "You hum along.", Now.AddSeconds(1));
        CategoryOf("Stackprime: Mystery Tune", TimerOverlayCategory.Buffs);
    }

    [Fact]
    public void Spells_announced_for_other_casters_with_sing_start_no_bars()
    {
        WriteFile(Path.Combine(_dir, "spells_us.txt"),
            LegacyRow(4, "Mystery Tune", "You hum along.", " hums along.", "Your tune fades.", 0, 11, 3));
        var spells = new SpellDatabaseService();
        spells.Load(Path.Combine(_dir, "spells_us.txt"));
        var engine = new TriggerEngineService();
        var tracker = new BuffTrackerService(spells, engine);
        tracker.SetTrackedSpells(new[] { 4 });

        tracker.OnLine("Stackprime", "Ream begins to sing a song. <Mystery Tune>", Now);
        tracker.OnLine("Stackprime", "Oodles hums along.", Now.AddSeconds(1));

        Assert.Empty(engine.Snapshot.Timers);
    }
}
