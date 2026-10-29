using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Spells;
using Xunit;
using static EQOverwatch.Core.Tests.TestData;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public sealed class RaidBossTimerServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    private readonly TriggerEngineService _engine = new();
    private readonly LiveDpsTracker _dps = new();
    private readonly RaidBossTimerService _service;
    private readonly List<TriggerFiredEvent> _spoken = new();

    public RaidBossTimerServiceTests()
    {
        _service = new RaidBossTimerService(_engine, () => _dps.Snapshot, autoTick: false);
        _engine.TriggerFired += _spoken.Add;
        _dps.AddKnownPlayer("Stackprime");
    }

    public void Dispose() => _service.Dispose();

    private static RaidBossTimerRule Rule(
        string mob = "",
        string match = "blasted by a roar",
        int recast = 30,
        string spell = "Dragon Roar",
        TimerOverlayCategory category = TimerOverlayCategory.RaidTriggers,
        string tts = "",
        RaidWarningTiming? timing = null,
        string start = "") => new()
    {
        MobName = mob,
        SpellId = 4242,
        SpellName = spell,
        MatchText = match,
        RecastSeconds = recast,
        OverlayCategory = category,
        WarningTtsText = tts,
        StartTtsText = start,
        WarningTiming = timing,
    };

    private void Line(string message, DateTime at, string character = "Stackprime") => _service.OnLine(character, message, at);

    private IReadOnlyList<ActiveTimerSnapshot> Timers => _engine.Snapshot.Timers;

    // ───────────────────────── matching and scoping ─────────────────────────

    [Fact]
    public void A_rule_without_a_mob_name_matches_any_boss_and_labels_the_bar_with_the_spell()
    {
        _service.SetRules(new[] { Rule() });

        Line("You are blasted by a roar!", T0);

        ActiveTimerSnapshot timer = Assert.Single(Timers);
        Assert.Equal("Dragon Roar", timer.Text);
        Assert.Equal(TimerKind.Countdown, timer.Kind);
        Assert.Equal(30, timer.DurationSeconds);
        Assert.Equal(T0.AddSeconds(30), timer.ExpiresUtc);
        Assert.Equal(TimerOverlayCategory.RaidTriggers, timer.Category);
    }

    [Fact]
    public void Lines_without_the_match_text_do_nothing()
    {
        _service.SetRules(new[] { Rule() });

        Line("Tormax yawns.", T0);
        Line("BLASTED BY A ROAR", T0); // matching is case-sensitive

        Assert.Empty(Timers);
    }

    [Fact]
    public void A_line_that_names_the_mob_fires_only_that_bosss_rule_with_its_own_recast()
    {
        _service.SetRules(new[]
        {
            Rule(mob: "Tormax", recast: 30),
            Rule(mob: "Velketor", recast: 48),
        });

        Line("Tormax roars and Ream is blasted by a roar.", T0);

        ActiveTimerSnapshot timer = Assert.Single(Timers);
        Assert.Equal("Tormax: Dragon Roar", timer.Text);
        Assert.Equal(30, timer.DurationSeconds);

        Line("You see velketor... and Oodles is blasted by a roar.", T0.AddSeconds(5)); // mob names match ignoring case
        Assert.Equal(new[] { "Tormax: Dragon Roar", "Velketor: Dragon Roar" }, Timers.Select(t => t.Text).OrderBy(t => t));
        Assert.Equal(48, Timers.Single(t => t.Text.StartsWith("Velketor", StringComparison.Ordinal)).DurationSeconds);
    }

    [Fact]
    public void A_line_that_does_not_name_the_mob_still_fires_when_the_live_encounter_is_that_mob()
    {
        _service.SetRules(new[]
        {
            Rule(mob: "Tormax", recast: 30),
            Rule(mob: "Velketor", recast: 48),
        });

        Line("You are blasted by a roar!", T0);
        Assert.Empty(Timers); // nobody is being fought and the line names no one

        _dps.OnCombatEvent(Hit("Stackprime", "Velketor", 500, "11:45:01")); // an active encounter against Velketor
        Line("You are blasted by a roar!", T0.AddSeconds(2));

        ActiveTimerSnapshot timer = Assert.Single(Timers);
        Assert.Equal("Velketor: Dragon Roar", timer.Text);
        Assert.Equal(48, timer.DurationSeconds);
    }

    [Fact]
    public void The_encounter_target_is_compared_ignoring_case_and_ended_encounters_do_not_count()
    {
        _service.SetRules(new[] { Rule(mob: "velketor") });

        _dps.OnCombatEvent(Hit("Stackprime", "Velketor", 500, "11:45:01"));
        Line("You are blasted by a roar!", T0);
        Assert.Single(Timers);

        _engine.ClearTimers();
        _dps.OnLogClock(At("11:46:30")); // the encounter times out
        Assert.Empty(_dps.Snapshot.Active);
        Line("You are blasted by a roar!", T0.AddSeconds(10));
        Assert.Empty(Timers);
    }

    [Fact]
    public void Disabled_and_incomplete_rules_are_ignored()
    {
        RaidBossTimerRule disabled = Rule();
        disabled.IsEnabled = false;
        _service.SetRules(new[]
        {
            disabled,
            Rule(match: string.Empty),
            Rule(spell: " "),
            Rule(recast: 0),
            Rule(recast: -5),
        });

        Assert.Empty(_service.Rules);
        Line("You are blasted by a roar!", T0);
        Assert.Empty(Timers);
    }

    [Fact]
    public void The_rules_overlay_category_decides_where_the_bar_goes()
    {
        _service.SetRules(new[]
        {
            Rule(match: "roar", category: TimerOverlayCategory.Debuffs, spell: "Roar Debuff"),
            Rule(match: "chant", category: TimerOverlayCategory.Songs, spell: "Chant"),
        });

        Line("a roar echoes", T0);
        Line("a chant begins", T0);

        Assert.Equal(TimerOverlayCategory.Debuffs, Timers.Single(t => t.Text == "Roar Debuff").Category);
        Assert.Equal(TimerOverlayCategory.Songs, Timers.Single(t => t.Text == "Chant").Category);
    }

    // ───────────────────────── repeat sightings ─────────────────────────

    [Fact]
    public void Seeing_the_ability_again_resets_the_bar()
    {
        _service.SetRules(new[] { Rule() });
        Line("You are blasted by a roar!", T0);
        ActiveTimerSnapshot first = Assert.Single(Timers);

        Line("You are blasted by a roar!", T0.AddSeconds(30));

        ActiveTimerSnapshot reset = Assert.Single(Timers);
        Assert.NotEqual(first.Id, reset.Id);
        Assert.Equal(T0.AddSeconds(60), reset.ExpiresUtc);
    }

    [Fact]
    public void The_same_cast_seen_by_two_watched_logs_counts_once()
    {
        _service.SetRules(new[] { Rule(tts: "Roar!", timing: RaidWarningTiming.AtStart) });

        Line("You are blasted by a roar!", T0, "Stackprime");
        long id = Assert.Single(Timers).Id;
        Line("You are blasted by a roar!", T0.AddMilliseconds(40), "Oodles");

        Assert.Equal(id, Assert.Single(Timers).Id);
        Assert.Single(_spoken); // and is announced once

        Line("You are blasted by a roar!", T0.AddSeconds(1), "Oodles"); // a full second later is a new cast
        Assert.NotEqual(id, Assert.Single(Timers).Id);
    }

    // ───────────────────────── spoken warnings ─────────────────────────

    [Fact]
    public void Legacy_single_speech_is_spoken_when_the_timer_starts_and_when_ten_seconds_remain()
    {
        _service.SetRules(new[] { Rule(mob: "Tormax", recast: 30, tts: "${MobName} will cast ${SpellName}", timing: RaidWarningTiming.Both) });

        Line("Tormax: Ream is blasted by a roar.", T0);
        TriggerFiredEvent atStart = Assert.Single(_spoken);
        Assert.Equal(TriggerActions.Audio, atStart.Actions);
        Assert.Equal("Tormax will cast Dragon Roar", atStart.AudioText);

        _service.Tick(T0.AddSeconds(19.9));
        Assert.Single(_spoken);

        _service.Tick(T0.AddSeconds(20));
        Assert.Equal(2, _spoken.Count);
        Assert.Equal("Tormax will cast Dragon Roar", _spoken[1].AudioText);

        _service.Tick(T0.AddSeconds(26)); // spoken once only
        Assert.Equal(2, _spoken.Count);
    }

    [Theory]
    [InlineData(RaidWarningTiming.AtStart, 1, 0)]
    [InlineData(RaidWarningTiming.FiveSecondsRemaining, 0, 1)] // the old name; the lead time is now 10 s
    [InlineData(RaidWarningTiming.Both, 1, 1)]
    public void A_legacy_warning_timing_setting_is_converted_to_separate_announcements(RaidWarningTiming timing, int atStart, int atTenSeconds)
    {
        _service.SetRules(new[] { Rule(recast: 30, tts: "Roar", timing: timing) });

        Line("You are blasted by a roar!", T0);
        Assert.Equal(atStart, _spoken.Count);

        _service.Tick(T0.AddSeconds(20));
        Assert.Equal(atStart + atTenSeconds, _spoken.Count);
    }

    [Fact]
    public void A_recast_of_ten_seconds_or_less_has_no_separate_ten_second_warning()
    {
        _service.SetRules(new[] { Rule(recast: 10, tts: "Roar", timing: RaidWarningTiming.FiveSecondsRemaining) });

        Line("You are blasted by a roar!", T0);
        _service.Tick(T0.AddSeconds(30));

        Assert.Empty(_spoken);
    }

    [Fact]
    public void A_new_cast_moves_the_pending_ten_second_warning()
    {
        _service.SetRules(new[] { Rule(recast: 30, tts: "Roar", timing: RaidWarningTiming.FiveSecondsRemaining) });
        Line("You are blasted by a roar!", T0);
        Line("You are blasted by a roar!", T0.AddSeconds(10)); // the boss cast early: the next one is due 30s after this

        _service.Tick(T0.AddSeconds(20)); // the original warning time
        Assert.Empty(_spoken);

        _service.Tick(T0.AddSeconds(30));
        Assert.Single(_spoken);
    }

    [Fact]
    public void Removing_a_rule_cancels_its_pending_warning()
    {
        _service.SetRules(new[] { Rule(recast: 30, tts: "Roar", timing: RaidWarningTiming.FiveSecondsRemaining) });
        Line("You are blasted by a roar!", T0);

        _service.SetRules(Array.Empty<RaidBossTimerRule>());
        _service.Tick(T0.AddSeconds(20));

        Assert.Empty(_spoken);
    }

    [Fact]
    public void Rules_without_warning_text_stay_silent()
    {
        _service.SetRules(new[] { Rule(recast: 30) });

        Line("You are blasted by a roar!", T0);
        _service.Tick(T0.AddSeconds(25));

        Assert.Empty(_spoken);
        Assert.Single(Timers);
    }

    [Fact]
    public void The_start_and_ten_second_announcements_are_separate_texts()
    {
        _service.SetRules(new[] { Rule(mob: "Tormax", recast: 30, start: "${MobName} roar now", tts: "${SpellName} in ten") });

        Line("Tormax: You are blasted by a roar!", T0); // a rule with a boss name needs the line to name that boss
        Assert.Equal("Tormax roar now", Assert.Single(_spoken).AudioText);

        _service.Tick(T0.AddSeconds(19.9));
        Assert.Single(_spoken);

        _service.Tick(T0.AddSeconds(20));
        Assert.Equal(2, _spoken.Count);
        Assert.Equal("Dragon Roar in ten", _spoken[1].AudioText);

        _service.Tick(T0.AddSeconds(25)); // each is spoken once
        Assert.Equal(2, _spoken.Count);
    }

    [Fact]
    public void Only_a_start_text_speaks_once_at_the_start_and_only_a_warning_text_speaks_at_ten_seconds()
    {
        _service.SetRules(new[] { Rule(recast: 30, start: "Roar!") });
        Line("You are blasted by a roar!", T0);
        _service.Tick(T0.AddSeconds(25));
        Assert.Equal("Roar!", Assert.Single(_spoken).AudioText);

        _spoken.Clear();
        _service.SetRules(new[] { Rule(recast: 30, tts: "Roar soon") });
        Line("You are blasted by a roar!", T0.AddSeconds(100));
        Assert.Empty(_spoken);
        _service.Tick(T0.AddSeconds(120));
        Assert.Equal("Roar soon", Assert.Single(_spoken).AudioText);
    }

    [Fact]
    public void A_start_text_is_spoken_even_when_the_recast_is_too_short_for_a_ten_second_warning()
    {
        _service.SetRules(new[] { Rule(recast: 8, start: "Roar!", tts: "never spoken") });

        Line("You are blasted by a roar!", T0);
        _service.Tick(T0.AddSeconds(30));

        Assert.Equal("Roar!", Assert.Single(_spoken).AudioText);
    }

    [Fact]
    public void Legacy_timing_conversion_keeps_the_text_where_it_used_to_be_spoken()
    {
        var atStart = new RaidBossTimerRule { WarningTtsText = "Go", WarningTiming = RaidWarningTiming.AtStart };
        var late = new RaidBossTimerRule { WarningTtsText = "Go", WarningTiming = RaidWarningTiming.FiveSecondsRemaining };
        var both = new RaidBossTimerRule { WarningTtsText = "Go", WarningTiming = RaidWarningTiming.Both };
        var current = new RaidBossTimerRule { StartTtsText = "Now", WarningTtsText = "Soon" };

        foreach (RaidBossTimerRule rule in new[] { atStart, late, both, current })
        {
            rule.ApplyLegacyTiming();
            Assert.Null(rule.WarningTiming);
        }

        Assert.Equal(("Go", ""), (atStart.StartTtsText, atStart.WarningTtsText));
        Assert.Equal(("", "Go"), (late.StartTtsText, late.WarningTtsText));
        Assert.Equal(("Go", "Go"), (both.StartTtsText, both.WarningTtsText));
        Assert.Equal(("Now", "Soon"), (current.StartTtsText, current.WarningTtsText));
    }

    // ───────────────────────── rule helpers ─────────────────────────

    [Fact]
    public void Match_text_is_suggested_from_the_casting_line_whatever_the_landing_messages_are()
    {
        var withYou = new SpellDefinition(1, "Dragon Roar", "You are blasted by a roar!", " is blasted by a roar.");
        var noMessages = new SpellDefinition(2, "Knockback", string.Empty, string.Empty);

        Assert.Equal("begins to cast a spell. <Dragon Roar>", RaidBossTimerRule.SuggestMatchText(withYou));
        Assert.Equal("begins to cast a spell. <Knockback>", RaidBossTimerRule.SuggestMatchText(noMessages));
    }

    [Fact]
    public void A_cast_line_starts_the_bar_for_the_named_boss_even_when_nobody_is_hit()
    {
        _service.SetRules(new[] { Rule(mob: "Lord Inquisitor Seru", match: RaidBossTimerRule.CastingText("Dragon Roar")) });

        Line("Lord Inquisitor Seru begins to cast a spell. <Dragon Roar>", T0);
        Line("A Praetorian Medicus begins to cast a spell. <Dragon Roar>", T0.AddSeconds(5)); // another mob: ignored

        ActiveTimerSnapshot timer = Assert.Single(Timers);
        Assert.Equal("Lord Inquisitor Seru: Dragon Roar", timer.Text);
    }

    [Fact]
    public void The_label_omits_the_mob_when_the_rule_applies_to_any_boss()
    {
        Assert.Equal("Tormax: Dragon Roar", new RaidBossTimerRule { MobName = " Tormax ", SpellName = "Dragon Roar" }.Label);
        Assert.Equal("Dragon Roar", new RaidBossTimerRule { SpellName = "Dragon Roar" }.Label);
    }
}

public sealed class RaidBossPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public RaidBossPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Live_log_lines_start_boss_timers_in_the_chosen_overlay_and_imports_do_not()
    {
        string path = Path.Combine(_dir, "eqlog_Stackprime_Frostreaver.txt");
        File.WriteAllText(path, string.Empty);
        await using var pipeline = new CompanionPipeline();
        pipeline.Logs.AddFile(path);
        pipeline.RaidBosses.SetRules(new[]
        {
            new RaidBossTimerRule
            {
                MobName = "Tormax", SpellName = "Dragon Roar", MatchText = "is blasted by a roar", RecastSeconds = 30,
                OverlayCategory = TimerOverlayCategory.Debuffs,
            },
        });

        // A historical import of the same text never starts a timer.
        string history = Path.Combine(_dir, "eqlog_Oodles_Frostreaver.txt");
        File.WriteAllText(history, Line("Tormax roars and Ream is blasted by a roar.") + "\n");
        await pipeline.ImportHistoricalAsync(history);
        Assert.Empty(pipeline.Triggers.Snapshot.Timers);

        pipeline.StartLive();
        try
        {
            await File.AppendAllTextAsync(path, Line("Tormax roars and Ream is blasted by a roar.", "11:45:02") + "\n");
            await WaitUntilAsync(() => pipeline.Triggers.Snapshot.Timers.Count == 1);

            ActiveTimerSnapshot timer = pipeline.Triggers.Snapshot.Timers[0];
            Assert.Equal("Tormax: Dragon Roar", timer.Text);
            Assert.Equal(TimerOverlayCategory.Debuffs, timer.Category);
            Assert.Equal(30, timer.DurationSeconds);
        }
        finally
        {
            await pipeline.StopLiveAsync();
        }
    }
}
