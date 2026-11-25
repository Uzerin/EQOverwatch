using System.Globalization;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Leveling;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Ding_rows_give_the_time_since_the_level_before()
    {
        long start = new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var levels = new[] { new LevelEvent(start + 3 * 86400, 12), new LevelEvent(start, 10), new LevelEvent(start + 7200, 11) };

        var rows = MainViewModel.DingRowsFor("Porter", levels, CultureInfo.InvariantCulture);

        Assert.Equal(new[] { 10, 11, 12 }, rows.Select(r => r.Level));
        Assert.Equal(string.Empty, rows[0].Gap);
        Assert.Equal("2.0 h", rows[1].Gap);
        Assert.Equal("2.9 days", rows[2].Gap);
    }
}
