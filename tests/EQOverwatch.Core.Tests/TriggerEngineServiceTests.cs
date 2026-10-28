using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using Xunit;
using static EQOverwatch.Core.Tests.TestData;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public class TriggerEngineServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    private static TriggerDefinition SkinFades() => new()
    {
        Name = "Protective Skin",
        Pattern = @"^Your protective skin fades\.$",
        Actions = TriggerActions.TextOverlay,
        DurationSeconds = 6,
        DisplayText = "${Character}: Protective Skin Faded",
    };

    private static TriggerDefinition CompleteHeal() => new()
    {
        Name = "Complete Heal",
        Pattern = @"^(?<caster>\w+) begins to cast a spell\. <Complete Heal>$",
        Actions = TriggerActions.CountdownTimer,
        DurationSeconds = 10,
        DisplayText = "${caster}: Complete Heal",
    };

    [Fact]
    public void Character_token_resolves_to_the_log_owner()
    {
        var engine = new TriggerEngineService();
        Assert.Empty(engine.SetTriggers(new[] { SkinFades() }));

        engine.OnLine("Stackprime", "Your protective skin fades.", Now);

        ActiveTimerSnapshot timer = Assert.Single(engine.Snapshot.Timers);
        Assert.Equal("Stackprime: Protective Skin Faded", timer.Text);
        Assert.Equal(TimerKind.TextAlert, timer.Kind);
        Assert.Equal("Stackprime", timer.CharacterName);
        Assert.Equal(Now, timer.StartedUtc);
        Assert.Equal(Now.AddSeconds(6), timer.ExpiresUtc);
    }

    [Fact]
    public void Named_capture_groups_and_the_character_token_combine()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[]
        {
            CompleteHeal() with { DisplayText = "${Character} sees ${caster} cast ${Trigger} (${1})" },
        });

        engine.OnLine("Stackprime", "Ream begins to cast a spell. <Complete Heal>", Now);

        ActiveTimerSnapshot timer = Assert.Single(engine.Snapshot.Timers);
        Assert.Equal("Stackprime sees Ream cast Complete Heal (Ream)", timer.Text);
        Assert.Equal(TimerKind.Countdown, timer.Kind);
        Assert.Equal(10.0, timer.DurationSeconds);
    }

    [Fact]
    public void Unknown_tokens_are_left_as_written_but_unmatched_defined_groups_render_empty()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[]
        {
            new TriggerDefinition
            {
                Name = "t",
                Pattern = @"^hello (?<who>\w+)?$",
                DisplayText = "[${nope}] [${who}] [${Character}]",
            },
        });

        engine.OnLine("Oodles", "hello ", Now);

        Assert.Equal("[${nope}] [] [Oodles]", Assert.Single(engine.Snapshot.Timers).Text);
    }

    [Fact]
    public void Same_trigger_on_two_characters_yields_two_timers_but_retriggering_restarts_one()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[] { SkinFades() });

        engine.OnLine("Stackprime", "Your protective skin fades.", Now);
        engine.OnLine("Oodles", "Your protective skin fades.", Now);
        Assert.Equal(new[] { "Oodles: Protective Skin Faded", "Stackprime: Protective Skin Faded" },
            engine.Snapshot.Timers.Select(t => t.Text).OrderBy(t => t));

        long originalId = engine.Snapshot.Timers.Single(t => t.CharacterName == "Stackprime").Id;
        engine.OnLine("Stackprime", "Your protective skin fades.", Now.AddSeconds(3));

        Assert.Equal(2, engine.Snapshot.Timers.Count);
        ActiveTimerSnapshot restarted = engine.Snapshot.Timers.Single(t => t.CharacterName == "Stackprime");
        Assert.NotEqual(originalId, restarted.Id);
        Assert.Equal(Now.AddSeconds(9), restarted.ExpiresUtc);
    }

    [Fact]
    public void Timers_use_the_received_time_and_expired_ones_are_filtered_and_pruned()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[] { SkinFades(), CompleteHeal() });

        engine.OnLine("Stackprime", "Your protective skin fades.", Now);                         // 6s
        engine.OnLine("Stackprime", "Ream begins to cast a spell. <Complete Heal>", Now);        // 10s

        Assert.Equal(2, engine.Snapshot.GetActive(Now.AddSeconds(5)).Count);
        ActiveTimerSnapshot heal = Assert.Single(engine.Snapshot.GetActive(Now.AddSeconds(7)));
        Assert.Equal(TimerKind.Countdown, heal.Kind);
        Assert.Equal(0.3, heal.FractionRemaining(Now.AddSeconds(7)), 3);
        Assert.Equal(3.0, heal.RemainingSeconds(Now.AddSeconds(7)), 3);
        Assert.Empty(engine.Snapshot.GetActive(Now.AddSeconds(10)));

        // A later firing prunes everything that has expired by then.
        engine.OnLine("Oodles", "Your protective skin fades.", Now.AddSeconds(30));
        Assert.Equal("Oodles", Assert.Single(engine.Snapshot.Timers).CharacterName);
    }

    [Fact]
    public void Disabled_filtered_and_non_matching_triggers_do_not_fire()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[]
        {
            SkinFades() with { IsEnabled = false },
            CompleteHeal() with { Characters = new[] { "oodles" } }, // case-insensitive character filter
        });

        engine.OnLine("Stackprime", "Your protective skin fades.", Now);
        engine.OnLine("Stackprime", "Ream begins to cast a spell. <Complete Heal>", Now);
        engine.OnLine("Oodles", "Something unrelated happens.", Now);
        Assert.Empty(engine.Snapshot.Timers);

        engine.OnLine("Oodles", "Ream begins to cast a spell. <Complete Heal>", Now);
        Assert.Single(engine.Snapshot.Timers);
    }

    [Fact]
    public void Invalid_patterns_are_reported_and_do_not_disable_the_other_triggers()
    {
        var engine = new TriggerEngineService();
        var bad = new TriggerDefinition { Name = "bad", Pattern = "(unclosed" };
        var empty = new TriggerDefinition { Name = "empty", Pattern = " " };

        IReadOnlyList<TriggerCompileError> errors = engine.SetTriggers(new[] { bad, SkinFades(), empty });

        Assert.Equal(new[] { "bad", "empty" }, errors.Select(e => e.Trigger.Name));
        engine.OnLine("Stackprime", "Your protective skin fades.", Now);
        Assert.Single(engine.Snapshot.Timers);
    }

    [Fact]
    public void Audio_actions_raise_an_event_with_rendered_text_and_create_no_timer()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[]
        {
            CompleteHeal() with
            {
                Actions = TriggerActions.Audio,
                AudioText = "${caster} is casting complete heal",
                AudioFilePath = "ding.wav",
            },
        });

        var fired = new List<TriggerFiredEvent>();
        engine.TriggerFired += fired.Add;

        engine.OnLine("Stackprime", "Ream begins to cast a spell. <Complete Heal>", Now);

        TriggerFiredEvent e = Assert.Single(fired);
        Assert.Equal("Ream is casting complete heal", e.AudioText);
        Assert.Equal("ding.wav", e.AudioFilePath);
        Assert.Equal("Stackprime", e.CharacterName);
        Assert.Equal(Now, e.ReceivedUtc);
        Assert.Empty(engine.Snapshot.Timers);
    }

    [Fact]
    public void A_throwing_handler_is_reported_and_does_not_break_the_engine()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[] { SkinFades() });
        var errors = new List<Exception>();
        engine.Error += errors.Add;
        engine.TriggerFired += _ => throw new InvalidOperationException("boom");

        engine.OnLine("Stackprime", "Your protective skin fades.", Now);

        Assert.Single(errors);
        Assert.Single(engine.Snapshot.Timers);
    }

    [Fact]
    public void Non_positive_duration_falls_back_to_the_default()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[] { SkinFades() with { DurationSeconds = 0 } });

        engine.OnLine("Stackprime", "Your protective skin fades.", Now);

        Assert.Equal(Now.AddSeconds(TriggerDefinition.DefaultDurationSeconds), Assert.Single(engine.Snapshot.Timers).ExpiresUtc);
    }

    [Fact]
    public void ClearTimers_empties_the_snapshot()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[] { SkinFades() });
        engine.OnLine("Stackprime", "Your protective skin fades.", Now);

        engine.ClearTimers();

        Assert.Empty(engine.Snapshot.Timers);
    }

    [Fact]
    public async Task Sample_trigger_lines_flow_through_the_real_consumer_without_touching_dps()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new[] { SkinFades(), CompleteHeal() });
        var dps = new RecordingDpsSink();

        await RunLinesAsync(SampleLog, Self, LogRoute.Triggers, engine, dps, receivedUtc: Now);

        Assert.Empty(dps.Events); // Triggers-only route never reaches the DPS sink
        Assert.Equal(
            new[] { "Ream: Complete Heal", "Stackprime: Protective Skin Faded" },
            engine.Snapshot.Timers.Select(t => t.Text).OrderBy(t => t));
        Assert.All(engine.Snapshot.Timers, t => Assert.Equal(Now, t.StartedUtc));
    }
}
