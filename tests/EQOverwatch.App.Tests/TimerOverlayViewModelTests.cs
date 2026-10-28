using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Models;
using Xunit;

namespace EQOverwatch.App.Tests;

public class TimerOverlayViewModelTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    private static ActiveTimerSnapshot Timer(long id, TimerKind kind, string text, DateTime started, double duration) =>
        new(id, Guid.Empty, "trigger", "Stackprime", kind, text, started, started.AddSeconds(duration), duration);

    private static TriggerSnapshot Snap(params ActiveTimerSnapshot[] timers) =>
        new(1, Array.AsReadOnly(timers));

    [Fact]
    public void Text_alerts_flash_for_four_seconds_even_when_the_trigger_lasts_longer()
    {
        DateTime now = T0;
        TriggerSnapshot snap = Snap(Timer(1, TimerKind.TextAlert, "Stackprime: Protective Skin Faded", T0, 10));
        var vm = new TimerOverlayViewModel(() => snap, () => now);

        now = T0.AddSeconds(3.9);
        vm.Tick();
        Assert.Equal("Stackprime: Protective Skin Faded", Assert.Single(vm.Alerts).Text);

        now = T0.AddSeconds(4.0);
        vm.Tick();
        Assert.Empty(vm.Alerts);
        Assert.Empty(vm.Bars);
    }

    [Fact]
    public void A_text_alert_with_a_shorter_duration_disappears_at_its_own_duration()
    {
        DateTime now = T0;
        TriggerSnapshot snap = Snap(Timer(1, TimerKind.TextAlert, "short", T0, 2));
        var vm = new TimerOverlayViewModel(() => snap, () => now);

        now = T0.AddSeconds(1.9);
        vm.Tick();
        Assert.Single(vm.Alerts);

        now = T0.AddSeconds(2.0);
        vm.Tick();
        Assert.Empty(vm.Alerts);
    }

    [Fact]
    public void Newest_alert_is_listed_first()
    {
        DateTime now = T0.AddSeconds(1);
        TriggerSnapshot snap = Snap(
            Timer(1, TimerKind.TextAlert, "older", T0, 10),
            Timer(2, TimerKind.TextAlert, "newer", T0.AddSeconds(0.5), 10));
        var vm = new TimerOverlayViewModel(() => snap, () => now);

        vm.Tick();

        Assert.Equal(new[] { "newer", "older" }, vm.Alerts.Select(a => a.Text));
    }

    [Fact]
    public void Bars_are_sorted_by_expiry_and_show_label_remaining_text_and_fill()
    {
        DateTime now = T0.AddSeconds(2);
        TriggerSnapshot snap = Snap(
            Timer(1, TimerKind.Countdown, "Ream: Complete Heal", T0, 10),
            Timer(2, TimerKind.Countdown, "Stackprime: Spell Haste", T0, 5));
        var vm = new TimerOverlayViewModel(() => snap, () => now);

        vm.Tick();

        Assert.Equal(new[] { "Stackprime: Spell Haste", "Ream: Complete Heal" }, vm.Bars.Select(b => b.Label));

        TimerBarViewModel soonest = vm.Bars[0];
        Assert.Equal("3.0s", soonest.RemainingText);
        Assert.Equal(0.6, soonest.Fraction, 3);
        Assert.True(soonest.IsUrgent); // 3.0s remaining is inside the urgent window

        TimerBarViewModel later = vm.Bars[1];
        Assert.Equal("8.0s", later.RemainingText);
        Assert.Equal(0.8, later.Fraction, 3);
        Assert.False(later.IsUrgent);
    }

    [Fact]
    public void Bar_view_models_are_reused_updated_replaced_on_restart_and_removed_on_expiry()
    {
        DateTime now = T0.AddSeconds(1);
        TriggerSnapshot snap = Snap(Timer(1, TimerKind.Countdown, "heal", T0, 10));
        var vm = new TimerOverlayViewModel(() => snap, () => now);

        vm.Tick();
        TimerBarViewModel first = Assert.Single(vm.Bars);
        Assert.Equal("9.0s", first.RemainingText);

        now = T0.AddSeconds(2);
        vm.Tick();
        Assert.Same(first, Assert.Single(vm.Bars)); // same instance, new values
        Assert.Equal("8.0s", first.RemainingText);

        // The engine restarts a re-triggered timer under a new id: the old bar is replaced.
        snap = Snap(Timer(2, TimerKind.Countdown, "heal", T0.AddSeconds(2), 10));
        vm.Tick();
        TimerBarViewModel restarted = Assert.Single(vm.Bars);
        Assert.NotSame(first, restarted);
        Assert.Equal(1.0, restarted.Fraction, 3);

        now = T0.AddSeconds(12);
        vm.Tick();
        Assert.Empty(vm.Bars);
    }

    [Fact]
    public void Reordering_moves_existing_bars_instead_of_recreating_them()
    {
        DateTime now = T0;
        TriggerSnapshot snap = Snap(
            Timer(1, TimerKind.Countdown, "one", T0, 10),
            Timer(2, TimerKind.Countdown, "two", T0, 20));
        var vm = new TimerOverlayViewModel(() => snap, () => now);

        vm.Tick();
        TimerBarViewModel one = vm.Bars[0];
        TimerBarViewModel two = vm.Bars[1];
        Assert.Equal(new long[] { 1, 2 }, vm.Bars.Select(b => b.Id));

        // Same ids, but now bar 2 expires first.
        snap = Snap(
            Timer(1, TimerKind.Countdown, "one", T0, 30),
            Timer(2, TimerKind.Countdown, "two", T0, 15));
        vm.Tick();

        Assert.Equal(new long[] { 2, 1 }, vm.Bars.Select(b => b.Id));
        Assert.Same(two, vm.Bars[0]);
        Assert.Same(one, vm.Bars[1]);
    }

    private static ActiveTimerSnapshot InCategory(long id, TimerOverlayCategory category, string text, TimerKind kind = TimerKind.Countdown) =>
        new(id, Guid.Empty, "trigger", "Stackprime", kind, text, T0, T0.AddSeconds(60), 60, category);

    [Fact]
    public void Each_overlay_shows_only_the_timers_of_its_own_category()
    {
        DateTime now = T0.AddSeconds(1);
        TriggerSnapshot snap = Snap(
            InCategory(1, TimerOverlayCategory.Buffs, "Stackprime: Haste"),
            InCategory(2, TimerOverlayCategory.Debuffs, "a gnoll: Disease Cloud"),
            InCategory(3, TimerOverlayCategory.Songs, "Stackprime: Selo's"),
            InCategory(4, TimerOverlayCategory.RaidTriggers, "Tormax: Dragon Roar"),
            InCategory(5, TimerOverlayCategory.Buffs, "Oodles: Haste"),
            InCategory(6, TimerOverlayCategory.Songs, "Song alert", TimerKind.TextAlert));

        var overlays = TimerOverlayCategoryExtensions.All.ToDictionary(
            c => c, c => new TimerOverlayViewModel(() => snap, () => now, c));
        foreach (TimerOverlayViewModel vm in overlays.Values)
        {
            vm.Tick();
        }

        Assert.Equal(new[] { "Oodles: Haste", "Stackprime: Haste" }, overlays[TimerOverlayCategory.Buffs].Bars.Select(b => b.Label).OrderBy(t => t));
        Assert.Equal("a gnoll: Disease Cloud", Assert.Single(overlays[TimerOverlayCategory.Debuffs].Bars).Label);
        Assert.Equal("Stackprime: Selo's", Assert.Single(overlays[TimerOverlayCategory.Songs].Bars).Label);
        Assert.Equal("Song alert", Assert.Single(overlays[TimerOverlayCategory.Songs].Alerts).Text); // alerts are routed too
        Assert.Equal("Tormax: Dragon Roar", Assert.Single(overlays[TimerOverlayCategory.RaidTriggers].Bars).Label);

        Assert.Empty(overlays[TimerOverlayCategory.Buffs].Alerts);
        Assert.Equal(TimerOverlayCategory.Debuffs, overlays[TimerOverlayCategory.Debuffs].Category);
        Assert.Equal(TimerOverlayCategory.Songs, Assert.Single(overlays[TimerOverlayCategory.Songs].Bars).Category);
    }

    [Fact]
    public void An_overlay_without_a_category_shows_everything()
    {
        DateTime now = T0.AddSeconds(1);
        TriggerSnapshot snap = Snap(
            InCategory(1, TimerOverlayCategory.Buffs, "a"),
            InCategory(2, TimerOverlayCategory.Debuffs, "b"));
        var vm = new TimerOverlayViewModel(() => snap, () => now);

        vm.Tick();

        Assert.Null(vm.Category);
        Assert.Equal(2, vm.Bars.Count);
    }

    [Fact]
    public void A_timer_that_changes_category_moves_to_the_other_overlay()
    {
        DateTime now = T0.AddSeconds(1);
        TriggerSnapshot snap = Snap(InCategory(1, TimerOverlayCategory.Buffs, "x"));
        var buffs = new TimerOverlayViewModel(() => snap, () => now, TimerOverlayCategory.Buffs);
        var songs = new TimerOverlayViewModel(() => snap, () => now, TimerOverlayCategory.Songs);
        buffs.Tick();
        songs.Tick();
        Assert.Single(buffs.Bars);
        Assert.Empty(songs.Bars);

        snap = Snap(InCategory(2, TimerOverlayCategory.Songs, "x")); // restarted as a song
        buffs.Tick();
        songs.Tick();

        Assert.Empty(buffs.Bars);
        Assert.Single(songs.Bars);
    }

    [Fact]
    public void Alerts_and_bars_are_kept_in_separate_regions()
    {
        DateTime now = T0.AddSeconds(1);
        TriggerSnapshot snap = Snap(
            Timer(1, TimerKind.TextAlert, "alert", T0, 6),
            Timer(2, TimerKind.Countdown, "bar", T0, 6));
        var vm = new TimerOverlayViewModel(() => snap, () => now);

        vm.Tick();

        Assert.Equal("alert", Assert.Single(vm.Alerts).Text);
        Assert.Equal("bar", Assert.Single(vm.Bars).Label);
    }
}
