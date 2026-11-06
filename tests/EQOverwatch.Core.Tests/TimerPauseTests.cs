using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class TimerPauseTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 2, 21, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Source = BuffTrackerService.TimerSourceId;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));
    private readonly TriggerEngineService _engine = new();
    private readonly TimerPauseService _pause;

    public TimerPauseTests()
    {
        _pause = new TimerPauseService(_engine);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void Bar(string who, string spell, double seconds, DateTime at, TimerOverlayCategory category = TimerOverlayCategory.Buffs) =>
        _engine.StartTimer(Source, BuffTrackerService.TimerSourceName, who, $"{who}: {spell}", seconds, at, category);

    private ActiveTimerSnapshot Only() => Assert.Single(_engine.Snapshot.Timers);

    private void Line(string who, string text, DateTime at) => _pause.OnLine(who, text, at);

    // ───────────────────────── the engine ─────────────────────────

    [Fact]
    public void A_stopped_bar_keeps_its_time_and_gets_it_back_when_the_clock_starts_again()
    {
        Bar("Stackprime", "Spirit of Wolf", 60, T0);

        _engine.PauseSubject(Source, "Stackprime", T0.AddSeconds(10), hidden: false, includeSongs: false);
        Assert.Equal(50, Only().RemainingSeconds(T0.AddSeconds(40)), 3);
        Assert.Equal(50, Only().RemainingSeconds(T0.AddHours(5)), 3);
        Assert.False(Only().IsExpired(T0.AddHours(5)));

        _engine.ResumeSubject(Source, "Stackprime", T0.AddSeconds(40));
        Assert.Equal(T0.AddSeconds(90), Only().ExpiresUtc);
        Assert.Equal(50, Only().RemainingSeconds(T0.AddSeconds(40)), 3);
        Assert.Equal(30, Only().RemainingSeconds(T0.AddSeconds(60)), 3);
        Assert.False(Only().IsPaused);
    }

    [Fact]
    public void A_hidden_bar_is_left_off_the_overlay_until_it_comes_back()
    {
        Bar("Stackprime", "Spirit of Wolf", 60, T0);

        _engine.PauseSubject(Source, "Stackprime", T0.AddSeconds(5), hidden: true, includeSongs: true);
        Assert.Empty(_engine.Snapshot.GetActive(T0.AddSeconds(6)));
        Assert.Empty(_engine.Snapshot.GetActive(T0.AddSeconds(6), TimerOverlayCategory.Buffs));

        _engine.ResumeSubject(Source, "Stackprime", T0.AddSeconds(100));
        Assert.Single(_engine.Snapshot.GetActive(T0.AddSeconds(101)));
    }

    [Fact]
    public void Songs_keep_running_unless_asked_to_stop_with_the_rest()
    {
        Bar("Stackprime", "Selo's", 24, T0, TimerOverlayCategory.Songs);
        Bar("Stackprime", "Spirit of Wolf", 60, T0);

        _engine.PauseSubject(Source, "Stackprime", T0.AddSeconds(4), hidden: false, includeSongs: false);

        ActiveTimerSnapshot song = _engine.Snapshot.Timers.Single(t => t.Category == TimerOverlayCategory.Songs);
        ActiveTimerSnapshot buff = _engine.Snapshot.Timers.Single(t => t.Category == TimerOverlayCategory.Buffs);
        Assert.False(song.IsPaused);
        Assert.True(buff.IsPaused);
        Assert.True(song.IsExpired(T0.AddSeconds(30)));
    }

    [Fact]
    public void A_bar_started_while_the_clock_is_stopped_starts_stopped_and_other_characters_are_untouched()
    {
        _engine.PauseSubject(Source, "Stackprime", T0, hidden: false, includeSongs: false);
        Bar("Stackprime", "Spirit of Wolf", 60, T0.AddSeconds(5));
        Bar("Oodles", "Spirit of Wolf", 60, T0.AddSeconds(5));

        ActiveTimerSnapshot mine = _engine.Snapshot.Timers.Single(t => t.CharacterName == "Stackprime");
        ActiveTimerSnapshot other = _engine.Snapshot.Timers.Single(t => t.CharacterName == "Oodles");
        Assert.Equal(60, mine.RemainingSeconds(T0.AddMinutes(10)), 3);
        Assert.False(other.IsPaused);
        Assert.Equal(0, other.RemainingSeconds(T0.AddMinutes(10)), 3);
    }

    [Fact]
    public void Cancelling_a_subject_removes_only_its_bars()
    {
        Bar("Stackprime", "Spirit of Wolf", 60, T0);
        Bar("Stackprime", "Clarity", 60, T0);
        Bar("Oodles", "Spirit of Wolf", 60, T0);

        Assert.Equal(2, _engine.CancelSubject(Source, "stackprime"));
        Assert.Equal("Oodles", Only().CharacterName);
    }

    // ───────────────────────── zones ─────────────────────────

    [Fact]
    public void Entering_a_safe_zone_stops_the_bars_and_leaving_gives_the_time_back()
    {
        Bar("Stackprime", "Spirit of Wolf", 600, T0);

        Line("Stackprime", "You have entered The Bazaar.", T0.AddSeconds(10));
        Assert.Equal(TimerClockState.SafeZone, _pause.StateOf("Stackprime"));
        Assert.Equal(590, Only().RemainingSeconds(T0.AddMinutes(20)), 3);

        Line("Stackprime", "You have entered Crescent Reach.", T0.AddSeconds(70));
        Assert.Equal(TimerClockState.Running, _pause.StateOf("Stackprime"));
        Assert.Equal(T0.AddSeconds(660), Only().ExpiresUtc);
    }

    [Theory]
    [InlineData("You have entered Plane of Knowledge.")]
    [InlineData("You have entered The Plane of Knowledge.")]
    [InlineData("You have entered Guild Lobby.")]
    [InlineData("You have entered Guild Hall.")]
    [InlineData("You have entered The Nexus.")]
    public void The_built_in_zones_stop_the_bars(string line)
    {
        Bar("Stackprime", "Spirit of Wolf", 600, T0);

        Line("Stackprime", line, T0.AddSeconds(1));

        Assert.Equal(TimerClockState.SafeZone, _pause.StateOf("Stackprime"));
    }

    [Fact]
    public void A_line_that_only_looks_like_a_zone_change_changes_nothing()
    {
        Line("Stackprime", "You have entered The Bazaar.", T0);
        Line("Stackprime", "You have entered an area where levitation effects do not function.", T0.AddSeconds(5));

        Assert.Equal(TimerClockState.SafeZone, _pause.StateOf("Stackprime"));
    }

    [Fact]
    public void The_zone_list_can_be_changed()
    {
        _pause.SetSafeZones(new[] { "The Plane of Time" });
        Bar("Stackprime", "Spirit of Wolf", 600, T0);

        Line("Stackprime", "You have entered The Bazaar.", T0.AddSeconds(1));
        Assert.Equal(TimerClockState.Running, _pause.StateOf("Stackprime"));

        Line("Stackprime", "You have entered Plane of Time.", T0.AddSeconds(2));
        Assert.Equal(TimerClockState.SafeZone, _pause.StateOf("Stackprime"));
    }

    [Fact]
    public void One_characters_zone_does_not_stop_another_characters_bars()
    {
        Bar("Stackprime", "Spirit of Wolf", 600, T0);
        Bar("Oodles", "Spirit of Wolf", 600, T0);

        Line("Oodles", "You have entered The Bazaar.", T0.AddSeconds(10));

        Assert.False(_engine.Snapshot.Timers.Single(t => t.CharacterName == "Stackprime").IsPaused);
        Assert.True(_engine.Snapshot.Timers.Single(t => t.CharacterName == "Oodles").IsPaused);
    }

    // ───────────────────────── logging out and in ─────────────────────────

    [Fact]
    public void A_camp_that_is_not_abandoned_hides_and_stops_the_bars_until_the_next_line_from_that_log()
    {
        Bar("Stackprime", "Spirit of Wolf", 600, T0);

        Line("Stackprime", "It will take you about 30 seconds to prepare your camp.", T0.AddSeconds(10));
        _pause.Tick(T0.AddSeconds(20));
        Assert.Equal(TimerClockState.Running, _pause.StateOf("Stackprime")); // still counting down the camp

        _pause.Tick(T0.AddSeconds(44));
        Assert.Equal(TimerClockState.LoggedOut, _pause.StateOf("Stackprime"));
        Assert.Empty(_engine.Snapshot.GetActive(T0.AddSeconds(50)));

        Line("Stackprime", "Welcome to EverQuest!", T0.AddSeconds(344));
        Assert.Equal(TimerClockState.Running, _pause.StateOf("Stackprime"));
        ActiveTimerSnapshot back = Assert.Single(_engine.Snapshot.GetActive(T0.AddSeconds(345)));
        Assert.Equal(T0.AddSeconds(900), back.ExpiresUtc); // the 300 seconds offline were given back
    }

    [Fact]
    public void Abandoning_the_camp_keeps_the_character_logged_in()
    {
        Bar("Stackprime", "Spirit of Wolf", 600, T0);

        Line("Stackprime", "It will take you about 30 seconds to prepare your camp.", T0.AddSeconds(10));
        Line("Stackprime", "You abandon your preparations to camp.", T0.AddSeconds(15));
        _pause.Tick(T0.AddSeconds(120));

        Assert.Equal(TimerClockState.Running, _pause.StateOf("Stackprime"));
        Assert.Single(_engine.Snapshot.GetActive(T0.AddSeconds(121)));
    }

    [Fact]
    public void Logging_back_in_to_a_safe_zone_keeps_the_bars_stopped_but_shown()
    {
        Bar("Stackprime", "Spirit of Wolf", 600, T0);
        Line("Stackprime", "You have entered The Bazaar.", T0.AddSeconds(10));
        Line("Stackprime", "It will take you about 30 seconds to prepare your camp.", T0.AddSeconds(20));
        _pause.Tick(T0.AddSeconds(60));
        Assert.Equal(TimerClockState.LoggedOut, _pause.StateOf("Stackprime"));

        Line("Stackprime", "Welcome to EverQuest!", T0.AddSeconds(500));

        Assert.Equal(TimerClockState.SafeZone, _pause.StateOf("Stackprime"));
        ActiveTimerSnapshot bar = Assert.Single(_engine.Snapshot.GetActive(T0.AddSeconds(501)));
        Assert.Equal(590, bar.RemainingSeconds(T0.AddSeconds(900)), 3);
    }

    [Fact]
    public void Dying_removes_the_bars()
    {
        Bar("Stackprime", "Spirit of Wolf", 600, T0);
        Bar("Oodles", "Spirit of Wolf", 600, T0);

        Line("Stackprime", "You have been slain by a frost giant!", T0.AddSeconds(30));

        Assert.Equal("Oodles", Only().CharacterName);
    }

    // ───────────────────────── saving ─────────────────────────

    [Fact]
    public void Bars_and_each_characters_state_survive_closing_the_app()
    {
        string file = Path.Combine(_dir, "bufftimers.json");
        _pause.SetStoragePath(file);
        Bar("Stackprime", "Spirit of Wolf", 600, T0);                  // running
        Bar("Oodles", "Clarity", 600, T0);                              // will be logged out
        Bar("Stackprime", "Short", 5, T0);                              // will have run out
        Line("Oodles", "It will take you about 30 seconds to prepare your camp.", T0.AddSeconds(10));
        _pause.Tick(T0.AddSeconds(50)); // Oodles is logged out
        _pause.Save(T0.AddSeconds(60));

        var engine2 = new TriggerEngineService();
        var pause2 = new TimerPauseService(engine2);
        pause2.SetStoragePath(file, T0.AddSeconds(200));

        Assert.Equal(TimerClockState.LoggedOut, pause2.StateOf("Oodles"));
        TriggerSnapshot snapshot = engine2.Snapshot;
        Assert.DoesNotContain(snapshot.Timers, t => t.Text.Contains("Short"));

        ActiveTimerSnapshot wolf = snapshot.Timers.Single(t => t.Text == "Stackprime: Spirit of Wolf");
        Assert.Equal(T0.AddSeconds(600), wolf.ExpiresUtc); // the game's clock went on while the app was closed
        Assert.False(wolf.IsPaused);

        ActiveTimerSnapshot clarity = snapshot.Timers.Single(t => t.Text == "Oodles: Clarity");
        Assert.True(clarity.Hidden);
        Assert.Equal(550, clarity.RemainingSeconds(T0.AddHours(3)), 3); // stopped at the 50 second mark
        Assert.DoesNotContain(snapshot.GetActive(T0.AddSeconds(210), TimerOverlayCategory.Buffs), t => t.CharacterName == "Oodles");

        pause2.OnLine("Oodles", "Welcome to EverQuest!", T0.AddSeconds(300));
        Assert.Contains(engine2.Snapshot.GetActive(T0.AddSeconds(301)), t => t.Text == "Oodles: Clarity");
    }

    [Fact]
    public void A_damaged_save_file_starts_empty()
    {
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "bufftimers.json");
        File.WriteAllText(file, "{ not json");

        _pause.SetStoragePath(file);

        Assert.Empty(_engine.Snapshot.Timers);
    }
}
