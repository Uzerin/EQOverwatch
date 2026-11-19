using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>The CH chain auto-press: the CH key on your turn (chat cue or timed), with the assist key 1.0 s and 0.5 s before it.</summary>
public sealed class AutoPressTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 18, 20, 0, 0, DateTimeKind.Utc);

    private sealed class FakeSender : IClickSender
    {
        public bool Focused = true;
        public List<string> Log { get; } = new();

        public bool IsGameFocused() => Focused;

        public void PressKey(KeyChord key) => Log.Add(key.ToString());
    }

    private readonly TriggerEngineService _engine = new();
    private readonly ChChainService _chain;
    private readonly AutoClickService _press;
    private readonly FakeSender _sender = new();
    private readonly List<string> _spoken = new();
    private readonly List<(double At, string Key)> _timeline = new();
    private DateTime _now = T0;

    public AutoPressTests()
    {
        _chain = new ChChainService(_engine, new ChChainSettings { TotalClerics = 4 }, autoTick: false);
        _chain.SetPosition("Stackprime", 3);
        _press = new AutoClickService(_chain, _engine, _sender, autoTick: false, clock: () => _now);
        _engine.TriggerFired += e => _spoken.Add(e.AudioText ?? string.Empty);
    }

    public void Dispose()
    {
        _press.Dispose();
        _chain.Dispose();
    }

    /// <summary>Moves the clock to <paramref name="seconds"/>, ticking every 50 ms on the way, and notes when each key was pressed.</summary>
    private void At(double seconds)
    {
        DateTime to = T0.AddSeconds(seconds);
        while (_now < to)
        {
            _now = _now.AddMilliseconds(50) > to ? to : _now.AddMilliseconds(50);
            int before = _sender.Log.Count;
            _press.Tick(_now);
            foreach (string key in _sender.Log.Skip(before))
            {
                _timeline.Add((Math.Round((_now - T0).TotalSeconds, 2), key));
            }
        }
    }

    private void Call(double seconds, int slot, string by = "Ream")
    {
        At(seconds);
        int before = _sender.Log.Count;
        _chain.OnLine("Stackprime", $"{by} tells the raid, '{slot}-{slot}-{slot} CAST NOW!'", _now);
        foreach (string key in _sender.Log.Skip(before))
        {
            _timeline.Add((Math.Round((_now - T0).TotalSeconds, 2), key));
        }
    }

    private void CastStarts(double seconds)
    {
        At(seconds);
        _chain.OnLine("Stackprime", "You begin casting Complete Heal.", _now);
    }

    // ───────────────────────────── chat cue, no assist ─────────────────────────────

    [Fact]
    public void It_is_off_until_switched_on()
    {
        Call(0, 3);
        Assert.Empty(_sender.Log);
    }

    [Fact]
    public void Your_turn_presses_the_ch_key_once_and_a_repeat_of_the_cue_does_not()
    {
        _press.SetArmed(true);
        Call(0, 3);
        Call(1, 3, by: "Leinhard"); // the same turn, said again
        Call(1.2, 2); // someone else's turn
        Assert.Equal(new[] { "2" }, _sender.Log);
        Assert.Equal(1, _press.Clicks);
    }

    [Fact]
    public void Nothing_is_pressed_while_the_game_is_not_in_front_or_without_a_sender()
    {
        _press.SetArmed(true);
        _sender.Focused = false;
        Call(0, 3);
        Assert.Empty(_sender.Log);
        Assert.Equal(1, _press.Skipped);

        _press.Sender = null;
        Call(10, 3);
        Assert.Equal(2, _press.Skipped);
    }

    [Fact]
    public void No_more_than_ten_ch_presses_are_made_in_a_minute()
    {
        _press.SetArmed(true);
        for (int i = 0; i < 12; i++)
        {
            Call(i * 5, 3);
        }

        Assert.Equal(10, _sender.Log.Count);
        Assert.Equal(2, _press.Skipped);
    }

    [Fact]
    public void It_never_switches_itself_off_and_says_its_position()
    {
        _press.SetArmed(true);
        At(3);
        _press.Tick(T0.AddHours(3));
        Assert.True(_press.IsArmed);
        _chain.SetPositions(new[] { new KeyValuePair<string, int>("Stackprime", 6) });
        _chain.SetPositions(new[] { new KeyValuePair<string, int>("Stackprime", 6) });
        _press.SetArmed(false);
        Assert.Equal(new[] { "Auto press on, position 3", "Auto press now on position 6", "Auto press off" }, _spoken);
    }

    [Fact]
    public void Moving_from_position_3_to_6_while_on_presses_for_6_and_not_3()
    {
        _press.SetArmed(true);
        Call(0, 3);
        _chain.SetPositions(new[] { new KeyValuePair<string, int>("Stackprime", 6) });
        Call(30, 3);
        Call(60, 6);
        Assert.Equal(new[] { "2", "2" }, _sender.Log);
    }

    // ───────────────────────────── chat cue with the assist key ─────────────────────────────

    [Fact]
    public void First_turn_of_a_fight_presses_assist_at_the_cue_and_half_a_second_later_then_ch_a_second_after_the_cue()
    {
        _press.UseAssist = true;
        _press.SetArmed(true);
        Call(0, 3);
        At(2);
        Assert.Equal(new[] { (0.0, "1"), (0.5, "1"), (1.0, "2") }, _timeline);
    }

    [Fact]
    public void With_the_chains_pace_the_assist_goes_one_and_half_a_second_before_the_expected_call_and_ch_on_the_call()
    {
        _press.UseAssist = true;
        _press.SetArmed(true);
        Call(0, 1);
        Call(2, 2); // pace 2 s: Pos 3 expected at 4 s
        Call(4, 3);
        At(5);
        Assert.Equal(new[] { (3.0, "1"), (3.5, "1"), (4.0, "2") }, _timeline);
    }

    [Fact]
    public void A_call_after_only_the_first_assist_presses_the_second_then_ch_half_a_second_apart()
    {
        _press.UseAssist = true;
        _press.SetArmed(true);
        Call(0, 1);
        Call(2, 2);
        Call(3.2, 3); // early: only the first assist (3.0) went
        At(5);
        Assert.Equal(new[] { (3.0, "1"), (3.2, "1"), (3.7, "2") }, _timeline);
    }

    [Fact]
    public void A_predicted_call_that_does_not_come_is_given_up_and_a_later_call_starts_over()
    {
        _press.UseAssist = true;
        _press.SetArmed(true);
        Call(0, 1);
        Call(2, 2);
        At(6); // expected at 4: the assists went, no call by 5.5
        Assert.Null(_press.ExpectedCallUtc);
        Call(8, 3);
        At(10);
        Assert.Equal(new[] { (3.0, "1"), (3.5, "1"), (8.0, "1"), (8.5, "1"), (9.0, "2") }, _timeline);
    }

    [Fact]
    public void A_position_one_cleric_is_predicted_from_the_last_position_in_the_chain()
    {
        _chain.SetPositions(new[] { new KeyValuePair<string, int>("Stackprime", 1) });
        _press.UseAssist = true;
        _press.SetArmed(true);
        Call(0, 3);
        Call(2, 4);
        Assert.Equal(T0.AddSeconds(4), _press.ExpectedCallUtc);
    }

    // ───────────────────────────── timed ─────────────────────────────

    [Fact]
    public void Timed_mode_waits_for_your_first_cast_then_presses_every_interval_after_your_last_cast_and_ignores_chat()
    {
        _press.Timing = AutoClickTiming.Timed;
        _press.IntervalSeconds = 18;
        _press.UseAssist = true;
        _press.SetArmed(true);

        Call(1, 3); // chat: ignored for pressing
        At(5);
        Assert.Empty(_sender.Log);

        CastStarts(6); // your first Complete Heal by hand
        At(25);
        Assert.Equal(new[] { (23.0, "1"), (23.5, "1"), (24.0, "2") }, _timeline);

        CastStarts(25.3); // the cast that press started (logged late): the count still runs from the press
        Assert.Equal(42.0, (_press.NextTimedUtc!.Value - T0).TotalSeconds, 2);
        _chain.OnLine("Stackprime", "You begin casting Complete Heal.", T0.AddSeconds(23)); // logged in whole seconds: can read just before the press
        Assert.Equal(42.0, (_press.NextTimedUtc!.Value - T0).TotalSeconds, 2);
        At(43);
        Assert.Equal(new[] { (23.0, "1"), (23.5, "1"), (24.0, "2"), (41.0, "1"), (41.5, "1"), (42.0, "2") }, _timeline);

        CastStarts(50); // cast by hand early: the count starts again from it
        Assert.Equal(68.0, (_press.NextTimedUtc!.Value - T0).TotalSeconds, 2);
    }

    [Fact]
    public void Switching_timed_mode_on_again_waits_for_a_new_first_cast()
    {
        _press.Timing = AutoClickTiming.Timed;
        _press.SetArmed(true);
        CastStarts(0);
        Assert.NotNull(_press.NextTimedUtc);
        _press.SetArmed(false);
        _press.SetArmed(true);
        Assert.Null(_press.NextTimedUtc);
    }

    [Fact]
    public void The_interval_is_kept_between_ten_and_sixty_seconds_in_tenths()
    {
        _press.IntervalSeconds = 12.34;
        Assert.Equal(12.3, _press.IntervalSeconds);
        _press.IntervalSeconds = 5;
        Assert.Equal(10.0, _press.IntervalSeconds);
        _press.IntervalSeconds = 90;
        Assert.Equal(60.0, _press.IntervalSeconds);
    }

    [Fact]
    public void The_keys_start_as_2_for_ch_and_1_for_assist()
    {
        Assert.Equal("2", _press.Key.ToString());
        Assert.Equal("1", _press.AssistKey.ToString());
        Assert.False(_press.UseAssist);
    }

    // ───────────────────────────── keys ─────────────────────────────

    [Theory]
    [InlineData("1", 0x31, false, false, false)]
    [InlineData("Shift+F3", 0x72, false, false, true)]
    [InlineData("Ctrl+Alt+Q", 0x51, true, true, false)]
    [InlineData("Num5", 0x65, false, false, false)]
    [InlineData("Ctrl+Minus", 0xBD, true, false, false)]
    public void Keys_are_read_and_written_the_same_way(string text, int vk, bool ctrl, bool alt, bool shift)
    {
        Assert.True(KeyChord.TryParse(text, out KeyChord key));
        Assert.Equal(new KeyChord(vk, ctrl, alt, shift), key);
        Assert.Equal(text, key.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Hyper+1")]
    [InlineData("F99")]
    public void Nonsense_keys_are_refused(string text) => Assert.False(KeyChord.TryParse(text, out _));
}
