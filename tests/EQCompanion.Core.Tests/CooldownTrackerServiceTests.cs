using EQCompanion.Core.Models;
using EQCompanion.Core.Services;
using Xunit;

namespace EQCompanion.Core.Tests;

public sealed class CooldownTrackerServiceTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    private readonly TriggerEngineService _engine = new();
    private readonly CooldownTrackerService _tracker;

    public CooldownTrackerServiceTests() => _tracker = new CooldownTrackerService(_engine);

    private void Line(string message, DateTime at, string character = "Ream") => _tracker.OnLine(character, message, at);

    private IReadOnlyList<ActiveTimerSnapshot> Timers => _engine.Snapshot.Timers;

    [Fact]
    public void The_spell_starts_a_fifteen_minute_cooldown()
    {
        Line(CooldownTrackerService.BeginCastingDivineAura, T0);
        Line(CooldownTrackerService.DivineAuraLanded, T0.AddSeconds(0.3));

        ActiveTimerSnapshot bar = Assert.Single(Timers);
        Assert.Equal("Spell: Divine Aura (15:00 CD)", bar.Text);
        Assert.Equal(900, bar.DurationSeconds);
        Assert.Equal(TimerOverlayCategory.RaidTriggers, bar.Category);
        Assert.Equal("Ream", bar.CharacterName);
    }

    [Fact]
    public void The_bestowed_aa_starts_a_seventy_two_minute_cooldown()
    {
        Line(CooldownTrackerService.ActivateBestowedDivineAura, T0);
        Line(CooldownTrackerService.DivineAuraLanded, T0.AddSeconds(0.2));

        ActiveTimerSnapshot bar = Assert.Single(Timers);
        Assert.Equal("AA: Bestowed Divine Aura (72:00 CD)", bar.Text);
        Assert.Equal(72 * 60, bar.DurationSeconds);
    }

    [Fact]
    public void Without_an_announcement_the_bestowed_aa_is_assumed()
    {
        Line(CooldownTrackerService.DivineAuraLanded, T0);

        Assert.Equal(CooldownTrackerService.BestowedDivineAuraLabel, Assert.Single(Timers).Text);
    }

    [Fact]
    public void Without_an_announcement_while_bestowed_is_cooling_down_it_is_the_regular_aa()
    {
        Line(CooldownTrackerService.ActivateBestowedDivineAura, T0);
        Line(CooldownTrackerService.DivineAuraLanded, T0.AddSeconds(0.2));

        Line(CooldownTrackerService.DivineAuraLanded, T0.AddMinutes(20));

        Assert.Contains(Timers, t => t.Text == CooldownTrackerService.AaDivineAuraLabel);
        Assert.Equal(2, Timers.Count);
    }

    [Fact]
    public void An_old_announcement_is_not_used_for_a_later_landing()
    {
        Line(CooldownTrackerService.BeginCastingDivineAura, T0);
        Line(CooldownTrackerService.DivineAuraLanded, T0.AddSeconds(10)); // far outside 2.5s

        Assert.Equal(CooldownTrackerService.BestowedDivineAuraLabel, Assert.Single(Timers).Text);
    }

    [Fact]
    public void A_disabled_tracker_does_nothing()
    {
        _tracker.IsEnabled = false;

        Line(CooldownTrackerService.DivineAuraLanded, T0);

        Assert.Empty(Timers);
    }

    [Fact]
    public void The_built_in_rules_describe_the_three_cooldowns()
    {
        Assert.Equal(3, CooldownTrackerService.BuiltInRules.Count);
        Assert.Contains(CooldownTrackerService.BuiltInRules, r => r.CooldownText == "15:00");
        Assert.Contains(CooldownTrackerService.BuiltInRules, r => r.CooldownText == "72:00");
    }
}
