using EQOverwatch.App.Presentation;
using Xunit;

namespace EQOverwatch.App.Tests;

public class ResponsiveColumnsPanelTests
{
    [Theory]
    [InlineData(300, 1)]    // too narrow for two: one column
    [InlineData(700, 1)]
    [InlineData(880, 2)]    // 2 x 430 + one gap
    [InlineData(1180, 2)]
    [InlineData(1360, 3)]   // 3 x 430 + two gaps
    [InlineData(2400, 3)]   // capped at MaxColumns
    [InlineData(0, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void The_column_count_follows_the_width(double width, int expected)
    {
        Assert.Equal(expected, ResponsiveColumnsPanel.ColumnCount(width, 430, 3, 12));
    }

    [Fact]
    public void The_column_cap_can_be_changed()
    {
        Assert.Equal(2, ResponsiveColumnsPanel.ColumnCount(5000, 430, 2, 12));
        Assert.Equal(1, ResponsiveColumnsPanel.ColumnCount(5000, 430, 0, 12));
    }

    [Fact]
    public void Cards_go_into_the_shortest_column_in_order()
    {
        // Heights 100, 300, 100, 100, 50: card 0 -> col 0, card 1 -> col 1, card 2 -> col 0 (100 < 300),
        // card 3 -> col 0 (212 < 312), card 4 -> col 0 (324 > 312? no: 324 vs 312, so col 1).
        int[] placement = ResponsiveColumnsPanel.Place(new double[] { 100, 300, 100, 100, 50 }, 2, 12);

        Assert.Equal(new[] { 0, 1, 0, 0, 1 }, placement);
    }

    [Fact]
    public void One_column_keeps_everything_in_order_in_column_zero()
    {
        Assert.Equal(new[] { 0, 0, 0 }, ResponsiveColumnsPanel.Place(new double[] { 10, 20, 30 }, 1, 12));
        Assert.Empty(ResponsiveColumnsPanel.Place(Array.Empty<double>(), 3, 12));
    }
}
