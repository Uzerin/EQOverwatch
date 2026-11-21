using EQOverwatch.Core.Leveling;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class Release84Tests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 20, 0, 0);

    private static LevelingScanner At(int level, string zone)
    {
        var scanner = new LevelingScanner();
        scanner.Feed(T0.AddMinutes(-1), $"You have entered {zone}.");
        scanner.Feed(T0.AddMinutes(-1), $"You have gained a level! Welcome to level {level}!");
        return scanner;
    }

    [Fact]
    public void A_fast_real_group_is_kept()
    {
        // The fastest stretches in your own Vaniki logs: a kill about every 20 seconds, at times 5 in 10 seconds, at most 12 in a minute.
        LevelingScanner scanner = At(40, "The Dreadlands");
        int[] gaps = { 2, 2, 2, 2, 20, 20, 5, 5, 20, 20, 20, 3, 3, 20, 20, 20, 20, 20, 20, 20 };
        DateTime t = T0;
        foreach (int gap in gaps)
        {
            t = t.AddSeconds(gap);
            scanner.Feed(t, "You gain party experience! (5.918%)");
        }

        LevelingStretch stretch = Assert.Single(scanner.Finish());
        Assert.Equal(gaps.Length, stretch.Kills);
    }

    [Fact]
    public void Power_leveling_is_dropped_with_the_gains_around_it_across_level_ups()
    {
        // Ream's log in Solusek's Eye: big gains a few seconds apart, levels every minute or so, and stragglers a minute or more later.
        LevelingScanner scanner = At(5, "Solusek's Eye");
        string[] lines =
        {
            "20:06:31", "20:06:32", "20:06:35", "20:06:57", "20:06:57|6", "20:07:11", "20:07:11", "20:07:12", "20:07:12|7", "20:07:20", "20:07:21",
            "20:07:24", "20:07:28|8", "20:07:30", "20:07:45", "20:12:37", "20:12:44|9", "20:12:47", "20:12:48", "20:12:52",
            "20:12:53|10", "20:12:53", "20:13:05", "20:13:07", "20:13:08", "20:13:12",
        };
        foreach (string line in lines)
        {
            string[] parts = line.Split('|');
            DateTime time = T0.Date + TimeSpan.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            if (parts.Length > 1)
            {
                scanner.Feed(time, $"You have gained a level! Welcome to level {parts[1]}!");
            }

            scanner.Feed(time, "You gain party experience (with a bonus)! (25.405%)");
        }

        scanner.Feed(T0.AddMinutes(39), "You have gained a level! Welcome to level 11!");
        scanner.Feed(T0.AddMinutes(40), "You gain party experience! (1.000%)"); // much later: a real stretch
        scanner.Feed(T0.AddMinutes(41), "You gain party experience! (1.000%)");
        scanner.Feed(T0.AddMinutes(43), "You gain party experience! (1.000%)");
        IReadOnlyList<LevelingStretch> found = scanner.Finish();
        LevelingStretch kept = Assert.Single(found);
        Assert.Equal((11, 3), (kept.Level, kept.Kills)); // only the much later kills
    }
}
