using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Spells;
using Xunit;
using static EQOverwatch.Core.Tests.SpellTestData;

namespace EQOverwatch.Core.Tests;

/// <summary>Real Daybreak strings-file header, songs started by "You begin singing", and cast-on-other text without a name.</summary>
public sealed class Phase8Fix2Tests : IDisposable
{
    private const int EagleEye = 2599;
    private const int Chorus = 2607;

    private static readonly DateTime T0 = new(2026, 10, 26, 19, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));
    private readonly SpellDatabaseService _spells = new();
    private readonly TriggerEngineService _engine = new();
    private readonly BuffTrackerService _tracker;

    public Phase8Fix2Tests()
    {
        string spells = WriteFile(Path.Combine(_dir, "spells_us.txt"),
            SplitRow(EagleEye, "Eagle Eye", 2000, 11, 360),
            SplitRow(Chorus, "Elemental Chorus", 0, 11, 3, bardLevel: 20));
        WriteFile(Path.Combine(_dir, "spells_us_str.txt"),
            "#SPELLINDEX^CASTERMETXT^CASTEROTHERTXT^CASTEDMETXT^CASTEDOTHERTXT^SPELLGONE^",
            "3^^^^^^",
            "2599^^^Your eyes sharpen, with an aura of avian presence.^'s eyes sharpen with an aura of avian presence.^The avian presence departs.^",
            "2607^^^You feel an aura of elemental protection surrounding you.^^The elemental chorus fades.^");
        _spells.Load(spells);
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

    [Fact]
    public void The_strings_file_header_decides_which_columns_hold_the_messages()
    {
        SpellInfo eye = _spells.Database.FindById(EagleEye)!;

        Assert.Equal("Your eyes sharpen, with an aura of avian presence.", eye.CastOnYou);
        Assert.Equal("'s eyes sharpen with an aura of avian presence.", eye.CastOnOther);
        Assert.Equal("The avian presence departs.", eye.SpellFades);
    }

    [Fact]
    public void The_header_reader_returns_null_without_the_expected_names()
    {
        string path = WriteFile(Path.Combine(_dir, "other_str.txt"), "#ID^A^B^C", "1^x^y^z");

        Assert.Null(SpellDatabaseService.ReadStringsHeader(path));
        Assert.Equal(new SpellStringsLayout(3, 4, 5), SpellDatabaseService.ReadStringsHeader(Path.Combine(_dir, "spells_us_str.txt")));
    }

    [Fact]
    public void A_buff_lands_on_you_and_on_others_whether_or_not_there_is_a_space_before_the_possessive()
    {
        _tracker.SetTrackedSpells(new[] { EagleEye });

        _tracker.OnLine("Oodlesii", "You begin casting Eagle Eye.", T0);
        _tracker.OnLine("Oodlesii", "Your eyes sharpen, with an aura of avian presence.", T0.AddSeconds(2));
        _tracker.OnLine("Oodlesii", "Soandso's eyes sharpen with an aura of avian presence.", T0.AddSeconds(3));
        _tracker.OnLine("Oodlesii", "Another 's eyes sharpen with an aura of avian presence.", T0.AddSeconds(4));

        Assert.Equal(
            new[] { "Another: Eagle Eye", "Oodlesii: Eagle Eye", "Soandso: Eagle Eye" },
            Timers.Select(t => t.Text).OrderBy(t => t).ToArray());
    }

    [Fact]
    public void The_fade_text_from_the_strings_file_cancels_the_buff()
    {
        _tracker.SetTrackedSpells(new[] { EagleEye });
        _tracker.OnLine("Oodlesii", "Your eyes sharpen, with an aura of avian presence.", T0);
        Assert.Single(Timers);

        _tracker.OnLine("Oodlesii", "The avian presence departs.", T0.AddSeconds(30));

        Assert.Empty(Timers);
    }

    [Fact]
    public void Singing_a_tracked_song_starts_its_bar_without_any_landing_text()
    {
        _tracker.SetTrackedSpellOverlays(new[] { new KeyValuePair<int, TimerOverlayCategory>(Chorus, TimerOverlayCategory.Songs) });

        _tracker.OnLine("Oodlesii", "You begin singing Elemental Chorus.", T0);

        ActiveTimerSnapshot timer = Assert.Single(Timers);
        Assert.Equal("Oodlesii: Elemental Chorus", timer.Text);
        Assert.Equal(TimerOverlayCategory.Songs, timer.Category);
        Assert.Equal(18, timer.DurationSeconds);
        Assert.Equal(T0, timer.StartedUtc);
        Assert.Equal(1, _tracker.TimersStarted);
    }

    [Fact]
    public void Singing_a_song_again_restarts_the_same_bar()
    {
        _tracker.SetTrackedSpells(new[] { Chorus });

        _tracker.OnLine("Oodlesii", "You begin singing Elemental Chorus.", T0);
        _tracker.OnLine("Oodlesii", "You begin singing Elemental Chorus.", T0.AddSeconds(12));

        ActiveTimerSnapshot timer = Assert.Single(Timers);
        Assert.Equal(T0.AddSeconds(12), timer.StartedUtc);
    }

    [Fact]
    public void The_chosen_overlay_applies_to_a_song_that_starts_on_the_sing_line()
    {
        _tracker.SetTrackedSpellOverlays(new[] { new KeyValuePair<int, TimerOverlayCategory>(Chorus, TimerOverlayCategory.Buffs) });

        _tracker.OnLine("Oodlesii", "You begin singing Elemental Chorus.", T0);

        Assert.Equal(TimerOverlayCategory.Buffs, Assert.Single(Timers).Category);
    }

    [Fact]
    public void An_untracked_song_and_another_bards_song_do_not_start_a_bar()
    {
        _tracker.SetTrackedSpells(new[] { EagleEye });
        _tracker.OnLine("Oodlesii", "You begin singing Elemental Chorus.", T0);
        Assert.Empty(Timers);

        _tracker.SetTrackedSpells(new[] { Chorus });
        _tracker.OnLine("Oodlesii", "Ream begins to sing a song. <Elemental Chorus>", T0);
        Assert.Empty(Timers);
    }

    [Fact]
    public void A_cast_of_a_buff_alone_does_not_start_a_bar()
    {
        _tracker.SetTrackedSpells(new[] { EagleEye });

        _tracker.OnLine("Oodlesii", "You begin casting Eagle Eye.", T0);

        Assert.Empty(Timers);
        Assert.Equal(1, _tracker.PendingCastCount);
    }

    [Fact]
    public void Song_landing_text_in_any_log_starts_nothing_so_only_the_singers_log_shows_the_bar()
    {
        _tracker.SetTrackedSpellOverlays(new[] { new KeyValuePair<int, TimerOverlayCategory>(Chorus, TimerOverlayCategory.Songs) });

        // A group member's log: the song lands on them.
        _tracker.OnLine("Groupmate", "You feel an aura of elemental protection surrounding you.", T0.AddSeconds(1));
        Assert.Empty(Timers);

        // The singer's log: sing line, then the same landing text.
        _tracker.OnLine("Oodlesii", "You begin singing Elemental Chorus.", T0);
        _tracker.OnLine("Oodlesii", "You feel an aura of elemental protection surrounding you.", T0.AddSeconds(1));

        Assert.Equal("Oodlesii: Elemental Chorus", Assert.Single(Timers).Text);
    }

    [Fact]
    public void A_spell_the_user_put_in_the_songs_overlay_ignores_landing_text_even_if_the_file_does_not_call_it_a_song()
    {
        _tracker.SetTrackedSpellOverlays(new[] { new KeyValuePair<int, TimerOverlayCategory>(EagleEye, TimerOverlayCategory.Songs) });

        _tracker.OnLine("Groupmate", "Your eyes sharpen, with an aura of avian presence.", T0);
        _tracker.OnLine("Groupmate", "Soandso's eyes sharpen with an aura of avian presence.", T0);

        Assert.Empty(Timers);
    }

    [Fact]
    public void A_cleric_buff_with_an_effect_value_in_the_old_bard_column_is_a_buff_and_a_real_bard_song_is_a_song()
    {
        // Shaped like the real Daybreak rows: class levels in columns 36-51 (Cleric 37, Bard 43), good/bad flag in column 28,
        // and an unrelated effect value (1) in column 110 that an earlier build mistook for a Bard level.
        static string Row(int id, string name, int good, int clericLevel, int bardLevel)
        {
            string[] c = Enumerable.Repeat("0", 166).ToArray();
            c[0] = id.ToString();
            c[1] = name;
            c[8] = "3000";
            c[11] = "11";
            c[12] = "100";
            c[28] = good.ToString();
            for (int i = 36; i <= 51; i++)
            {
                c[i] = "255";
            }

            c[37] = clericLevel.ToString();
            c[43] = bardLevel.ToString();
            c[110] = "1";
            return string.Join('^', c);
        }

        string dir = Path.Combine(_dir, "real");
        string path = WriteFile(Path.Combine(dir, "spells_us.txt"),
            Row(1, "Aegolism-like", good: 1, clericLevel: 60, bardLevel: 255),
            Row(2, "Chorus-like", good: 1, clericLevel: 255, bardLevel: 54),
            Row(3, "Poison-like", good: 0, clericLevel: 255, bardLevel: 255));
        var service = new SpellDatabaseService();

        service.Load(path);

        Assert.Equal(TimerOverlayCategory.Buffs, service.Database.FindById(1)!.Category);
        Assert.Equal(TimerOverlayCategory.Songs, service.Database.FindById(2)!.Category);
        Assert.Equal(TimerOverlayCategory.Debuffs, service.Database.FindById(3)!.Category);
    }
}
