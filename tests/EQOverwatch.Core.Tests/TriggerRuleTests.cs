using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class TriggerRuleTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_default_rule_becomes_a_single_text_alert()
    {
        var rule = new TriggerRule { Name = "Skin", Pattern = "fades" };

        TriggerDefinition def = Assert.Single(rule.ToDefinitions());

        Assert.Equal(rule.Id, def.Id);
        Assert.Equal(TriggerActions.TextOverlay, def.Actions);
        Assert.Equal(TriggerRule.DefaultTemplate, def.DisplayText);
        Assert.Equal("fades", def.Pattern);
    }

    [Fact]
    public void A_rule_with_alert_timer_and_audio_maps_to_two_definitions()
    {
        var rule = new TriggerRule
        {
            Name = "Heal", Pattern = @"^(?<caster>\w+) casts$",
            AlertTextTemplate = "${caster} casts!", AudioText = "heal incoming", AudioFilePath = "",
            TimerDurationSeconds = 10, TimerLabelTemplate = "${caster}: CH",
        };

        IReadOnlyList<TriggerDefinition> defs = rule.ToDefinitions();

        Assert.Equal(2, defs.Count);
        TriggerDefinition alert = defs[0];
        Assert.Equal(TriggerActions.TextOverlay | TriggerActions.Audio, alert.Actions);
        Assert.Equal("${caster} casts!", alert.DisplayText);
        Assert.Equal("heal incoming", alert.AudioText);
        Assert.Null(alert.AudioFilePath);

        TriggerDefinition timer = defs[1];
        Assert.Equal(TriggerActions.CountdownTimer, timer.Actions);
        Assert.Equal(10, timer.DurationSeconds);
        Assert.Equal("${caster}: CH", timer.DisplayText);
        Assert.NotEqual(alert.Id, timer.Id);
        Assert.Equal(timer.Id, new TriggerRule { Id = rule.Id, TimerDurationSeconds = 1 }.ToDefinitions().Single(d => d.Actions == TriggerActions.CountdownTimer).Id);
    }

    [Fact]
    public void A_timer_only_rule_has_no_alert_definition()
    {
        var rule = new TriggerRule { Pattern = "x", AlertTextTemplate = "", TimerDurationSeconds = 5 };

        TriggerDefinition def = Assert.Single(rule.ToDefinitions());

        Assert.Equal(TriggerActions.CountdownTimer, def.Actions);
    }

    [Fact]
    public void An_audio_only_rule_has_an_audio_definition_and_no_text_overlay()
    {
        var rule = new TriggerRule { Pattern = "x", AlertTextTemplate = "", AudioFilePath = "ding.wav" };

        TriggerDefinition def = Assert.Single(rule.ToDefinitions());

        Assert.Equal(TriggerActions.Audio, def.Actions);
        Assert.Equal("ding.wav", def.AudioFilePath);
    }

    [Fact]
    public void Plain_text_rules_are_escaped_and_match_anywhere_in_the_line()
    {
        var rule = new TriggerRule { Pattern = "a.b(c)", UseRegex = false };
        var engine = new TriggerEngineService();
        Assert.Empty(engine.SetTriggers(rule.ToDefinitions()));

        engine.OnLine("Stackprime", "xx a.b(c) yy", Now);
        Assert.Single(engine.Snapshot.Timers);

        engine.ClearTimers();
        engine.OnLine("Stackprime", "xx aXb(c) yy", Now); // '.' is literal, not "any character"
        Assert.Empty(engine.Snapshot.Timers);
    }

    [Fact]
    public void Disabled_rules_produce_disabled_definitions()
    {
        var engine = new TriggerEngineService();
        engine.SetTriggers(new TriggerRule { Pattern = "x", IsEnabled = false }.ToDefinitions());

        engine.OnLine("Stackprime", "x", Now);

        Assert.Empty(engine.Snapshot.Timers);
    }

    [Fact]
    public void The_tester_reports_matches_and_capture_groups()
    {
        var rule = new TriggerRule { Pattern = @"^(?<caster>\w+) begins to cast a spell\. <(?<spell>[^>]+)>$" };

        TriggerTestResult hit = TriggerRuleTester.Evaluate(rule, "Ream begins to cast a spell. <Complete Heal>");
        Assert.True(hit.IsPatternValid);
        Assert.True(hit.Matched);
        Assert.Equal("Ream", hit.Groups["caster"]);
        Assert.Equal("Complete Heal", hit.Groups["spell"]);

        TriggerTestResult miss = TriggerRuleTester.Evaluate(rule, "something else");
        Assert.True(miss.IsPatternValid);
        Assert.False(miss.Matched);
        Assert.Empty(miss.Groups);
    }

    [Fact]
    public void The_tester_reports_invalid_and_empty_patterns()
    {
        TriggerTestResult bad = TriggerRuleTester.Evaluate(new TriggerRule { Pattern = "(unclosed" }, "x");
        Assert.False(bad.IsPatternValid);
        Assert.NotNull(bad.Error);

        TriggerTestResult empty = TriggerRuleTester.Evaluate(new TriggerRule { Pattern = "" }, "x");
        Assert.False(empty.IsPatternValid);

        // Plain text is escaped, so the same characters are valid there.
        Assert.True(TriggerRuleTester.Evaluate(new TriggerRule { Pattern = "(unclosed", UseRegex = false }, "x (unclosed").Matched);
    }

    [Fact]
    public void Engine_Test_fires_ad_hoc_definitions_without_changing_the_active_set()
    {
        var engine = new TriggerEngineService();
        var fired = new List<TriggerFiredEvent>();
        engine.TriggerFired += fired.Add;
        var rule = new TriggerRule
        {
            Name = "Heal", Pattern = @"^(?<caster>\w+) casts$", AlertTextTemplate = "${Character} sees ${caster}",
            AudioText = "${caster} is casting",
        };

        IReadOnlyList<TriggerFiredEvent> result = engine.Test(rule.ToDefinitions(), "Stackprime", "Ream casts", Now);

        TriggerFiredEvent e = Assert.Single(result);
        Assert.Equal("Stackprime sees Ream", e.DisplayText);
        Assert.Equal("Ream is casting", e.AudioText);
        Assert.Single(fired);
        Assert.Equal("Stackprime sees Ream", Assert.Single(engine.Snapshot.Timers).Text);

        engine.ClearTimers();
        engine.OnLine("Stackprime", "Ream casts", Now); // the active set is still empty
        Assert.Empty(engine.Snapshot.Timers);
    }

    [Fact]
    public void A_sound_file_only_trigger_does_not_also_speak_its_display_text()
    {
        var engine = new TriggerEngineService();
        var fired = new List<TriggerFiredEvent>();
        engine.TriggerFired += fired.Add;

        engine.Test(new TriggerRule { Pattern = "x", AlertTextTemplate = "", AudioFilePath = "ding.wav" }.ToDefinitions(),
            "Stackprime", "x", Now);

        TriggerFiredEvent e = Assert.Single(fired);
        Assert.Equal("ding.wav", e.AudioFilePath);
        Assert.Null(e.AudioText);
    }
}
