using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.History;
using Xunit;

namespace EQOverwatch.App.Tests;

public sealed class BossKillsLineTests
{
    private static BossFight Attempt(int day) => new("Lord Inquisitor Seru", new DateTime(2026, 10, day, 20, 0, 0), 72, 500_000, 20, 1, false);

    [Fact]
    public void No_attempts_gives_no_line()
    {
        Assert.Equal(string.Empty, MainViewModel.AttemptsLine(Array.Empty<BossFight>()));
    }

    [Fact]
    public void One_attempt_is_named_in_the_singular()
    {
        string line = MainViewModel.AttemptsLine(new[] { Attempt(9) });

        Assert.StartsWith("1 attempt without a kill: ", line);
        Assert.Contains("(1:12)", line);
        Assert.DoesNotContain(" and ", line);
    }

    [Fact]
    public void Two_attempts_are_joined_with_and()
    {
        string line = MainViewModel.AttemptsLine(new[] { Attempt(9), Attempt(2) });

        Assert.StartsWith("2 attempts without a kill: ", line);
        Assert.Contains(" and ", line);
    }

    [Fact]
    public void Many_attempts_name_the_first_five_and_count_the_rest()
    {
        string line = MainViewModel.AttemptsLine(Enumerable.Range(1, 7).Select(Attempt).ToArray());

        Assert.StartsWith("7 attempts without a kill: ", line);
        Assert.EndsWith(", and 2 more", line);
    }
}
