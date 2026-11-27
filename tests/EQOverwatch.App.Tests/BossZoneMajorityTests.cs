using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Models;
using Xunit;

namespace EQOverwatch.App.Tests;

public sealed class BossZoneMajorityTests
{
    private static EncounterSnapshot Fight(string mob, string zone, DateTime start) =>
        new(1, mob, start, start.AddMinutes(5), 300, 1000, false, Array.Empty<ParticipantSnapshot>()) { Zone = zone };

    [Fact]
    public void A_boss_is_placed_where_most_of_its_fights_were_not_where_the_newest_one_was_tagged()
    {
        var day = new DateTime(2026, 11, 1, 20, 0, 0);
        var fights = new[]
        {
            Fight("Lord Inquisitor Seru", "Sanctus Seru", day),
            Fight("Lord Inquisitor Seru", "Sanctus Seru", day.AddDays(7)),
            Fight("Lord Inquisitor Seru", "The Nexus", day.AddDays(14)),
            Fight("Lady Vox", "Permafrost Caverns", day),
            Fight("Lady Vox", "Everfrost Peaks", day.AddDays(1)), // a tie goes to the newest fight
        };

        var zones = MainViewModel.MostFoughtZones(fights).ToDictionary(z => z.Mob, z => z);

        Assert.Equal("Sanctus Seru", zones["Lord Inquisitor Seru"].Zone);
        Assert.Equal(day.AddDays(14), zones["Lord Inquisitor Seru"].Start);
        Assert.Equal("Everfrost Peaks", zones["Lady Vox"].Zone);
    }
}
