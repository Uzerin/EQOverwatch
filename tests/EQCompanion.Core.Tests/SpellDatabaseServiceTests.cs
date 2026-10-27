using EQCompanion.Core.Spells;
using Xunit;
using static EQCompanion.Core.Tests.SpellTestData;

namespace EQCompanion.Core.Tests;

public sealed class SpellDatabaseServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    private string SpellsPath => Path.Combine(_dir, "spells_us.txt");
    private string StringsPath => Path.Combine(_dir, "spells_us_str.txt");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ───────────────────────── duration formulas ─────────────────────────

    [Theory]
    [InlineData(0, 60, 100, 0)]
    [InlineData(1, 60, 100, 30)]   // ceil(level / 2)
    [InlineData(1, 59, 100, 30)]   // ceil(29.5)
    [InlineData(1, 60, 10, 10)]    // capped by max ticks
    [InlineData(2, 60, 100, 35)]   // ceil(level / 2) + 5
    [InlineData(2, 3, 100, 1)]     // level <= 3
    [InlineData(2, 60, 20, 20)]
    [InlineData(3, 60, 2000, 1800)] // level * 30
    [InlineData(3, 60, 1000, 1000)]
    [InlineData(4, 60, 0, 50)]     // 0 max ticks means 50
    [InlineData(4, 60, 90, 90)]
    [InlineData(5, 60, 0, 2)]
    [InlineData(5, 60, 5, 2)]
    [InlineData(5, 60, 1, 1)]
    [InlineData(6, 60, 100, 32)]   // level / 2 + 2
    [InlineData(6, 61, 100, 32)]   // integer division
    [InlineData(7, 60, 0, 60)]     // no cap: level
    [InlineData(7, 60, 40, 40)]
    [InlineData(7, 60, 100, 60)]
    [InlineData(8, 60, 100, 70)]   // level + 10
    [InlineData(8, 60, 50, 50)]
    [InlineData(9, 60, 200, 130)]  // level * 2 + 10
    [InlineData(10, 60, 500, 190)] // level * 3 + 10
    [InlineData(11, 60, 150, 150)]
    [InlineData(12, 60, 150, 150)]
    [InlineData(13, 60, 77, 77)]   // unknown formulas use max ticks
    [InlineData(1, 60, 0, 0)]      // a zero cap leaves nothing
    public void Base_duration_formulas(int formula, int level, int maxTicks, int expectedTicks) =>
        Assert.Equal(expectedTicks, SpellDatabaseService.ComputeBaseTicks(formula, level, maxTicks));

    // ───────────────────────── legacy / unified layout ─────────────────────────

    private string WriteLegacy() => WriteFile(SpellsPath,
        "# a comment line that must be ignored",
        LegacyRow(100, "Spirit of Wolf", "You feel quick as a wolf.", " looks faster.", "Your speed returns to normal.", 1500, 11, 150),
        LegacyRow(101, "Instant Heal", "You feel better.", " feels better.", "", 2000, 0, 0),
        LegacyRow(102, "Aura of Pain", "You feel pain.", " feels pain.", "", 0, 50, 72000),
        LegacyRow(103, "Short Buff", "You feel brief power.", " glows briefly.", "Your power fades.", 3000, 1, 100),
        "104^Broken Row^only^a^few^columns");

    [Fact]
    public void Legacy_file_keeps_only_spells_with_a_real_duration()
    {
        var service = new SpellDatabaseService();

        SpellLoadReport report = service.Load(WriteLegacy());

        Assert.Equal(SpellFileFormat.Legacy, report.Format);
        Assert.Null(report.StringsFilePath);
        Assert.Null(report.Warning);
        Assert.Equal(5, report.RowsRead);              // the # line is not a row
        Assert.Equal(2, report.RowsKept);
        Assert.Equal(2, report.RowsSkippedNoDuration); // instant heal and formula 50
        Assert.Equal(1, report.RowsMalformed);

        SpellDatabase db = service.Database;
        Assert.Equal(new[] { "Short Buff", "Spirit of Wolf" }, db.Spells.Select(s => s.Name));
        Assert.Null(db.FindById(101));
        Assert.Null(db.FindById(102));

        SpellInfo wolf = db.FindById(100)!;
        Assert.Equal("Spirit of Wolf", wolf.Name);
        Assert.Equal("You feel quick as a wolf.", wolf.CastOnYou);
        Assert.Equal(" looks faster.", wolf.CastOnOther);
        Assert.Equal("looks faster.", wolf.CastOnOtherSuffix);
        Assert.Equal("Your speed returns to normal.", wolf.SpellFades);
        Assert.Equal(1500, wolf.CastTimeMs);
        Assert.Equal(11, wolf.DurationFormula);
        Assert.Equal(150, wolf.MaxDurationTicks);
        Assert.Equal(900, wolf.BaseDurationSeconds); // 150 ticks x 6s
    }

    [Fact]
    public void Every_lookup_index_works()
    {
        var service = new SpellDatabaseService();
        service.Load(WriteLegacy());
        SpellDatabase db = service.Database;

        Assert.Equal(100, db.FindByName("spirit of WOLF")!.SpellId); // case-insensitive
        Assert.Null(db.FindByName("nonexistent"));
        Assert.Equal(100, Assert.Single(db.FindByCastOnYou("You feel quick as a wolf.")).SpellId);
        Assert.Equal(100, Assert.Single(db.FindByCastOnOther(" looks faster.")).SpellId); // leading space is ignored
        Assert.Equal(100, Assert.Single(db.FindByCastOnOther("looks faster.")).SpellId);
        Assert.Equal(100, Assert.Single(db.FindBySpellFades("Your speed returns to normal.")).SpellId);
        Assert.Empty(db.FindByCastOnYou("You feel quick as a WOLF.")); // landing text is matched exactly
        Assert.Empty(db.FindByCastOnYou("You feel better."));          // belongs to a discarded instant spell
        Assert.Empty(db.FindBySpellFades(string.Empty));
    }

    [Fact]
    public void Caster_level_changes_the_calculated_durations()
    {
        var service = new SpellDatabaseService();
        string path = WriteLegacy();

        service.Load(path); // default level 60
        Assert.Equal(SpellDatabaseService.DefaultCasterLevel, service.Database.CasterLevel);
        Assert.Equal(180, service.Database.FindById(103)!.BaseDurationSeconds); // ceil(60/2) = 30 ticks

        service.Load(path, casterLevel: 20);
        Assert.Equal(20, service.Database.CasterLevel);
        Assert.Equal(60, service.Database.FindById(103)!.BaseDurationSeconds);  // 10 ticks
        Assert.Equal(900, service.Database.FindById(100)!.BaseDurationSeconds); // formula 11 ignores level
    }

    [Fact]
    public void Shared_text_and_shared_names_resolve_to_the_longest_duration_first()
    {
        var service = new SpellDatabaseService();
        string path = WriteFile(SpellsPath,
            LegacyRow(1, "Pack Spirit", "You feel quick.", " looks quick.", "Your quickness fades.", 2000, 11, 100),
            LegacyRow(2, "Spirit of Wolf", "You feel quick.", " looks quick.", "Your quickness fades.", 1500, 11, 150),
            LegacyRow(3, "Spirit of Wolf", "You feel quick.", " looks quick.", "Your quickness fades.", 1500, 11, 200)); // same name, longer

        service.Load(path);
        SpellDatabase db = service.Database;

        Assert.Equal(new[] { 3, 2, 1 }, db.FindByCastOnYou("You feel quick.").Select(s => s.SpellId));
        Assert.Equal(new[] { 3, 2, 1 }, db.FindByCastOnOther("looks quick.").Select(s => s.SpellId));
        Assert.Equal(new[] { 3, 2, 1 }, db.FindBySpellFades("Your quickness fades.").Select(s => s.SpellId));
        Assert.Equal(3, db.FindByName("Spirit of Wolf")!.SpellId);
    }

    // ───────────────────────── modern split layout ─────────────────────────

    [Fact]
    public void Split_layout_reads_messages_from_the_companion_strings_file()
    {
        WriteFile(SpellsPath,
            "# header",
            SplitRow(200, "Selo's Accelerando", 0, 11, 30),
            SplitRow(201, "Instant Heal", 2000, 0, 0),
            SplitRow(202, "Aura of Pain", 0, 50, 72000),
            SplitRow(203, "Short Buff", 3000, 1, 100),
            SplitRow(204, "No Strings Entry", 1000, 11, 10));
        WriteFile(StringsPath,
            "# id^cast on you^cast on other^fades",
            "",
            "200^You feel your pace quicken.^ moves faster.^pace returns to normal.^unused",
            "201^You feel better.^ feels better.^^",
            "999^Stranger^ is not in the spell file.^gone^",       // ids that were not kept are ignored
            "203^You feel brief power.^ glows briefly.^Your power fades.^");
        var service = new SpellDatabaseService();

        SpellLoadReport report = service.Load(SpellsPath);

        Assert.Equal(SpellFileFormat.Split, report.Format);
        Assert.Equal(StringsPath, report.StringsFilePath);
        Assert.Null(report.Warning);
        Assert.Equal(5, report.RowsRead);
        Assert.Equal(3, report.RowsKept);
        Assert.Equal(2, report.RowsSkippedNoDuration);

        SpellDatabase db = service.Database;
        SpellInfo song = db.FindById(200)!;
        Assert.Equal("You feel your pace quicken.", song.CastOnYou);
        Assert.Equal(" moves faster.", song.CastOnOther);
        Assert.Equal("pace returns to normal.", song.SpellFades);
        Assert.Equal(0, song.CastTimeMs);
        Assert.Equal(180, song.BaseDurationSeconds);

        Assert.Equal(180, db.FindById(203)!.BaseDurationSeconds); // formula 1 at level 60
        Assert.Equal(3000, db.FindById(203)!.CastTimeMs);

        SpellInfo noStrings = db.FindById(204)!;               // present in spells_us.txt, missing from the strings file
        Assert.Equal(string.Empty, noStrings.CastOnYou);
        Assert.Equal(60, noStrings.BaseDurationSeconds);

        Assert.Equal(200, Assert.Single(db.FindByCastOnYou("You feel your pace quicken.")).SpellId);
        Assert.Equal(200, Assert.Single(db.FindByCastOnOther("moves faster.")).SpellId);
        Assert.Equal(200, Assert.Single(db.FindBySpellFades("pace returns to normal.")).SpellId);
    }

    [Fact]
    public void Split_layout_is_detected_from_an_integer_in_column_six_when_the_strings_file_is_missing()
    {
        WriteFile(SpellsPath,
            SplitRow(300, "Some Song", 0, 11, 40),
            SplitRow(301, "Instant", 0, 0, 0));
        var service = new SpellDatabaseService();

        SpellLoadReport report = service.Load(SpellsPath);

        Assert.Equal(SpellFileFormat.Split, report.Format);
        Assert.Null(report.StringsFilePath);
        Assert.Contains("spells_us_str.txt", report.Warning);
        SpellInfo spell = Assert.Single(service.Database.Spells);
        Assert.Equal("Some Song", spell.Name);
        Assert.Equal(240, spell.BaseDurationSeconds);
        Assert.Equal(string.Empty, spell.SpellFades);
    }

    [Fact]
    public void A_legacy_row_with_an_empty_cast_on_you_message_is_still_legacy()
    {
        WriteFile(SpellsPath, LegacyRow(1, "Quiet Buff", "", " is buffed.", "", 0, 11, 10));
        var service = new SpellDatabaseService();

        SpellLoadReport report = service.Load(SpellsPath);

        Assert.Equal(SpellFileFormat.Legacy, report.Format);
        Assert.Equal(" is buffed.", Assert.Single(service.Database.Spells).CastOnOther);
    }

    [Fact]
    public void A_companion_strings_file_forces_the_split_layout()
    {
        WriteFile(SpellsPath, SplitRow(400, "Song", 0, 11, 5));
        WriteFile(StringsPath, "400^You hum.^ hums.^You stop humming.");
        var service = new SpellDatabaseService();

        Assert.Equal(SpellFileFormat.Split, service.Load(SpellsPath).Format);
        Assert.Equal("You hum.", service.Database.FindById(400)!.CastOnYou);
    }

    [Fact]
    public void The_strings_file_columns_can_be_remapped()
    {
        WriteFile(SpellsPath, SplitRow(500, "Song", 0, 11, 5));
        WriteFile(StringsPath, "500^ignored^You hum.^ hums.^You stop humming.");
        var service = new SpellDatabaseService(new SpellStringsLayout(CastOnYou: 2, CastOnOther: 3, SpellFades: 4));

        service.Load(SpellsPath);

        SpellInfo spell = service.Database.FindById(500)!;
        Assert.Equal("You hum.", spell.CastOnYou);
        Assert.Equal("You stop humming.", spell.SpellFades);
    }

    // ───────────────────────── service behaviour ─────────────────────────

    [Fact]
    public void Loading_and_clearing_raise_DatabaseChanged_and_replace_the_database()
    {
        var service = new SpellDatabaseService();
        int changes = 0;
        service.DatabaseChanged += () => changes++;
        Assert.Equal(0, service.Database.Count);

        service.Load(WriteLegacy());
        Assert.Equal(1, changes);
        Assert.Equal(2, service.Database.Count);

        service.Clear();
        Assert.Equal(2, changes);
        Assert.Equal(0, service.Database.Count);
    }

    [Fact]
    public void A_failed_load_keeps_the_previous_database()
    {
        var service = new SpellDatabaseService();
        service.Load(WriteLegacy());

        Assert.Throws<FileNotFoundException>(() => service.Load(Path.Combine(_dir, "missing.txt")));

        Assert.Equal(2, service.Database.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public void Caster_level_must_be_within_range(int level)
    {
        var service = new SpellDatabaseService();
        string path = WriteLegacy();

        Assert.Throws<ArgumentOutOfRangeException>(() => service.Load(path, level));
    }

    [Fact]
    public void Extreme_levels_are_accepted()
    {
        var service = new SpellDatabaseService();
        string path = WriteLegacy();

        service.Load(path, SpellDatabaseService.MinCasterLevel);
        Assert.Equal(6, service.Database.FindById(103)!.BaseDurationSeconds); // ceil(1/2) = 1 tick

        service.Load(path, SpellDatabaseService.MaxCasterLevel);
        Assert.Equal(63 * 6, service.Database.FindById(103)!.BaseDurationSeconds); // ceil(125/2) = 63 ticks
    }
}
