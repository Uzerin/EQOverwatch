using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>The click spot: held for the click, predicted from the chain's pace, and let go when the cast starts or the call is late.</summary>
public sealed class AutoClickHoldTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 18, 20, 0, 0, DateTimeKind.Utc);

    private sealed class FakeSender : IClickSender
    {
        public bool Focused = true;
        public List<string> Log { get; } = new();
        public bool Held;

        public bool IsGameFocused() => Focused;

        public void ClickAtCursor() => Log.Add(Held ? "click on spot" : "click at pointer");

        public void PressKey(KeyChord key) => Log.Add("key " + key);

        public bool HoldAt(ClickSpot spot)
        {
            if (!Focused)
            {
                return false;
            }

            Held = true;
            Log.Add($"hold {spot.X},{spot.Y}");
            return true;
        }

        public void Release()
        {
            if (Held)
            {
                Held = false;
                Log.Add("release");
            }
        }
    }

    private readonly TriggerEngineService _engine = new();
    private readonly ChChainService _chain;
    private readonly AutoClickService _click;
    private readonly FakeSender _sender = new();
    private DateTime _now = T0;

    public AutoClickHoldTests()
    {
        _chain = new ChChainService(_engine, new ChChainSettings { TotalClerics = 4 }, autoTick: false);
        _chain.SetPosition("Stackprime", 3);
        _click = new AutoClickService(_chain, _engine, _sender, autoTick: false, clock: () => _now);
        _click.Spot = new ClickSpot(412, 655);
        _click.SetArmed(true);
    }

    public void Dispose()
    {
        _click.Dispose();
        _chain.Dispose();
    }

    private void Call(double seconds, int slot)
    {
        At(seconds);
        _chain.OnLine("Stackprime", $"Ream tells the raid, '{slot}-{slot}-{slot} CAST NOW!'", _now);
    }

    private void At(double seconds)
    {
        _now = T0.AddSeconds(seconds);
        _click.Tick(_now);
    }

    private void CastStarts(double seconds)
    {
        At(seconds);
        _chain.OnLine("Stackprime", "You begin casting Complete Heal.", _now);
    }

    [Fact]
    public void The_first_turn_of_a_fight_holds_the_spot_at_the_call_clicks_and_lets_go_when_the_cast_starts()
    {
        Call(0, 3);
        Assert.Equal(new[] { "hold 412,655", "click on spot" }, _sender.Log);
        Assert.True(_click.IsHolding);

        CastStarts(0.3);
        Assert.Equal(new[] { "hold 412,655", "click on spot", "release" }, _sender.Log);
        Assert.False(_click.IsHolding);
    }

    [Fact]
    public void From_the_chains_pace_the_spot_is_held_half_a_second_before_the_expected_call()
    {
        Call(0, 1);
        Call(2, 2); // pace 2 s: Pos 3 is expected at 4 s
        Assert.Equal(T0.AddSeconds(4), _click.ExpectedCallUtc);

        At(3.4);
        Assert.Empty(_sender.Log);
        At(3.5);
        Assert.Equal(new[] { "hold 412,655" }, _sender.Log);

        Call(4, 3);
        CastStarts(4.2);
        Assert.Equal(new[] { "hold 412,655", "click on spot", "release" }, _sender.Log);
    }

    [Fact]
    public void A_late_call_lets_go_one_and_a_half_seconds_after_the_expected_time_and_still_clicks_when_it_comes()
    {
        Call(0, 1);
        Call(2, 2);
        At(3.6);
        At(5.4);
        Assert.True(_click.IsHolding);
        At(5.5);
        Assert.False(_click.IsHolding);

        Call(7, 3);
        Assert.Equal(new[] { "hold 412,655", "release", "hold 412,655", "click on spot" }, _sender.Log);
    }

    [Fact]
    public void With_no_cast_two_seconds_after_the_click_the_pointer_is_let_go()
    {
        Call(0, 3);
        At(1.9);
        Assert.True(_click.IsHolding);
        At(2.0);
        Assert.False(_click.IsHolding);
    }

    [Fact]
    public void Thirty_seconds_without_a_call_starts_a_new_fight_with_no_pace()
    {
        Call(0, 1);
        Call(2, 2);
        Call(4, 3);
        CastStarts(4.2);
        Call(40, 2); // a new fight: one call is not a pace
        Assert.Null(_click.ExpectedCallUtc);
    }

    [Fact]
    public void Switching_it_off_lets_go_at_once()
    {
        Call(0, 3);
        _click.SetArmed(false);
        Assert.False(_click.IsHolding);
        Assert.Equal("release", _sender.Log[^1]);
    }

    [Fact]
    public void Nothing_is_held_or_clicked_while_the_game_is_not_in_front()
    {
        _sender.Focused = false;
        Call(0, 1);
        Call(2, 2);
        At(3.6);
        Call(4, 3);
        Assert.Empty(_sender.Log);
        Assert.Equal(1, _click.Skipped);
    }

    [Fact]
    public void Without_a_spot_it_clicks_where_the_pointer_is_and_never_holds_it()
    {
        _click.Spot = null;
        Call(0, 1);
        Call(2, 2);
        At(3.6);
        Call(4, 3);
        Assert.Equal(new[] { "click at pointer" }, _sender.Log);
    }

    [Fact]
    public void A_position_one_cleric_is_predicted_from_the_last_position_in_the_chain()
    {
        _chain.SetPositions(new[] { new KeyValuePair<string, int>("Stackprime", 1) });
        Call(0, 3);
        Call(2, 4);
        Assert.Equal(T0.AddSeconds(4), _click.ExpectedCallUtc);
    }

    // ───────────────────────────── timed mode and key press ─────────────────────────────

    [Fact]
    public void Timed_mode_waits_for_your_first_cast_then_presses_every_interval_after_your_last_cast_and_ignores_chat()
    {
        _click.Timing = AutoClickTiming.Timed;
        _click.IntervalSeconds = 10;

        Call(0, 3); // chat: ignored for pressing
        At(5);
        Assert.Empty(_sender.Log);
        Assert.Null(_click.NextTimedUtc);

        CastStarts(6); // your first Complete Heal by hand
        Assert.Equal(T0.AddSeconds(16), _click.NextTimedUtc);
        At(15.4);
        Assert.Empty(_sender.Log);
        At(15.5);
        Assert.Equal(new[] { "hold 412,655" }, _sender.Log);
        At(16);
        Assert.Equal(new[] { "hold 412,655", "click on spot" }, _sender.Log);

        CastStarts(16.3); // the press worked: let go, and the next one is 10 s after this cast
        Assert.Equal("release", _sender.Log[^1]);
        Assert.Equal(T0.AddSeconds(26.3), _click.NextTimedUtc);

        CastStarts(20); // cast by hand early: the count starts again from it
        Assert.Equal(T0.AddSeconds(30), _click.NextTimedUtc);
    }

    [Fact]
    public void Switching_timed_mode_on_again_waits_for_a_new_first_cast()
    {
        _click.Timing = AutoClickTiming.Timed;
        CastStarts(0);
        Assert.NotNull(_click.NextTimedUtc);
        _click.SetArmed(false);
        _click.SetArmed(true);
        Assert.Null(_click.NextTimedUtc);
    }

    [Fact]
    public void Key_mode_presses_the_key_and_never_touches_the_mouse()
    {
        _click.Action = AutoClickAction.KeyPress;
        _click.Key = new KeyChord(0x72, Shift: true);
        Call(0, 1);
        Call(2, 2);
        At(3.6);
        Call(4, 3);
        Assert.Equal(new[] { "key Shift+F3" }, _sender.Log);
        Assert.False(_click.IsHolding);
    }

    [Fact]
    public void The_interval_is_kept_between_ten_and_sixty_seconds_in_tenths()
    {
        _click.IntervalSeconds = 12.34;
        Assert.Equal(12.3, _click.IntervalSeconds);
        _click.IntervalSeconds = 5;
        Assert.Equal(10.0, _click.IntervalSeconds);
        _click.IntervalSeconds = 90;
        Assert.Equal(60.0, _click.IntervalSeconds);
    }

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
