using EQCompanion.Core.Models;
using EQCompanion.Core.Services;
using EQCompanion.Core.Settings;
using Xunit;

namespace EQCompanion.Core.Tests;

public sealed class ChChainServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    private readonly TriggerEngineService _engine = new();
    private readonly ChChainService _chain;
    private readonly List<TriggerFiredEvent> _spoken = new();

    public ChChainServiceTests()
    {
        _chain = new ChChainService(_engine, new ChChainSettings(), autoTick: false);
        _engine.TriggerFired += _spoken.Add;
    }

    public void Dispose() => _chain.Dispose();

    private IReadOnlyList<ActiveTimerSnapshot> Timers => _engine.Snapshot.Timers;

    private IEnumerable<ActiveTimerSnapshot> Alerts => Timers.Where(t => t.Kind == TimerKind.TextAlert);

    private void Line(string character, string message, DateTime at) => _chain.OnLine(character, message, at);

    // ───────────────────────── announcements ─────────────────────────

    [Fact]
    public void An_announcement_starts_a_ten_second_bar_in_the_buffs_overlay_by_default()
    {
        Line("Ream", "Ream tells the raid, '4-4-4 - Tank'", T0);

        ActiveTimerSnapshot bar = Assert.Single(Timers);
        Assert.Equal("Ream: Complete Heal 4", bar.Text);
        Assert.Equal(TimerKind.Countdown, bar.Kind);
        Assert.Equal(10, bar.DurationSeconds);
        Assert.Equal(TimerOverlayCategory.Buffs, bar.Category);
    }

    [Fact]
    public void With_the_chain_overlay_enabled_the_bar_goes_to_the_chain_overlay()
    {
        _chain.UseChChainOverlay = true;

        Line("Ream", "Ream tells the raid, '4-4-4 - Tank'", T0);

        Assert.Equal(TimerOverlayCategory.ChChain, Assert.Single(Timers).Category);
    }

    [Fact]
    public void Your_own_announcement_uses_the_character_whose_log_it_was_read_from()
    {
        Line("Stackprime", "You tell your raid, '2-2-2 - Warrior'", T0);

        Assert.Equal("Stackprime: Complete Heal 2", Assert.Single(Timers).Text);
    }

    [Fact]
    public void Mismatched_tags_and_ordinary_chat_are_ignored()
    {
        Line("Ream", "Ream tells the raid, '4-5-4 - Tank'", T0);
        Line("Ream", "Ream tells the raid, 'pull in 5'", T0);
        Line("Ream", "Ream says, '4-4-4 - Tank'", T0);

        Assert.Empty(Timers);
    }

    [Fact]
    public void A_custom_tag_format_is_honoured()
    {
        _chain.Configure(new ChChainSettings { PositionTagFormat = "[{N}]", CueKeyword = "GO" });

        Line("Ream", "Ream tells the raid, '[3] - Tank'", T0);
        Line("Ream", "Ream tells the raid, '3-3-3 - Tank'", T0);

        Assert.Equal("Ream: Complete Heal 3", Assert.Single(Timers).Text);
    }

    [Fact]
    public void Only_the_position_tag_matters_so_a_differently_worded_announcement_still_starts_the_bar()
    {
        Line("Ream", "Ream tells the raid, '5-5-5 Tank'", T0);                 // no dash
        Line("Ream", "Ream tells the raid, 'CH on Tank >>> 6-6-6'", T0);       // tag at the end
        Line("Ream", "Ream tells the raid, '7-7-7: tank (rotate)'", T0);       // different punctuation

        Assert.Equal(
            new[] { "Ream: Complete Heal 5", "Ream: Complete Heal 6", "Ream: Complete Heal 7" },
            Timers.Select(x => x.Text).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void A_position_tag_inside_a_longer_number_is_not_that_position()
    {
        Line("Ream", "Ream tells the raid, '13-13-13 - Tank'", T0); // 13 is a valid tag only as 13-13-13, not 3-3-3

        ActiveTimerSnapshot bar = Assert.Single(Timers);
        Assert.Equal("Ream: Complete Heal 13", bar.Text);
    }

    // ───────────────────────── cue and late alarm ─────────────────────────

    [Theory]
    [InlineData("Ream tells the raid, '3-3-3 CAST NOW!'")]
    [InlineData("Ream tells the raid, '3-3-3 cast now'")]
    [InlineData("Ream tells the raid, '3-3-3 Cast Now!!!'")]
    [InlineData("Ream tells the raid, 'Tank is low, 3-3-3 - cast now please'")]
    public void The_cue_is_recognised_in_any_letter_case_and_anywhere_after_the_tag(string line)
    {
        _chain.SetPosition("Stackprime", 3);

        Line("Stackprime", line, T0);

        Assert.True(_chain.IsAwaitingCast("Stackprime"));
        Assert.Empty(Timers); // a cue is not itself a chain bar
    }

    [Fact]
    public void A_cue_for_my_position_starts_waiting_and_the_alarm_sounds_after_the_threshold()
    {
        _chain.SetPosition("Stackprime", 3);

        Line("Stackprime", "Ream tells the raid, '3-3-3 CAST NOW!'", T0);

        Assert.True(_chain.IsAwaitingCast("Stackprime"));
        Assert.Equal(T0.AddSeconds(1), _chain.GetAlarmDueUtc("Stackprime"));

        _chain.Tick(T0.AddSeconds(0.5)); // not late yet
        Assert.Empty(Alerts);
        Assert.Empty(_spoken);

        _chain.Tick(T0.AddSeconds(1.0)); // 1 second without a cast: late
        ActiveTimerSnapshot alert = Assert.Single(Alerts);
        Assert.Equal(ChChainService.AlarmText, alert.Text);
        Assert.Equal("Stackprime", alert.CharacterName);
        Assert.Single(_spoken);
        Assert.Equal(ChChainService.AlarmSpeech, _spoken[0].AudioText);
    }

    [Fact]
    public void The_alarm_repeats_every_second_until_the_ten_second_window_ends()
    {
        _chain.SetPosition("Stackprime", 3);
        Line("Stackprime", "Ream tells the raid, '3-3-3 CAST NOW!'", T0);

        for (int ms = 0; ms <= 12_000; ms += 100)
        {
            _chain.Tick(T0.AddMilliseconds(ms));
        }

        // sounded at 1s, 2s, ... 9s (the window closes at 10s)
        Assert.Equal(9, _spoken.Count);
        Assert.False(_chain.IsAwaitingCast("Stackprime"));
        Assert.Empty(Alerts);
    }

    [Fact]
    public void Beginning_the_cast_in_time_prevents_the_alarm_and_clears_it_when_already_showing()
    {
        _chain.SetPosition("Stackprime", 3);

        Line("Stackprime", "Ream tells the raid, '3-3-3 CAST NOW!'", T0);
        Line("Stackprime", "You begin casting Complete Heal.", T0.AddSeconds(0.4));
        _chain.Tick(T0.AddSeconds(2));
        Assert.Empty(_spoken);
        Assert.False(_chain.IsAwaitingCast("Stackprime"));

        Line("Stackprime", "Ream tells the raid, '3-3-3 CAST NOW!'", T0.AddSeconds(30));
        _chain.Tick(T0.AddSeconds(31.5));
        Assert.Single(Alerts);

        Line("Stackprime", "You begin casting Complete Heal.", T0.AddSeconds(32));
        Assert.Empty(Alerts);
    }

    [Fact]
    public void A_cue_for_another_position_does_nothing()
    {
        _chain.SetPosition("Stackprime", 3);

        Line("Stackprime", "Ream tells the raid, '5-5-5 CAST NOW!'", T0);

        Assert.False(_chain.IsAwaitingCast("Stackprime"));
        _chain.Tick(T0.AddSeconds(3));
        Assert.Empty(_spoken);
    }

    [Fact]
    public void A_fizzle_right_after_the_cast_started_rearms_the_alarm_at_once()
    {
        _chain.SetPosition("Stackprime", 1);
        Line("Stackprime", "Ream tells the raid, '1-1-1 CAST NOW!'", T0);
        Line("Stackprime", "You begin casting Complete Heal.", T0.AddSeconds(0.5));

        Line("Stackprime", "Your spell fizzles!", T0.AddSeconds(3));
        Assert.True(_chain.IsAwaitingCast("Stackprime"));
        _chain.Tick(T0.AddSeconds(3));

        Assert.Single(Alerts);
        Assert.Single(_spoken);
    }

    [Fact]
    public void A_fizzle_without_a_chain_cast_is_not_ours()
    {
        _chain.SetPosition("Stackprime", 1);

        Line("Stackprime", "Your spell fizzles!", T0);

        Assert.False(_chain.IsAwaitingCast("Stackprime"));
    }

    [Fact]
    public void A_slot_belongs_to_one_character_and_zero_removes_a_character_from_the_chain()
    {
        _chain.SetPosition("Stackprime", 2);
        _chain.SetPosition("Oodles", 2);

        Assert.Equal(0, _chain.GetPosition("Stackprime"));
        Assert.Equal(2, _chain.GetPosition("Oodles"));

        _chain.SetPosition("Oodles", 0);
        Assert.Equal(0, _chain.GetPosition("Oodles"));
    }

    [Fact]
    public void The_late_alarm_uses_the_chain_overlay_when_it_is_enabled()
    {
        _chain.UseChChainOverlay = true;
        _chain.SetPosition("Stackprime", 3);
        Line("Stackprime", "Ream tells the raid, '3-3-3 CAST NOW!'", T0);

        _chain.Tick(T0.AddSeconds(1));

        Assert.Equal(TimerOverlayCategory.ChChain, Assert.Single(Alerts).Category);
    }
}
