using EQOverwatch.App.Presentation;
using Xunit;

namespace EQOverwatch.App.Tests;

public class OverlayFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(950, "950")]
    [InlineData(1_000, "1K")]
    [InlineData(1_234, "1.2K")]
    [InlineData(12_345, "12.3K")]
    [InlineData(123_456, "123K")]
    [InlineData(999_499, "999K")]
    [InlineData(999_500, "1M")]
    [InlineData(1_234_567, "1.23M")]
    [InlineData(12_345_678, "12.35M")]
    [InlineData(123_456_789, "123.5M")]
    [InlineData(-1_500, "-1.5K")]
    public void Compact_uses_K_and_M_suffixes(long value, string expected) =>
        Assert.Equal(expected, OverlayFormat.Compact(value));

    [Theory]
    [InlineData(-1.0, "0.0s")]
    [InlineData(0.0, "0.0s")]
    [InlineData(3.04, "3.0s")]
    [InlineData(7.34, "7.3s")]
    [InlineData(59.9, "59.9s")]
    [InlineData(59.96, "01:00")]
    [InlineData(61.2, "01:02")]
    [InlineData(90.0, "01:30")]
    [InlineData(125.0, "02:05")]
    public void Remaining_switches_to_mm_ss_at_one_minute(double seconds, string expected) =>
        Assert.Equal(expected, OverlayFormat.Remaining(seconds));

    [Theory]
    [InlineData(-5.0, "00:00")]
    [InlineData(0.0, "00:00")]
    [InlineData(65.9, "01:05")]
    [InlineData(3600.0, "60:00")]
    public void Duration_is_mm_ss(double seconds, string expected) =>
        Assert.Equal(expected, OverlayFormat.Duration(seconds));
}
