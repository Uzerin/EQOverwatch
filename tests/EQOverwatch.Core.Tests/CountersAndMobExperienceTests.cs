using System.Globalization;
using EQOverwatch.Core.Experience;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Spells;
using Xunit;
using static EQOverwatch.Core.Tests.SpellTestData;

namespace EQOverwatch.Core.Tests;

/// <summary>Debuff counters and "cured of" lines, and experience by mob per character level.</summary>
public sealed class CountersAndMobExperienceTests : IDisposable
{
    private const int Crud = 2769;
    private const int Venom = 2770;

    private static readonly DateTime T0 = new(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));
    private readonly SpellDatabaseService _spells = new();
    private readonly TriggerEngineService _engine = new();
    private readonly BuffTrackerService _buffs;

    public CountersAndMobExperienceTests()
    {
        string spells = WriteFile(Path.Combine(_dir, "spells_us.txt"),
            SplitRow(Crud, "Spreading Crud", 3000, 11, 10, goodEffect: 0, resistType: 5, effects: "1|35|3|0|100|0$2|7|-10|0|100|0$3|0|-3|0|100|0"),
            SplitRow(Venom, "Crawling Venom", 3000, 11, 10, goodEffect: 0, resistType: 4, effects: "1|35|2|0|100|0$2|36|4|0|100|0"));
        WriteFile(Path.Combine(_dir, "spells_us_str.txt"),
            "#SPELLINDEX^CASTERMETXT^CASTEROTHERTXT^CASTEDMETXT^CASTEDOTHERTXT^SPELLGONE^",
            "2769^^^You are covered in crud.^ is covered in crud.^The crud falls away.^",
            "2770^^^Venom crawls through you.^'s veins crawl with venom.^The venom fades.^");
        _spells.Load(spells);
        _buffs = new BuffTrackerService(_spells, _engine);
        _buffs.SetTrackedSpells(new[] { Crud, Venom });
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private IReadOnlyList<ActiveTimerSnapshot> Timers => _engine.Snapshot.Timers;

    // ───────────────────────── counters and cures ─────────────────────────

    [Fact]
    public void The_spell_file_gives_each_debuff_its_counters()
    {
        Assert.Equal("3 disease", _spells.Database.FindById(Crud)!.CountersText);
        Assert.Equal("2 disease, 4 poison", _spells.Database.FindById(Venom)!.CountersText);
        Assert.Null(SpellCounters.From(new[] { new SpellEffects.Effect(35, -1, 0) })); // a cure removes counters; it has none
    }

    [Fact]
    public void A_debuff_bar_shows_its_counters_and_a_cure_line_turns_it_into_a_short_cured_bar()
    {
        _buffs.OnLine("Ream", "Ream is covered in crud.", T0);
        ActiveTimerSnapshot bar = Assert.Single(Timers);
        Assert.Equal(("Ream: Spreading Crud", "3 disease", TimerOverlayCategory.Debuffs), (bar.Text, bar.Note, bar.Category));

        // A partial cure prints nothing: the bar keeps running with its count.
        _buffs.OnLine("Ream", "Stackprime begins casting Cure Disease.", T0.AddSeconds(5));
        Assert.Equal("Ream: Spreading Crud", Assert.Single(Timers).Text);

        _buffs.OnLine("Oodles", "Ream is cured of Spreading Crud by Stackprime.", T0.AddSeconds(10));
        ActiveTimerSnapshot cured = Assert.Single(Timers);
        Assert.True(cured.Cured);
        Assert.Equal("Ream: Spreading Crud cured by Stackprime", cured.Text);
        Assert.Equal(TimerOverlayCategory.Debuffs, cured.Category);
        Assert.Equal(BuffTrackerService.CuredShowSeconds, cured.DurationSeconds);
        Assert.Empty(_engine.Snapshot.GetActive(T0.AddSeconds(16)));
    }

    [Fact]
    public void You_are_cured_ends_your_own_bar_and_a_cure_of_an_untracked_debuff_changes_nothing()
    {
        _buffs.OnLine("Ream", "You are covered in crud.", T0);
        _buffs.OnLine("Ream", "Ream is cured of Some Other Thing by Stackprime.", T0.AddSeconds(1));
        Assert.False(Assert.Single(Timers).Cured);

        _buffs.OnLine("Ream", "You are cured of Spreading Crud by Stackprime.", T0.AddSeconds(2));
        Assert.Equal("Ream: Spreading Crud cured by Stackprime", Assert.Single(Timers).Text);
    }

    [Fact]
    public void Without_a_cure_line_the_bar_runs_out_on_its_own()
    {
        _buffs.OnLine("Ream", "Ream is covered in crud.", T0);
        Assert.Single(_engine.Snapshot.GetActive(T0.AddSeconds(59)));
        Assert.Empty(_engine.Snapshot.GetActive(T0.AddSeconds(61)));
    }

    // ───────────────────────── experience by mob ─────────────────────────

    private static MobExperienceStore Store(int level = 24)
    {
        var store = new MobExperienceStore();
        store.SetLevelProvider((_, _) => level);
        return store;
    }

    private static void Feed(MobExperienceStore store, DateTime at, params string[] lines)
    {
        foreach (string line in lines)
        {
            store.OnLine("Ream", "frostreaver", line, at);
        }
    }

    [Fact]
    public void A_gain_goes_to_the_kill_line_after_it_or_else_the_one_just_before()
    {
        MobExperienceStore store = Store();
        Feed(store, T0,
            "You gain party experience! (7.800%)",
            "You receive 3 copper as your split.",
            "Orc pawn was slain by Jannder!");
        Feed(store, T0.AddSeconds(30),
            "You have slain a decaying skeleton!",
            "You gain experience! (2.500%)",
            "A decaying skeleton was slain by Dedah!"); // a second kill in the same second takes the gain only if nothing claimed it
        Feed(store, T0.AddSeconds(60), "You gain experience! (1.000%)");
        Feed(store, T0.AddSeconds(70), "Somebody says, 'hello'"); // time passes: the gain had no kill beside it

        Assert.Equal(new[] { ("Orc pawn", 1, 7.8), ("a decaying skeleton", 1, 2.5), ("", 1, 1.0) },
            store.Rows("Ream", "frostreaver").Select(r => (r.Mob, r.Kills, Math.Round(r.TotalPercent, 3))).ToArray());
        Assert.All(store.Rows("Ream", "frostreaver"), r => Assert.Equal(24, r.Level));
    }

    [Fact]
    public void Kills_are_counted_per_level_and_names_are_grouped_without_regard_to_capitals()
    {
        int level = 24;
        var store = new MobExperienceStore();
        store.SetLevelProvider((_, _) => level);
        Feed(store, T0, "You gain experience! (10.000%)", "A werebat was slain by Ream!");
        Feed(store, T0.AddSeconds(20), "You gain experience! (12.000%)", "You have slain a werebat!");
        level = 25;
        Feed(store, T0.AddSeconds(40), "You gain experience! (8.000%)", "You have slain a werebat!");
        Feed(store, T0.AddSeconds(60), "You gain party experience!", "A werebat was slain by Oodles!"); // no percent: a kill at 0%

        IReadOnlyList<MobExperienceRow> rows = store.Rows("Ream", "frostreaver");
        Assert.Equal(new[] { (25, "a werebat", 2, 4.0), (24, "a werebat", 2, 11.0) },
            rows.Select(r => (r.Level, r.Mob, r.Kills, Math.Round(r.AveragePercent, 3))).ToArray());
        Assert.Empty(store.Rows("Ream", "newserver")); // kept per server
    }

    [Fact]
    public void Live_the_kill_that_causes_a_level_up_counts_at_the_old_level()
    {
        int level = 24;
        var store = new MobExperienceStore();
        store.SetLevelProvider((_, _) => level);
        level = 25; // the roster has already read the level-up line
        Feed(store, T0, "You have gained a level! Welcome to level 25!", "You gain party experience! (4.043%)", "A giant wasp worker was slain by Jannder!");
        Feed(store, T0.AddSeconds(20), "You gain party experience! (3.000%)", "A giant wasp worker was slain by Jannder!");

        Assert.Equal(new[] { (25, 1), (24, 1) }, store.Rows("Ream", "frostreaver").Select(r => (r.Level, r.Kills)).ToArray());
    }

    [Fact]
    public void Reading_a_log_from_the_start_fills_levels_in_from_level_ups_and_the_table_is_saved()
    {
        string log = Path.Combine(_dir, "eqlog_Ream_frostreaver.txt");
        File.WriteAllLines(log, new[]
        {
            Stamp(0) + "You gain experience! (40.000%)",
            Stamp(0) + "You have slain a gnoll pup!",
            Stamp(30) + "You have gained a level! Welcome to level 5!", // EQ writes the level-up before the gain of the kill that caused it
            Stamp(30) + "You gain experience! (10.000%)",
            Stamp(30) + "A gnoll was slain by Ream!",
            Stamp(60) + "You gain experience! (20.000%)",
            Stamp(60) + "You have slain a gnoll!",
            Stamp(90) + "[6 Healer (Cleric)] Ream (Human) ZONE: qeytoqrg",
            Stamp(120) + "You gain experience! (15.000%)",
            Stamp(120) + "A gnoll was slain by Ream!",
        });

        MobExperienceStore store = Store();
        string saved = Path.Combine(_dir, "mobexperience.json");
        store.SetStoragePath(saved);
        Assert.False(store.IsScanned("Ream", "frostreaver"));
        Assert.True(store.ScanLog(log, "Ream", "frostreaver", currentLevel: 6));

        var expected = new[] { (6, "a gnoll", 1), (5, "a gnoll", 1), (4, "a gnoll pup", 1), (4, "a gnoll", 1) }; // the level-up kill counts at 4
        Assert.Equal(expected, store.Rows("Ream", "frostreaver").Select(r => (r.Level, r.Mob, r.Kills)).ToArray());
        Assert.True(store.IsScanned("Ream", "frostreaver"));

        var reloaded = new MobExperienceStore();
        reloaded.SetStoragePath(saved);
        Assert.Equal(expected, reloaded.Rows("Ream", "frostreaver").Select(r => (r.Level, r.Mob, r.Kills)).ToArray());
        Assert.True(reloaded.IsScanned("Ream", "frostreaver"));
        Assert.Equal(new MobExperienceCharacter("Ream", "frostreaver"), Assert.Single(reloaded.Characters()));

        // Reading it again replaces the rows instead of adding to them.
        Assert.True(reloaded.ScanLog(log, "Ream", "frostreaver", currentLevel: 6));
        Assert.Equal(expected, reloaded.Rows("Ream", "frostreaver").Select(r => (r.Level, r.Mob, r.Kills)).ToArray());

        static string Stamp(int seconds) =>
            new DateTime(2026, 5, 27, 12, 0, 0).AddSeconds(seconds).ToString("[ddd MMM dd HH:mm:ss yyyy] ", CultureInfo.InvariantCulture);
    }
}
