using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>The killing blow on a death: the killer's last hit on the victim, as seen in the log.</summary>
public class KillingBlowTests
{
    private static void Feed(DpsTrackerService tracker, params string[] lines)
    {
        foreach (string line in lines)
        {
            Assert.True(LogLineFormat.TryParseTimestamp(line, out DateTime ts));
            if (CombatLineParser.TryParse(line, ts, "Oodles", out CombatEvent e))
            {
                tracker.OnCombatEvent(e);
            }
        }
    }

    [Fact]
    public void The_killers_last_hit_is_the_killing_blow()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Oodles");
        tracker.AddKnownPlayer("Rumination");
        Feed(tracker,
            "[Mon Nov 16 20:56:58 2026] Oodles punches Klazaz the Slayer for 120 points of damage.",
            "[Mon Nov 16 20:57:01 2026] Klazaz the Slayer punches Rumination for 155 points of damage.",
            "[Mon Nov 16 20:57:01 2026] Grziz the Tormentor punches Rumination for 65 points of damage.",
            "[Mon Nov 16 20:57:02 2026] Klazaz the Slayer punches Rumination for 95 points of damage.",
            "[Mon Nov 16 20:57:02 2026] Klazaz the Slayer punches Rumination for 175 points of damage.",
            "[Mon Nov 16 20:57:02 2026] Rumination has been slain by Klazaz the Slayer!");

        DeathSnapshot death = Assert.Single(Assert.Single(tracker.Snapshot.Active).DeathRows);
        Assert.Equal(("Rumination", "Klazaz the Slayer", "175 punch"), (death.Name, death.KilledBy, death.KillingBlow));
    }

    [Fact]
    public void A_death_with_no_hit_in_the_log_has_no_killing_blow()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Oodles");
        tracker.AddKnownPlayer("Powertart");
        Feed(tracker,
            "[Mon Nov 16 21:23:20 2026] Oodles punches Emperor Ssraeshza  for 120 points of damage.",
            "[Mon Nov 16 21:23:21 2026] Emperor Ssraeshza  punches Powertart for 816 points of damage.",
            "[Mon Nov 16 21:23:21 2026] Oodles punches Emperor Ssraeshza  for 120 points of damage.");
        Feed(tracker, "[Mon Nov 16 21:24:26 2026] Oodles punches Emperor Ssraeshza  for 120 points of damage.");
        Feed(tracker, "[Mon Nov 16 21:24:27 2026] Powertart has been slain by Emperor Ssraeshza !");

        DeathSnapshot death = Assert.Single(tracker.Snapshot.Active.SelectMany(a => a.DeathRows));
        Assert.Equal(string.Empty, death.KillingBlow); // the 816 punch came more than 30 seconds earlier
    }

    [Fact]
    public void A_spell_can_be_the_killing_blow()
    {
        var tracker = new LiveDpsTracker();
        tracker.AddKnownPlayer("Oodles");
        tracker.AddKnownPlayer("Nasti");
        Feed(tracker,
            "[Wed Oct 21 11:45:00 2026] Oodles punches a Praetorian Medicus for 120 points of damage.",
            "[Wed Oct 21 11:45:01 2026] a Praetorian Medicus hit Nasti for 2400 points of magic damage by Judgment.",
            "[Wed Oct 21 11:45:01 2026] Nasti has been slain by a Praetorian Medicus!");

        Assert.Equal("2,400 Judgment", Assert.Single(Assert.Single(tracker.Snapshot.Active).DeathRows).KillingBlow);
    }

    [Theory]
    [InlineData(DamageKind.Melee, "punches", "punch")]
    [InlineData(DamageKind.Melee, "slashes", "slash")]
    [InlineData(DamageKind.Melee, "bites", "bite")]
    [InlineData(DamageKind.Melee, "pierces", "pierce")]
    [InlineData(DamageKind.Melee, "hits", "hit")]
    [InlineData(DamageKind.Melee, "frenzies on", "frenzy")]
    [InlineData(DamageKind.NonMelee, "hits", "non-melee")]
    [InlineData(DamageKind.NonMelee, "Judgment", "Judgment")]
    [InlineData(DamageKind.DamageOverTime, "Venom of the Snake", "Venom of the Snake")]
    [InlineData(DamageKind.DamageShield, "thorns", "damage shield")]
    public void Hits_are_labelled_with_a_short_word(DamageKind kind, string detail, string expected) =>
        Assert.Equal(expected, DpsTrackerService.KillingBlowLabel(kind, detail));
}
