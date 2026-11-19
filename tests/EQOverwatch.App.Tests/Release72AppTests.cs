using System.Globalization;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.History;
using Xunit;

namespace EQOverwatch.App.Tests;

public class Release72AppTests
{
    [Fact]
    public void Item_search_totals_count_characters_and_shared_banks()
    {
        CultureInfo old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var hits = new[]
            {
                new ItemHit("Ream", "General 2", "Frightening Flatbread", 16),
                new ItemHit("Ream box", "Shared bank 1 · slot 1", "Frightening Flatbread", 27),
            };
            Assert.Equal("43 found on 1 character and 1 shared bank.", MainViewModel.ItemTotalText(hits));
            Assert.Equal("2 found on 2 characters.", MainViewModel.ItemTotalText(new[] { new ItemHit("A", "General 1", "X", 1), new ItemHit("B", "Bank 1", "X", 1) }));
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Fact]
    public void Kill_time_trend_and_last_played_are_short()
    {
        BossFight Kill(int day, double seconds) => new("Seru", new DateTime(2026, 11, day, 21, 0, 0), seconds, 1_000_000, 40, 0, true);
        Assert.Equal("–", MainViewModel.KillTrendText(BossHistoryStore.Trend(new[] { Kill(1, 300), Kill(2, 290) })));
        Assert.StartsWith("Faster", MainViewModel.KillTrendText(BossHistoryStore.Trend(new[] { Kill(1, 300), Kill(2, 300), Kill(3, 240), Kill(4, 240) })));
        Assert.StartsWith("Slower", MainViewModel.KillTrendText(BossHistoryStore.Trend(new[] { Kill(1, 200), Kill(2, 200), Kill(3, 260), Kill(4, 260) })));
        Assert.Equal("Steady", MainViewModel.KillTrendText(BossHistoryStore.Trend(new[] { Kill(1, 200), Kill(2, 200), Kill(3, 201), Kill(4, 201) })));

        Assert.Equal("5m ago", MainViewModel.AgoText(TimeSpan.FromMinutes(5)));
        Assert.Equal("3h ago", MainViewModel.AgoText(TimeSpan.FromHours(3.5)));
        Assert.Equal("2d ago", MainViewModel.AgoText(TimeSpan.FromDays(2.2)));
    }
}
