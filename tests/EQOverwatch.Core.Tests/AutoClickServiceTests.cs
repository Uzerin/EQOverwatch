using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class AutoClickServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 4, 20, 0, 0, DateTimeKind.Utc);
    private const string Cue = "Ream tells the raid, '3-3-3 CAST NOW!'";

    private sealed class FakeSender : IClickSender
    {
        public bool Focused = true;
        public int ClickCount;

        public bool IsGameFocused() => Focused;

        public void ClickAtCursor() => ClickCount++;
    }

    private readonly TriggerEngineService _engine = new();
    private readonly ChChainService _chain;
    private readonly AutoClickService _click;
    private readonly FakeSender _sender = new();
    private readonly List<TriggerFiredEvent> _spoken = new();
    private DateTime _now = T0;

    public AutoClickServiceTests()
    {
        _chain = new ChChainService(_engine, new ChChainSettings(), autoTick: false);
        _chain.SetPosition("Stackprime", 3);
        _click = new AutoClickService(_chain, _engine, _sender, autoTick: false, clock: () => _now);
        _engine.TriggerFired += _spoken.Add;
    }

    public void Dispose()
    {
        _click.Dispose();
        _chain.Dispose();
    }

    private void CueAt(DateTime at, string observedBy = "Stackprime", string line = Cue)
    {
        _now = at;
        _chain.OnLine(observedBy, line, at);
    }

    [Fact]
    public void It_is_off_until_switched_on_and_does_not_click_when_off()
    {
        Assert.False(_click.IsArmed);

        CueAt(T0);

        Assert.Equal(0, _sender.ClickCount);
    }

    [Fact]
    public void Your_turn_in_the_chain_makes_one_click_while_the_game_is_in_front()
    {
        _click.SetArmed(true);

        CueAt(T0);

        Assert.Equal(1, _sender.ClickCount);
        Assert.Equal(1, _click.Clicks);
    }

    [Fact]
    public void A_cue_for_someone_elses_position_makes_no_click()
    {
        _click.SetArmed(true);

        CueAt(T0, line: "Ream tells the raid, '5-5-5 CAST NOW!'");

        Assert.Equal(0, _sender.ClickCount);
    }

    [Fact]
    public void The_same_cue_read_from_two_logs_makes_one_click()
    {
        _click.SetArmed(true);

        CueAt(T0);
        CueAt(T0.AddSeconds(1), observedBy: "Pecos"); // another of your characters' logs sees the same raid chat

        Assert.Equal(1, _sender.ClickCount);
    }

    [Fact]
    public void The_next_turn_makes_another_click()
    {
        _click.SetArmed(true);

        CueAt(T0);
        CueAt(T0.AddSeconds(30));

        Assert.Equal(2, _sender.ClickCount);
    }

    [Fact]
    public void The_delay_holds_the_click_back_until_it_has_passed()
    {
        _click.SetArmed(true);
        _click.DelayMilliseconds = 500;

        CueAt(T0);
        Assert.Equal(0, _sender.ClickCount);

        _click.Tick(T0.AddMilliseconds(400));
        Assert.Equal(0, _sender.ClickCount);

        _click.Tick(T0.AddMilliseconds(600));
        Assert.Equal(1, _sender.ClickCount);

        _click.Tick(T0.AddMilliseconds(700));
        Assert.Equal(1, _sender.ClickCount); // one click per turn
    }

    [Fact]
    public void The_delay_is_kept_between_zero_and_five_seconds()
    {
        _click.DelayMilliseconds = 99_999;
        Assert.Equal(AutoClickService.MaxDelayMilliseconds, _click.DelayMilliseconds);

        _click.DelayMilliseconds = -5;
        Assert.Equal(0, _click.DelayMilliseconds);
    }

    [Fact]
    public void Nothing_is_clicked_when_the_game_is_not_the_window_in_front()
    {
        _click.SetArmed(true);
        _sender.Focused = false;

        CueAt(T0);

        Assert.Equal(0, _sender.ClickCount);
        Assert.Equal(1, _click.Skipped);
    }

    [Fact]
    public void Without_a_sender_nothing_is_clicked()
    {
        _click.Sender = null;
        _click.SetArmed(true);

        CueAt(T0);

        Assert.Equal(0, _sender.ClickCount);
        Assert.Equal(1, _click.Skipped);
    }

    [Fact]
    public void No_more_than_ten_clicks_are_made_in_a_minute()
    {
        _click.SetArmed(true);

        for (int i = 0; i < 12; i++)
        {
            CueAt(T0.AddSeconds(i * 4.5)); // 12 turns inside a minute
        }

        Assert.Equal(AutoClickService.MaxClicksPerMinute, _sender.ClickCount);
        Assert.Equal(2, _click.Skipped);
    }

    [Fact]
    public void It_never_switches_itself_off_and_says_its_position_when_switched_on_and_off()
    {
        var changes = new List<bool>();
        _click.ArmedChanged += changes.Add;
        _click.SetArmed(true);

        _click.Tick(T0.AddHours(3));
        Assert.True(_click.IsArmed);

        _click.SetArmed(false);
        Assert.Equal(new[] { true, false }, changes);
        Assert.Equal(new[] { "Auto click on, position 3", "Auto click off" }, _spoken.Select(s => s.AudioText));
    }

    [Fact]
    public void A_position_change_while_on_is_said_aloud()
    {
        _click.SetArmed(true);
        _chain.SetPositions(new[] { new KeyValuePair<string, int>("Stackprime", 6) });
        _chain.SetPositions(new[] { new KeyValuePair<string, int>("Stackprime", 6) }); // the same again: nothing said
        Assert.Equal(new[] { "Auto click on, position 3", "Auto click now on position 6" }, _spoken.Select(s => s.AudioText));
    }

    [Fact]
    public void Switching_it_on_when_it_is_already_on_changes_nothing()
    {
        var changes = new List<bool>();
        _click.ArmedChanged += changes.Add;

        _click.SetArmed(true);
        _click.SetArmed(true);

        Assert.Single(changes);
        Assert.Single(_spoken);
    }

    [Fact]
    public void Moving_from_position_3_to_6_while_on_clicks_for_6_and_not_3()
    {
        _click.SetArmed(true);
        CueAt(T0);
        Assert.Equal(1, _sender.ClickCount);

        _chain.SetPositions(new[] { new KeyValuePair<string, int>("Stackprime", 6) });
        CueAt(T0.AddSeconds(30)); // 3-3-3
        Assert.Equal(1, _sender.ClickCount);

        CueAt(T0.AddSeconds(60), line: "Ream tells the raid, '6-6-6 CAST NOW!'");
        Assert.Equal(2, _sender.ClickCount);
    }
}
