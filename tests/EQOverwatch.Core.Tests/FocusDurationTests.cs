using System.Globalization;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Spells;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class FocusDurationTests : IDisposable
{
    // Extended Enhancement style: 15%, to level 20 (5% of itself lost per level above), buffs only, 4-tick minimum, three excluded effects.
    private const string FocusEffects =
        "1|128|15|0|100|0$2|134|20|5|100|0$3|138|1|0|100|0$4|137|-101|0|100|0$5|137|-40|0|100|0$6|140|4|0|100|0$7|137|-86|0|100|0$8|311|0|0|100|0";

    private const int FocusId = 9000, Buff = 9001, ShortBuff = 9002, ExcludedBuff = 9003, Dot = 9004, Song = 9005, HighBuff = 9006;

    private static readonly DateTime T0 = new(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>A split row with the given effects at the end and a level for the Warrior (class 0) column.</summary>
    private static string Row(int id, string name, int formula, int ticks, int good, int bard, string effects, int warriorLevel = 0)
    {
        string[] c = SpellTestData.SplitRow(id, name, 0, formula, ticks, good, bard).Split('^');
        c[36] = warriorLevel.ToString(CultureInfo.InvariantCulture);
        c[^1] = effects;
        return string.Join('^', c);
    }

    private SpellDatabase LoadDatabase()
    {
        string path = SpellTestData.WriteFile(Path.Combine(_dir, "spells_us.txt"),
            Row(FocusId, "Extended Enhancement II", 0, 0, 1, 255, FocusEffects),
            Row(Buff, "Test Buff", 11, 100, 1, 255, "1|11|100|0|0|0", warriorLevel: 18),
            Row(HighBuff, "High Buff", 11, 100, 1, 255, "1|11|100|0|0|0", warriorLevel: 25),
            Row(ShortBuff, "Short Buff", 11, 3, 1, 255, "1|11|100|0|0|0", warriorLevel: 10),
            Row(ExcludedBuff, "Excluded Buff", 11, 100, 1, 255, "1|11|100|0|0|0$2|101|1|0|0|0", warriorLevel: 10),
            Row(Dot, "Test Dot", 11, 100, 0, 255, "1|0|-10|0|0|0", warriorLevel: 10),
            Row(Song, "Test Song", 11, 100, 1, 10, "1|11|100|0|0|0"));
        var service = new SpellDatabaseService();
        service.Load(path);
        return service.Database;
    }

    [Fact]
    public void The_effects_field_is_read_into_a_focus()
    {
        DurationFocus focus = Assert.IsType<DurationFocus>(
            SpellEffects.ToFocus(FocusId, "Extended Enhancement II", SpellEffects.Parse(FocusEffects)));

        Assert.Equal(15, focus.Percent);
        Assert.Equal(20, focus.MaxLevel);
        Assert.Equal(5, focus.PercentPerLevelOver);
        Assert.Equal(FocusAppliesTo.Beneficial, focus.AppliesTo);
        Assert.Equal(4, focus.MinDurationTicks);
        Assert.Equal(new[] { 101, 40, 86 }, focus.ExcludedEffects);
        Assert.Equal("Extended Enhancement II (+15%, to level 20)", focus.Label);
    }

    [Fact]
    public void A_spell_without_a_duration_effect_is_not_a_focus()
    {
        Assert.Null(SpellEffects.ToFocus(1, "Plain", SpellEffects.Parse("1|11|100|0|0|0")));
    }

    [Fact]
    public void Loading_a_split_file_finds_the_focus_and_the_class_levels()
    {
        SpellDatabase db = LoadDatabase();

        Assert.Equal(FocusId, Assert.Single(db.DurationFoci).SpellId);
        Assert.NotNull(db.FindFocus(FocusId));
        Assert.Equal(18, db.FindById(Buff)!.ClassLevels![0]);
        Assert.Equal(16, db.FindById(Buff)!.ClassLevels!.Count);
        Assert.Contains(101, db.FindById(ExcludedBuff)!.EffectIds!);
    }

    [Fact]
    public void A_focus_and_the_aa_percentage_add_together()
    {
        SpellDatabase db = LoadDatabase();
        SpellInfo buff = db.FindById(Buff)!;

        Assert.Equal(600, DurationCalculator.Seconds(buff, 0, DurationProfile.None, db));
        Assert.Equal(690, DurationCalculator.Seconds(buff, 0, new DurationProfile(FocusId, null, 0), db)); // 100 x 1.15 = 115 ticks
        Assert.Equal(750, DurationCalculator.Seconds(buff, 0, new DurationProfile(FocusId, null, 10), db)); // 125 ticks
        Assert.Equal(660, DurationCalculator.Seconds(buff, 0, new DurationProfile(null, null, 10), db)); // AA alone
    }

    [Fact]
    public void A_spell_above_the_focus_level_gets_a_smaller_bonus()
    {
        SpellDatabase db = LoadDatabase();
        SpellInfo high = db.FindById(HighBuff)!; // level 25, five over the cap: 15% x (1 - 5 x 5%) = 11.25%

        Assert.Equal(666, DurationCalculator.Seconds(high, 0, new DurationProfile(FocusId, null, 0), db)); // 111 ticks
        // another class with no level listed: no penalty
        Assert.Equal(690, DurationCalculator.Seconds(high, 5, new DurationProfile(FocusId, null, 0), db));
    }

    [Fact]
    public void Short_spells_and_excluded_effects_skip_the_focus_but_keep_the_aa()
    {
        SpellDatabase db = LoadDatabase();
        var focusOnly = new DurationProfile(FocusId, null, 0);

        Assert.Equal(18, DurationCalculator.Seconds(db.FindById(ShortBuff)!, 0, focusOnly, db)); // 3 ticks < 4
        Assert.Equal(600, DurationCalculator.Seconds(db.FindById(ExcludedBuff)!, 0, focusOnly, db));
        Assert.Equal(660, DurationCalculator.Seconds(db.FindById(ExcludedBuff)!, 0, new DurationProfile(FocusId, null, 10), db));
    }

    [Fact]
    public void Debuffs_use_the_detrimental_focus_and_songs_are_never_extended()
    {
        SpellDatabase db = LoadDatabase();
        SpellInfo dot = db.FindById(Dot)!;
        SpellInfo song = db.FindById(Song)!;
        Assert.Equal(TimerOverlayCategory.Debuffs, dot.Category);
        Assert.Equal(TimerOverlayCategory.Songs, song.Category);

        // The beneficial focus does not touch a DoT; the AA percentage does.
        Assert.Equal(600, DurationCalculator.Seconds(dot, 0, new DurationProfile(FocusId, null, 0), db));
        Assert.Equal(720, DurationCalculator.Seconds(dot, 0, new DurationProfile(null, null, 20), db));
        Assert.Equal(600, DurationCalculator.Seconds(song, 7, new DurationProfile(FocusId, null, 50), db));
    }

    [Fact]
    public void Only_spells_you_cast_yourself_get_the_bonus()
    {
        string path = BuffTrackerServiceTests.WriteSpellFile(Path.Combine(_dir, "tracker", "spells_us.txt"));
        var spells = new SpellDatabaseService();
        spells.Load(path);
        var engine = new TriggerEngineService();
        var tracker = new BuffTrackerService(spells, engine);
        tracker.SetTrackedSpells(new[] { 100 });
        tracker.SetDurationProvider(name =>
            name == "Stackprime" ? new CasterDuration(-1, new DurationProfile(null, null, 50)) : null);

        tracker.OnLine("Stackprime", "You begin casting Spirit of Wolf.", T0);
        tracker.OnLine("Stackprime", "You feel quick as a wolf.", T0.AddSeconds(2));
        Assert.Equal(1350, Assert.Single(engine.Snapshot.Timers).DurationSeconds); // 150 ticks + 50% = 225 ticks

        // Someone else's cast landing on the same character stays at the base length.
        var engine2 = new TriggerEngineService();
        var other = new BuffTrackerService(spells, engine2);
        other.SetTrackedSpells(new[] { 100 });
        other.SetDurationProvider(name => new CasterDuration(-1, new DurationProfile(null, null, 50)));
        other.OnLine("Stackprime", "Ream begins to cast a spell. <Spirit of Wolf>", T0);
        other.OnLine("Stackprime", "You feel quick as a wolf.", T0.AddSeconds(2));
        Assert.Equal(900, Assert.Single(engine2.Snapshot.Timers).DurationSeconds);
    }
}
