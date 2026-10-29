using EQOverwatch.Core.Models;
using EQOverwatch.Core.Spells;
using Xunit;
using static EQOverwatch.Core.Tests.SpellTestData;

namespace EQOverwatch.Core.Tests;

public sealed class SpellCategoryTests : IDisposable
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

    [Theory]
    [InlineData(null, null, TimerOverlayCategory.Buffs)]       // row too short to say: a buff
    [InlineData(255, 1, TimerOverlayCategory.Buffs)]
    [InlineData(255, 0, TimerOverlayCategory.Debuffs)]
    [InlineData(0, 1, TimerOverlayCategory.Buffs)]             // level 0 is not a bard level
    [InlineData(1, 1, TimerOverlayCategory.Songs)]
    [InlineData(125, 0, TimerOverlayCategory.Songs)]           // a bard song beats GoodEffect
    [InlineData(254, 1, TimerOverlayCategory.Songs)]
    [InlineData(null, 0, TimerOverlayCategory.Debuffs)]
    [InlineData(null, 2, TimerOverlayCategory.Buffs)]          // any non-zero GoodEffect is beneficial
    public void Spells_are_categorized_from_bard_level_and_good_effect(int? bardLevel, int? goodEffect, TimerOverlayCategory expected) =>
        Assert.Equal(expected, SpellDatabaseService.Categorize(bardLevel, goodEffect));

    [Fact]
    public void The_unified_layout_reads_good_effect_from_column_83_and_bard_level_from_column_115()
    {
        WriteFile(SpellsPath,
            LegacyRow(1, "Haste", "You feel quick.", " looks quick.", "Your quickness fades.", 1000, 11, 100, goodEffect: 1),
            LegacyRow(2, "Disease Cloud", "You feel sick.", " looks sick.", "Your sickness fades.", 1000, 11, 100, goodEffect: 0),
            LegacyRow(3, "Selo's Accelerando", "You pick up speed.", " moves fast.", "Your speed fades.", 0, 11, 3, goodEffect: 1, bardLevel: 5),
            LegacyRow(4, "Bard Debuff Song", "You feel slowed.", " is slowed.", "Your slow fades.", 0, 11, 3, goodEffect: 0, bardLevel: 20),
            ShortLegacyRow(5, "Old Row", "You feel old.", " looks old.", "Your age fades.", 1000, 11, 100));
        var service = new SpellDatabaseService();

        service.Load(SpellsPath);

        SpellDatabase db = service.Database;
        Assert.Equal(TimerOverlayCategory.Buffs, db.FindById(1)!.Category);
        Assert.Equal(TimerOverlayCategory.Debuffs, db.FindById(2)!.Category);
        Assert.Equal(TimerOverlayCategory.Songs, db.FindById(3)!.Category);
        Assert.Equal(TimerOverlayCategory.Songs, db.FindById(4)!.Category);
        Assert.Equal(TimerOverlayCategory.Buffs, db.FindById(5)!.Category); // fewer columns than the category fields need
    }

    [Fact]
    public void The_split_layout_reads_good_effect_from_column_28_and_bard_level_from_column_43()
    {
        WriteFile(SpellsPath,
            SplitRow(10, "Haste", 1000, 11, 100, goodEffect: 1),
            SplitRow(11, "Disease Cloud", 1000, 11, 100, goodEffect: 0),
            SplitRow(12, "Selo's Accelerando", 0, 11, 3, goodEffect: 1, bardLevel: 5));
        WriteFile(StringsPath,
            "10^You feel quick.^ looks quick.^fades",
            "11^You feel sick.^ looks sick.^fades",
            "12^You pick up speed.^ moves fast.^fades");
        var service = new SpellDatabaseService();

        service.Load(SpellsPath);

        SpellDatabase db = service.Database;
        Assert.Equal(TimerOverlayCategory.Buffs, db.FindById(10)!.Category);
        Assert.Equal(TimerOverlayCategory.Debuffs, db.FindById(11)!.Category);
        Assert.Equal(TimerOverlayCategory.Songs, db.FindById(12)!.Category);
    }

    [Fact]
    public void Display_names_match_the_overlay_titles()
    {
        Assert.Equal(
            new[] { "Buffs", "Debuffs", "Songs", "Raid Triggers", "CH Chain" },
            TimerOverlayCategoryExtensions.All.Select(c => c.DisplayName()));
    }

    // ───────────────────────── all spell definitions (raid boss picker) ─────────────────────────

    [Fact]
    public void Every_named_spell_stays_available_to_the_boss_picker_with_or_without_a_landing_message()
    {
        WriteFile(SpellsPath,
            LegacyRow(1, "Spirit of Wolf", "You feel quick.", " looks quick.", "Your speed fades.", 1000, 11, 100),
            LegacyRow(2, "Dragon Roar", "You are blasted by a roar!", " is blasted by a roar.", "", 0, 0, 0),
            LegacyRow(3, "Knockback", "", " is knocked back.", "", 0, 0, 0),                        // only a cast-on-other message
            LegacyRow(4, "Boss Aura", "You are scorched.", "", "", 0, 50, 72000),                  // permanent (formula 50)
            LegacyRow(5, "Silent Spell", "", "", "", 0, 0, 0),                                    // no landing text at all
            LegacyRow(6, "Whitespace Only", "", "   ", "", 0, 0, 0),                              // blank cast-on-other counts as none
            "7^Broken^too^few");
        var service = new SpellDatabaseService();

        SpellLoadReport report = service.Load(SpellsPath);

        SpellDatabase db = service.Database;
        Assert.Equal(new[] { 1 }, db.Spells.Select(s => s.SpellId));                     // duration spells only
        Assert.Equal(
            new[] { "Boss Aura", "Dragon Roar", "Knockback", "Silent Spell", "Spirit of Wolf", "Whitespace Only" },
            db.AllSpellDefinitions.Select(d => d.Name));
        Assert.Equal(6, report.DefinitionCount);

        SpellDefinition roar = db.FindDefinitionById(2)!;
        Assert.Equal("You are blasted by a roar!", roar.CastOnYou);
        Assert.Equal(" is blasted by a roar.", roar.CastOnOther);
        Assert.Equal("Dragon Roar  (#2)", roar.Label);
        Assert.NotNull(db.FindDefinitionById(5)); // no messages at all: still pickable, a rule matches its casting line
        Assert.Null(db.FindDefinitionById(7));    // a malformed row is skipped
    }

    [Fact]
    public void In_the_split_layout_definitions_get_their_messages_from_the_strings_file()
    {
        WriteFile(SpellsPath,
            SplitRow(20, "Selo's Accelerando", 0, 11, 3, bardLevel: 5),
            SplitRow(21, "Dragon Roar", 0, 0, 0),
            SplitRow(22, "Quiet Spell", 0, 0, 0));
        WriteFile(StringsPath,
            "# header",
            "20^You pick up speed.^ moves fast.^fades",
            "21^You are blasted!^ is blasted.^",
            "22^^^");
        var service = new SpellDatabaseService();

        SpellLoadReport report = service.Load(SpellsPath);

        SpellDatabase db = service.Database;
        Assert.Equal(new[] { 20 }, db.Spells.Select(s => s.SpellId));
        Assert.Equal(new[] { "Dragon Roar", "Quiet Spell", "Selo's Accelerando" }, db.AllSpellDefinitions.Select(d => d.Name));
        Assert.Equal("You are blasted!", db.FindDefinitionById(21)!.CastOnYou);
        Assert.Equal(string.Empty, db.FindDefinitionById(22)!.CastOnYou); // no message in the strings file, still listed
        Assert.Equal(3, report.DefinitionCount);
    }

    [Fact]
    public void Without_a_strings_file_the_split_layout_lists_spells_without_messages()
    {
        WriteFile(SpellsPath, SplitRow(30, "Dragon Roar", 0, 0, 0));
        var service = new SpellDatabaseService();

        SpellLoadReport report = service.Load(SpellsPath);

        SpellDefinition only = Assert.Single(service.Database.AllSpellDefinitions);
        Assert.Equal(string.Empty, only.CastOnYou);
        Assert.NotNull(report.Warning);
    }
}
