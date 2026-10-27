using EQCompanion.Core.Models;
using EQCompanion.Core.Services;
using EQCompanion.Core.Spells;
using Xunit;
using static EQCompanion.Core.Tests.SpellTestData;

namespace EQCompanion.Core.Tests;

public sealed class BuffTrackerServiceTests : IDisposable
{
    private const int Wolf = 100;
    private const int Selo = 101;
    private const int PackSpirit = 102;
    private const int Skin = 103;
    private const int Untracked = 104;

    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));
    private readonly SpellDatabaseService _spells = new();
    private readonly TriggerEngineService _engine = new();
    private readonly BuffTrackerService _tracker;

    public BuffTrackerServiceTests()
    {
        _spells.Load(WriteSpellFile(Path.Combine(_dir, "spells_us.txt")));
        _tracker = new BuffTrackerService(_spells, _engine);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Wolf and Pack Spirit deliberately share their landing and fade text; Selo is a 3-minute bard song.</summary>
    internal static string WriteSpellFile(string path) => SpellTestData.WriteFile(path,
        LegacyRow(Wolf, "Spirit of Wolf", "You feel quick as a wolf.", " looks faster.", "Your speed returns to normal.", 1500, 11, 150),
        LegacyRow(Selo, "Selo's Accelerando", "You feel your pace quicken.", " moves faster.", "pace returns to normal.", 0, 11, 30),
        LegacyRow(PackSpirit, "Pack Spirit", "You feel quick as a wolf.", " looks faster.", "Your speed returns to normal.", 2000, 11, 100),
        LegacyRow(Skin, "Skin like Nature", "Your skin turns to bark.", "'s skin turns to bark.", "Your skin returns to normal.", 3000, 11, 50),
        LegacyRow(Untracked, "Untracked Buff", "You feel untracked.", " feels untracked.", "Your untracked fades.", 1000, 11, 10));

    private void Track(params int[] ids) => _tracker.SetTrackedSpells(ids);

    private void Line(string character, string message, DateTime at) => _tracker.OnLine(character, message, at);

    private IReadOnlyList<ActiveTimerSnapshot> Timers => _engine.Snapshot.Timers;

    // ───────────────────────── cast → land ─────────────────────────

    [Fact]
    public void A_pending_cast_decides_between_spells_that_share_landing_text()
    {
        Track(Wolf, PackSpirit);

        Line("Stackprime", "You begin casting Pack Spirit.", T0);
        Assert.Equal(1, _tracker.PendingCastCount);
        Line("Stackprime", "You feel quick as a wolf.", T0.AddSeconds(2));

        ActiveTimerSnapshot timer = Assert.Single(Timers);
        Assert.Equal("Stackprime: Pack Spirit", timer.Text);
        Assert.Equal("Stackprime", timer.CharacterName);
        Assert.Equal(TimerKind.Countdown, timer.Kind);
        Assert.Equal(600, timer.DurationSeconds);
        Assert.Equal(T0.AddSeconds(2), timer.StartedUtc);
        Assert.Equal(T0.AddSeconds(602), timer.ExpiresUtc);
        Assert.Equal(BuffTrackerService.TimerSourceId, timer.TriggerId);
        Assert.Equal(0, _tracker.PendingCastCount); // consumed
    }

    [Fact]
    public void Without_a_pending_cast_the_longest_lasting_tracked_spell_wins()
    {
        Track(Wolf, PackSpirit);

        Line("Stackprime", "You feel quick as a wolf.", T0);

        ActiveTimerSnapshot timer = Assert.Single(Timers);
        Assert.Equal("Stackprime: Spirit of Wolf", timer.Text);
        Assert.Equal(900, timer.DurationSeconds);
    }

    [Fact]
    public void A_pending_cast_is_valid_for_cast_time_plus_two_and_a_half_seconds()
    {
        Track(Wolf, PackSpirit); // Pack Spirit casts in 2.0s, so its window is 4.5s

        Line("Stackprime", "You begin casting Pack Spirit.", T0);
        Line("Stackprime", "You feel quick as a wolf.", T0.AddMilliseconds(4500)); // still inside
        Assert.Equal("Stackprime: Pack Spirit", Assert.Single(Timers).Text);

        _engine.ClearTimers();
        Line("Stackprime", "You begin casting Pack Spirit.", T0);
        Line("Stackprime", "You feel quick as a wolf.", T0.AddMilliseconds(4501)); // too late: falls back to the longest
        Assert.Equal("Stackprime: Spirit of Wolf", Assert.Single(Timers).Text);
        Assert.Equal(0, _tracker.PendingCastCount);
    }

    [Fact]
    public void Pending_casts_belong_to_the_log_that_saw_them()
    {
        Track(Wolf, PackSpirit);

        Line("Oodles", "You begin casting Pack Spirit.", T0);
        Line("Stackprime", "You feel quick as a wolf.", T0.AddSeconds(1)); // a different log

        Assert.Equal("Stackprime: Spirit of Wolf", Assert.Single(Timers).Text);
        Assert.Equal(1, _tracker.PendingCastCount); // Oodles' cast is still waiting
    }

    [Fact]
    public void Only_whitelisted_spells_are_tracked_and_spell_names_match_case_insensitively()
    {
        Track(Wolf);

        Line("Stackprime", "You begin casting Untracked Buff.", T0);
        Assert.Equal(0, _tracker.PendingCastCount);
        Line("Stackprime", "You feel untracked.", T0.AddSeconds(1));
        Assert.Empty(Timers);

        Line("Stackprime", "You begin casting spirit of WOLF.", T0);
        Assert.Equal(1, _tracker.PendingCastCount);
    }

    [Fact]
    public void Another_casters_spell_landing_on_someone_else_uses_the_cast_on_other_text()
    {
        Track(Wolf, Skin);

        Line("Stackprime", "Ream begins to cast a spell. <Spirit of Wolf>", T0);
        Assert.Equal(1, _tracker.PendingCastCount);
        Line("Stackprime", "Oodles looks faster.", T0.AddSeconds(2));

        ActiveTimerSnapshot wolf = Assert.Single(Timers);
        Assert.Equal("Oodles: Spirit of Wolf", wolf.Text);
        Assert.Equal("Oodles", wolf.CharacterName);
        Assert.Equal(900, wolf.DurationSeconds);

        Line("Stackprime", "Ream's skin turns to bark.", T0.AddSeconds(3)); // possessive suffix
        Assert.Contains(Timers, t => t.Text == "Ream: Skin like Nature" && t.DurationSeconds == 300);
    }

    [Fact]
    public void Buffs_from_outside_the_group_still_start_timers_without_a_cast_message()
    {
        Track(Wolf, Skin);

        Line("Oodles", "Stackprime looks faster.", T0);        // someone else's buff lands on a watched character
        Line("Stackprime", "Your skin turns to bark.", T0);    // cast-on-you with no "begins to cast" seen

        Assert.Equal(
            new[] { "Stackprime: Skin like Nature", "Stackprime: Spirit of Wolf" },
            Timers.Select(t => t.Text).OrderBy(t => t));
    }

    [Fact]
    public void The_same_landing_seen_by_two_watched_logs_shares_one_bar()
    {
        Track(Wolf);

        Line("Stackprime", "Ream looks faster.", T0);
        Line("Oodles", "Ream looks faster.", T0.AddMilliseconds(40));

        Assert.Equal("Ream: Spirit of Wolf", Assert.Single(Timers).Text);
    }

    [Fact]
    public void A_line_that_only_resembles_a_landing_is_ignored()
    {
        Track(Wolf);

        Line("Stackprime", "You feel quick as a wolf", T0);          // no period
        Line("Stackprime", "looks faster.", T0);                      // nobody is named
        Line("Stackprime", "Ream looks faster", T0);                  // no period
        Line("Stackprime", "You say, 'Ream looks faster.'", T0);      // not at the end of the line
        Line("Stackprime", "You begin casting Spirit of Wolf", T0);   // a cast line must end with a period

        Assert.Empty(Timers);
        Assert.Equal(0, _tracker.PendingCastCount);
    }

    // ───────────────────────── bard songs ─────────────────────────

    [Fact]
    public void Resinging_a_bard_song_refreshes_the_existing_bar()
    {
        Track(Selo);

        Line("Stackprime", "You begin singing Selo's Accelerando.", T0);
        Line("Stackprime", "You feel your pace quicken.", T0);
        ActiveTimerSnapshot first = Assert.Single(Timers);
        Assert.Equal("Stackprime: Selo's Accelerando", first.Text);
        Assert.Equal(T0.AddSeconds(180), first.ExpiresUtc);

        Line("Stackprime", "You begin singing Selo's Accelerando.", T0.AddSeconds(6));
        Line("Stackprime", "You feel your pace quicken.", T0.AddSeconds(6));

        ActiveTimerSnapshot refreshed = Assert.Single(Timers); // still one bar
        Assert.NotEqual(first.Id, refreshed.Id);
        Assert.Equal(T0.AddSeconds(6), refreshed.StartedUtc);
        Assert.Equal(T0.AddSeconds(186), refreshed.ExpiresUtc);
        Assert.Equal(1.0, refreshed.FractionRemaining(T0.AddSeconds(6)), 3);
    }

    [Fact]
    public void Songs_sung_by_others_never_start_bars_from_landing_text()
    {
        Track(Selo);

        Line("Stackprime", "Ream begins to sing a song. <Selo's Accelerando>", T0);
        Assert.Equal(1, _tracker.PendingCastCount);
        Line("Stackprime", "Oodles moves faster.", T0.AddSeconds(1));

        Assert.Empty(Timers);
        Assert.Equal(0, _tracker.PendingCastCount);
    }

    // ───────────────────────── early fade ─────────────────────────

    [Fact]
    public void A_fade_message_cancels_the_matching_bar()
    {
        Track(Wolf, Selo);
        Line("Stackprime", "You feel quick as a wolf.", T0);
        Line("Stackprime", "You feel your pace quicken.", T0);
        Assert.Equal(2, Timers.Count);

        Line("Stackprime", "Your speed returns to normal.", T0.AddSeconds(30)); // stored with "Your" in the database
        Assert.Equal("Stackprime: Selo's Accelerando", Assert.Single(Timers).Text);

        Line("Stackprime", "Your pace returns to normal.", T0.AddSeconds(31));   // stored without "Your"
        Assert.Empty(Timers);
    }

    [Fact]
    public void The_database_fade_text_matches_with_or_without_the_Your_prefix()
    {
        Track(Selo);
        Line("Stackprime", "You feel your pace quicken.", T0);

        Line("Stackprime", "pace returns to normal.", T0.AddSeconds(1)); // exactly as stored

        Assert.Empty(Timers);
    }

    [Fact]
    public void A_fade_only_cancels_the_bar_of_the_character_whose_log_reported_it()
    {
        Track(Wolf);
        Line("Stackprime", "You feel quick as a wolf.", T0);
        Line("Oodles", "You feel quick as a wolf.", T0);
        Assert.Equal(2, Timers.Count);

        Line("Oodles", "Your speed returns to normal.", T0.AddSeconds(10));

        Assert.Equal("Stackprime", Assert.Single(Timers).CharacterName);
    }

    [Fact]
    public void Fades_for_untracked_spells_or_without_a_bar_do_nothing()
    {
        Track(Wolf);
        Line("Stackprime", "You feel quick as a wolf.", T0);

        Line("Stackprime", "Your untracked fades.", T0.AddSeconds(1));         // not whitelisted
        Line("Oodles", "Your speed returns to normal.", T0.AddSeconds(1));     // Oodles has no bar
        Assert.Single(Timers);

        Line("Stackprime", "Your speed returns to normal.", T0.AddSeconds(2));
        Line("Stackprime", "Your speed returns to normal.", T0.AddSeconds(3)); // already gone
        Assert.Empty(Timers);
    }

    [Fact]
    public void A_shared_fade_message_cancels_every_matching_tracked_bar()
    {
        Track(Wolf, PackSpirit);
        Line("Stackprime", "You begin casting Pack Spirit.", T0);
        Line("Stackprime", "You feel quick as a wolf.", T0.AddSeconds(1));  // Pack Spirit (pending)
        Line("Stackprime", "You feel quick as a wolf.", T0.AddSeconds(2));  // Spirit of Wolf (longest)
        Assert.Equal(2, Timers.Count);

        Line("Stackprime", "Your speed returns to normal.", T0.AddSeconds(3));

        Assert.Empty(Timers);
    }

    // ───────────────────────── whitelist & database changes ─────────────────────────

    [Fact]
    public void Tracking_can_be_configured_before_the_spell_file_is_loaded_and_follows_reloads()
    {
        var spells = new SpellDatabaseService();
        var engine = new TriggerEngineService();
        var tracker = new BuffTrackerService(spells, engine);

        tracker.SetTrackedSpells(new[] { Wolf });
        tracker.OnLine("Stackprime", "You feel quick as a wolf.", T0);
        Assert.Empty(engine.Snapshot.Timers); // nothing loaded yet

        spells.Load(Path.Combine(_dir, "spells_us.txt"));
        tracker.OnLine("Stackprime", "You feel quick as a wolf.", T0);
        Assert.Single(engine.Snapshot.Timers);

        spells.Clear();
        engine.ClearTimers();
        tracker.OnLine("Stackprime", "You feel quick as a wolf.", T0);
        Assert.Empty(engine.Snapshot.Timers);
        Assert.Equal(new[] { Wolf }, tracker.TrackedSpellIds); // the whitelist itself is untouched
    }

    [Fact]
    public void Emptying_the_whitelist_stops_all_tracking()
    {
        Track(Wolf);
        Line("Stackprime", "You feel quick as a wolf.", T0);
        Assert.Single(Timers);
        _engine.ClearTimers();

        Track();
        Line("Stackprime", "You feel quick as a wolf.", T0);

        Assert.Empty(Timers);
        Assert.Empty(_tracker.TrackedSpellIds);
    }

    [Fact]
    public void Changing_the_whitelist_discards_pending_casts()
    {
        Track(Wolf, PackSpirit);
        Line("Stackprime", "You begin casting Pack Spirit.", T0);
        Assert.Equal(1, _tracker.PendingCastCount);

        Track(Wolf, PackSpirit, Skin);

        Assert.Equal(0, _tracker.PendingCastCount);
    }

    // ───────────────────────── monitored characters (Phase 7) ─────────────────────────

    [Fact]
    public void Buffs_landing_on_unmonitored_players_are_ignored_once_a_target_list_is_set()
    {
        Track(Wolf);
        _tracker.SetAllowedTargets(new[] { "Oodles" });

        Line("Stackprime", "Oodles looks faster.", T0); // monitored
        Line("Stackprime", "Randomguy looks faster.", T0.AddSeconds(1)); // a stranger in the raid

        ActiveTimerSnapshot timer = Assert.Single(Timers);
        Assert.Contains("Oodles", timer.Text);
    }

    [Fact]
    public void Buffs_on_yourself_are_always_tracked_and_no_list_means_everyone()
    {
        Track(Wolf);
        _tracker.SetAllowedTargets(new[] { "Oodles" });
        Line("Stackprime", "You feel quick as a wolf.", T0);
        Assert.Single(Timers);

        _tracker.SetAllowedTargets(null);
        Line("Stackprime", "Randomguy looks faster.", T0.AddSeconds(1));
        Assert.Equal(2, Timers.Count);
    }
}
