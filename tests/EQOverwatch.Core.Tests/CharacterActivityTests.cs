using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public sealed class CharacterActivityTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_character_is_active_for_sixty_seconds_after_its_last_line()
    {
        var activity = new CharacterActivityService();
        Assert.False(activity.AnyActive(T0));

        activity.OnLogLine("Stackprime", T0);
        Assert.True(activity.IsActive("Stackprime", T0.AddSeconds(60)));
        Assert.True(activity.IsActive("stackprime", T0.AddSeconds(10))); // names are not case sensitive
        Assert.False(activity.IsActive("Stackprime", T0.AddSeconds(60.5)));
        Assert.False(activity.IsActive("Ream", T0));
        Assert.True(activity.AnyActive(T0.AddSeconds(30)));
        Assert.False(activity.AnyActive(T0.AddMinutes(5)));

        activity.OnLogLine("Stackprime", T0.AddMinutes(5)); // a new line brings the character back
        Assert.True(activity.IsActive("Stackprime", T0.AddMinutes(5)));
    }

    [Fact]
    public void Combat_counts_only_from_characters_that_are_still_active()
    {
        var activity = new CharacterActivityService();
        activity.OnCombatLine("Ream", T0);                    // Ream fights, then logs off
        activity.OnLogLine("Stackprime", T0.AddSeconds(100)); // Stackprime is active but has not fought

        DateTime now = T0.AddSeconds(110);
        Assert.False(activity.IsActive("Ream", now));
        Assert.True(activity.IsActive("Stackprime", now));
        Assert.Null(activity.LastCombatUtc(now));

        activity.OnCombatLine("Stackprime", T0.AddSeconds(105));
        Assert.Equal(T0.AddSeconds(105), activity.LastCombatUtc(now));
    }

    [Fact]
    public void A_combat_line_also_counts_as_a_log_line()
    {
        var activity = new CharacterActivityService();
        activity.OnCombatLine("Ream", T0);
        Assert.True(activity.IsActive("Ream", T0.AddSeconds(5)));
    }

    [Fact]
    public async Task The_processor_reports_every_line_and_each_combat_line_of_a_character()
    {
        var activity = new CharacterActivityService();
        DateTime received = DateTime.UtcNow;
        await RunLinesAsync(
            new[]
            {
                "[Wed Oct 21 11:45:00 2026] You have entered The Bazaar.",
                "[Wed Oct 21 11:45:01 2026] Stackprime slashes a frost giant for 210 points of damage.",
            },
            "Ream", LogRoute.Dps, dpsSink: new LiveDpsTracker(), receivedUtc: received, activity: activity);

        Assert.True(activity.IsActive("Ream", received));
        Assert.Equal(received, activity.LastCombatUtc(received));
        Assert.False(activity.IsActive("Stackprime", received)); // the attacker named in a line is not the log's character
    }

    [Fact]
    public async Task A_log_with_only_non_combat_lines_is_active_but_has_no_combat()
    {
        var activity = new CharacterActivityService();
        DateTime received = DateTime.UtcNow;
        await RunLinesAsync(
            new[] { "[Wed Oct 21 11:45:00 2026] You have entered The Bazaar." },
            "Ream", LogRoute.Dps, dpsSink: new LiveDpsTracker(), receivedUtc: received, activity: activity);

        Assert.True(activity.AnyActive(received));
        Assert.Null(activity.LastCombatUtc(received));
    }
}
