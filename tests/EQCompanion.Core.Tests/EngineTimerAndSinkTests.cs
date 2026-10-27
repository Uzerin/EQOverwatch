using EQCompanion.Core.Models;
using EQCompanion.Core.Pipeline;
using EQCompanion.Core.Services;
using Xunit;

namespace EQCompanion.Core.Tests;

public class EngineTimerAndSinkTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Source = Guid.NewGuid();

    [Fact]
    public void StartTimer_adds_a_countdown_and_restarting_it_resets_the_same_bar()
    {
        var engine = new TriggerEngineService();

        engine.StartTimer(Source, "Buffs", "Stackprime", "Stackprime: Spirit of Wolf", 900, Now);
        ActiveTimerSnapshot first = Assert.Single(engine.Snapshot.Timers);
        Assert.Equal(TimerKind.Countdown, first.Kind);
        Assert.Equal("Buffs", first.TriggerName);
        Assert.Equal("Stackprime", first.CharacterName);
        Assert.Equal(Now.AddSeconds(900), first.ExpiresUtc);

        engine.StartTimer(Source, "Buffs", "Stackprime", "Stackprime: Spirit of Wolf", 900, Now.AddSeconds(100));
        ActiveTimerSnapshot reset = Assert.Single(engine.Snapshot.Timers);
        Assert.NotEqual(first.Id, reset.Id);
        Assert.Equal(Now.AddSeconds(1000), reset.ExpiresUtc);

        engine.StartTimer(Source, "Buffs", "Oodles", "Stackprime: Spirit of Wolf", 900, Now); // different subject => different bar
        Assert.Equal(2, engine.Snapshot.Timers.Count);
    }

    [Fact]
    public void StartTimer_ignores_non_positive_durations_and_prunes_expired_bars()
    {
        var engine = new TriggerEngineService();
        engine.StartTimer(Source, "Buffs", "A", "short", 5, Now);

        engine.StartTimer(Source, "Buffs", "B", "never", 0, Now);
        engine.StartTimer(Source, "Buffs", "B", "never", -3, Now);
        Assert.Single(engine.Snapshot.Timers);

        engine.StartTimer(Source, "Buffs", "C", "later", 60, Now.AddSeconds(30)); // the 5s bar has expired by then
        Assert.Equal("later", Assert.Single(engine.Snapshot.Timers).Text);
    }

    [Fact]
    public void CancelTimer_removes_only_the_named_bar_and_reports_whether_it_existed()
    {
        var engine = new TriggerEngineService();
        engine.StartTimer(Source, "Buffs", "Stackprime", "one", 60, Now);
        engine.StartTimer(Source, "Buffs", "Stackprime", "two", 60, Now);
        long version = engine.Snapshot.Version;

        Assert.False(engine.CancelTimer(Source, "Stackprime", "missing"));
        Assert.False(engine.CancelTimer(Guid.NewGuid(), "Stackprime", "one"));
        Assert.Equal(version, engine.Snapshot.Version); // nothing changed, nothing published

        Assert.True(engine.CancelTimer(Source, "Stackprime", "one"));
        Assert.Equal("two", Assert.Single(engine.Snapshot.Timers).Text);
        Assert.True(engine.Snapshot.Version > version);
    }

    private sealed class RecordingSink : ITriggerLineSink
    {
        public List<string> Lines { get; } = new();

        public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
            Lines.Add($"{characterName}:{message}");
    }

    private sealed class ThrowingSink : ITriggerLineSink
    {
        public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public void The_composite_sink_delivers_to_every_sink_even_when_one_throws()
    {
        var before = new RecordingSink();
        var after = new RecordingSink();
        var errors = new List<Exception>();
        var composite = new CompositeTriggerSink(new ITriggerLineSink[] { before, new ThrowingSink(), after }, errors.Add);

        composite.OnLine("Stackprime", "hello", Now);

        Assert.Equal("Stackprime:hello", Assert.Single(before.Lines));
        Assert.Equal("Stackprime:hello", Assert.Single(after.Lines));
        Assert.Equal("boom", Assert.Single(errors).Message);
    }
}
