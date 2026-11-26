using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V119Tests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 20, 0, 0);

    [Fact]
    public void Raid_boss_fights_are_kept_and_do_not_count_toward_the_limit()
    {
        var tracker = new LiveDpsTracker(maxCompletedEncounters: 3) { KeepAlways = name => name == "Lord Inquisitor Seru" };
        tracker.BeginBatch();
        Fight(tracker, "Lord Inquisitor Seru", T0);
        for (int i = 1; i <= 5; i++)
        {
            Fight(tracker, "a crypt guardian", T0.AddMinutes(i * 10)); // each one a new fight, long after the last
        }

        tracker.EndBatch();
        IReadOnlyList<EncounterSnapshot> kept = tracker.Snapshot.Completed;
        Assert.Equal(4, kept.Count);
        Assert.Equal("Lord Inquisitor Seru", kept[0].TargetName);
        Assert.Equal(T0.AddMinutes(30), kept[1].StartTime); // the oldest two guardians went
    }

    [Fact]
    public void Older_raid_boss_windows_are_joined_and_read_only_before_the_recent_logs()
    {
        var windows = new[]
        {
            (T0.AddMinutes(5), T0.AddMinutes(15)),
            (T0, T0.AddMinutes(6)),
            (T0.AddDays(2), T0.AddDays(2).AddMinutes(10)), // already in the recent part
        };

        Assert.Equal(new[] { (T0, T0.AddMinutes(15)) }, CompanionPipeline.MergeWindows(windows, T0.AddDays(1)));
        Assert.Empty(CompanionPipeline.MergeWindows(windows, null)); // a read of everything has every fight
    }

    private static void Fight(DpsTrackerService tracker, string mob, DateTime start)
    {
        for (int t = 0; t <= 10; t += 5)
        {
            tracker.OnCombatEvent(new CombatEvent(start.AddSeconds(t), CombatEventKind.Damage, "Tankard", mob, 100, DamageKind.Melee, "hit"));
        }

        tracker.OnCombatEvent(new CombatEvent(start.AddSeconds(10), CombatEventKind.Death, "Tankard", mob, 0, DamageKind.None, null));
    }
}
