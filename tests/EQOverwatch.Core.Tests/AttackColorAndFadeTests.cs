using EQOverwatch.Core.Export;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public sealed class AttackColorAndFadeTests
{
    private const string Giant = "a frost giant";

    private static CombatEvent Hit(string source, string verb, int amount, string time, bool crit = false) =>
        new(At(time), CombatEventKind.Damage, source, Giant, amount, DamageKind.Melee, verb, crit);

    // ───────────────────────── attack types ─────────────────────────

    [Theory]
    [InlineData("hit", AttackType.Hit)]
    [InlineData("slashes", AttackType.Slash)]
    [InlineData("pierce", AttackType.Pierce)]
    [InlineData("crushes", AttackType.Crush)]
    [InlineData("punch", AttackType.HandToHand)]
    [InlineData("kick", AttackType.Kick)]
    [InlineData("round kick", AttackType.Kick)]
    [InlineData("bash", AttackType.Bash)]
    [InlineData("backstabs", AttackType.Backstab)]
    [InlineData("frenzy on", AttackType.Frenzy)]
    [InlineData("shoots", AttackType.Archery)]
    [InlineData("bites", AttackType.OtherMelee)]
    public void Melee_verbs_map_to_attack_types(string verb, AttackType expected) =>
        Assert.Equal(expected, AttackTypes.Classify(DamageKind.Melee, verb));

    [Fact]
    public void Spells_dots_and_procs_are_one_type()
    {
        Assert.Equal(AttackType.Spell, AttackTypes.Classify(DamageKind.NonMelee, null));
        Assert.Equal(AttackType.Spell, AttackTypes.Classify(DamageKind.DamageOverTime, "Flame Lick"));
        Assert.Equal(AttackType.Spell, AttackTypes.Classify(DamageKind.NonMelee, "hit"));
    }

    [Fact]
    public void Each_players_damage_is_split_by_attack_type_and_crit()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(Hit("Ream", "slashes", 100, "10:00:00"));
        tracker.OnCombatEvent(Hit("Ream", "slashes", 300, "10:00:02", crit: true));
        tracker.OnCombatEvent(Hit("Ream", "kicks", 50, "10:00:03"));
        tracker.OnCombatEvent(new CombatEvent(At("10:00:04"), CombatEventKind.Damage, "Ream", Giant, 70, DamageKind.NonMelee, null));

        ParticipantSnapshot ream = Assert.Single(Assert.Single(tracker.Snapshot.Active).Participants);
        IReadOnlyList<AttackSlice> slices = Assert.IsAssignableFrom<IReadOnlyList<AttackSlice>>(ream.Attacks);
        Assert.Equal(3, slices.Count);

        AttackSlice slash = slices.Single(s => s.Type == AttackType.Slash);
        Assert.Equal(100, slash.Normal);
        Assert.Equal(300, slash.Crit);
        Assert.Equal(50, slices.Single(s => s.Type == AttackType.Kick).Normal);
        Assert.Equal(70, slices.Single(s => s.Type == AttackType.Spell).Normal);
        Assert.Equal(ream.TotalDamage, slices.Sum(s => s.Total));
        Assert.Equal(AttackType.Slash, slices[0].Type); // largest first

        EncounterExportRow row = Assert.Single(EncounterExport.BuildRows(tracker.Snapshot.Active[0]));
        Assert.Equal(slices.Count, row.Attacks!.Count);
    }

    // ───────────────────────── colors ─────────────────────────

    [Fact]
    public void Colors_default_then_follow_the_users_choice_and_crits_are_lighter()
    {
        var scheme = new AttackColorScheme(new Dictionary<string, string> { ["Slash"] = "#0000FF", ["Kick"] = "not a color" });

        Assert.Equal("#0000FF", scheme.Normal(AttackType.Slash));
        Assert.Equal(AttackTypes.DefaultColor(AttackType.Kick), scheme.Normal(AttackType.Kick)); // bad value ignored
        Assert.Equal(AttackTypes.DefaultColor(AttackType.Bash), scheme.Normal(AttackType.Bash));

        // 45% of the way to white: blue (0,0,255) becomes (115,115,255).
        Assert.Equal("#7373FF", scheme.Crit(AttackType.Slash));
    }

    [Fact]
    public void Every_attack_type_has_its_own_default_color()
    {
        string[] colors = AttackTypes.All.Select(AttackTypes.DefaultColor).ToArray();
        Assert.Equal(colors.Length, colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ───────────────────────── fading ─────────────────────────

    private static readonly DateTime Now = new(2026, 11, 5, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Overlays_are_fully_shown_during_and_just_after_combat()
    {
        Assert.Equal(1.0, OverlayFade.Opacity(Now.AddSeconds(-3), Now, 30, 30, isDpsOverlay: false, unlocked: false));
        Assert.Equal(1.0, OverlayFade.Opacity(Now.AddSeconds(-10), Now, 30, 30, isDpsOverlay: true, unlocked: false));
    }

    [Fact]
    public void Overlays_fade_to_the_chosen_percent_when_combat_stops()
    {
        Assert.Equal(0.3, OverlayFade.Opacity(Now.AddSeconds(-20), Now, 30, 30, isDpsOverlay: false, unlocked: false), 3);
        Assert.Equal(0.3, OverlayFade.Opacity(Now.AddSeconds(-20), Now, 30, 30, isDpsOverlay: true, unlocked: false), 3);
        Assert.Equal(0.3, OverlayFade.Opacity(null, Now, 30, 30, isDpsOverlay: false, unlocked: false), 3);
        Assert.Equal(0.1, OverlayFade.Opacity(null, Now, 0, 30, isDpsOverlay: false, unlocked: false), 3); // never fully invisible
        Assert.Equal(1.0, OverlayFade.Opacity(null, Now, 100, 30, isDpsOverlay: false, unlocked: false));
    }

    [Fact]
    public void The_dps_overlay_disappears_after_the_hide_timeout_and_comes_back_on_combat()
    {
        Assert.Equal(0.0, OverlayFade.Opacity(Now.AddSeconds(-31), Now, 100, 30, isDpsOverlay: true, unlocked: false));
        Assert.Equal(0.0, OverlayFade.Opacity(null, Now, 100, 30, isDpsOverlay: true, unlocked: false));
        Assert.Equal(1.0, OverlayFade.Opacity(Now.AddSeconds(-1), Now, 100, 30, isDpsOverlay: true, unlocked: false));
        Assert.Equal(1.0, OverlayFade.Opacity(Now.AddSeconds(-31), Now, 100, 0, isDpsOverlay: true, unlocked: false)); // 0 = never hide
        Assert.Equal(1.0, OverlayFade.Opacity(Now.AddSeconds(-31), Now, 100, 30, isDpsOverlay: false, unlocked: false)); // other overlays stay
    }

    [Fact]
    public void Unlocked_overlays_are_always_fully_shown()
    {
        Assert.Equal(1.0, OverlayFade.Opacity(null, Now, 20, 30, isDpsOverlay: true, unlocked: true));
    }

    [Fact]
    public void Any_live_combat_line_counts_but_a_history_import_does_not()
    {
        var clock = Now;
        var tracker = new LiveDpsTracker { WallClock = () => clock };
        Assert.Null(tracker.LastCombatUtc);

        tracker.OnCombatEvent(new CombatEvent(At("10:00:00"), CombatEventKind.Miss, Giant, "Ream", 0, DamageKind.None, "hit"));
        Assert.Equal(Now, tracker.LastCombatUtc);

        clock = Now.AddMinutes(5);
        tracker.BeginBatch();
        tracker.OnCombatEvent(Hit("Ream", "slashes", 100, "10:00:01"));
        tracker.EndBatch();
        Assert.Equal(Now, tracker.LastCombatUtc);

        tracker.OnCombatEvent(Hit("Ream", "slashes", 100, "10:00:02"));
        Assert.Equal(Now.AddMinutes(5), tracker.LastCombatUtc);
    }
}

public sealed class ExportHitsTests
{
    [Fact]
    public void The_picture_table_has_a_hits_column_after_total_with_each_players_hit_count()
    {
        var tracker = new EQOverwatch.Core.Services.LiveDpsTracker();
        for (int i = 0; i < 3; i++)
        {
            tracker.OnCombatEvent(new CombatEvent(
                TestHelpers.At($"10:00:0{i}"), CombatEventKind.Damage, "Ream", "a frost giant", 400, DamageKind.Melee, "slashes"));
        }

        Assert.Equal("Total", EQOverwatch.Core.Export.EncounterExport.ColumnHeaders[3]);
        Assert.Equal("Hits", EQOverwatch.Core.Export.EncounterExport.ColumnHeaders[4]);
        var row = Assert.Single(EQOverwatch.Core.Export.EncounterExport.BuildRows(tracker.Snapshot.Active[0]));
        Assert.Equal("3", row.Hits);
    }
}
