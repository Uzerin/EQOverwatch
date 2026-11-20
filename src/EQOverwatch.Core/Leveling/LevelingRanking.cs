using EQOverwatch.Core.Maps;

namespace EQOverwatch.Core.Leveling;

/// <summary>One player's leveling in one zone and level band, solo or in a group (players are numbered, never named).</summary>
public sealed record LevelingRow(string Zone, int Band, bool Group, string Player, double Seconds, double Xp, int Kills);

/// <summary>A zone's standing in a level band: the median player's XP and kills per hour, and how much data it rests on.</summary>
public sealed record ZoneRank(string Zone, int Band, bool Group, double XpPerHour, double KillsPerHour, int Players, double Hours)
{
    /// <summary>True when there is enough data to rank the zone.</summary>
    public bool Enough => Players >= LevelingRanking.MinPlayers && Hours >= LevelingRanking.MinHours;
}

/// <summary>Ranks zones for leveling from everyone's stretches, and picks the best zone for each level band.</summary>
public static class LevelingRanking
{
    public const int BandSize = 5;
    public const int MinPlayers = 3;
    public const double MinHours = 5;

    /// <summary>A player's rate in a zone counts once they have at least this much time there.</summary>
    private const double MinPlayerHours = 0.25;

    /// <summary>The first level of <paramref name="level"/>'s band (1, 6, 11, ...).</summary>
    public static int BandOf(int level) => (((Math.Max(1, level) - 1) / BandSize) * BandSize) + 1;

    public static string BandText(int band) => $"{band}–{band + BandSize - 1}";

    /// <summary>Your own stretches as rows, under the player name <paramref name="player"/>.</summary>
    public static IReadOnlyList<LevelingRow> Rows(IEnumerable<LevelingStretch> stretches, string player) =>
        stretches
            .GroupBy(s => (Zone: s.Zone.ToLowerInvariant(), Band: BandOf(s.Level), s.Group))
            .Select(g => new LevelingRow(g.First().Zone, g.Key.Band, g.Key.Group, player, g.Sum(s => s.Seconds), g.Sum(s => s.Xp), g.Sum(s => s.Kills)))
            .ToList();

    /// <summary>
    /// The zones of <paramref name="band"/> (solo or group), best first: zones with enough data before the rest, then by the median
    /// player's XP per hour. <paramref name="allowed"/> leaves out zones of expansions the server does not have.
    /// </summary>
    public static IReadOnlyList<ZoneRank> Rank(IEnumerable<LevelingRow> rows, int band, bool group, Func<string, bool>? allowed = null) =>
        rows
            .Where(r => r.Band == band && r.Group == group && r.Seconds > 0 && (allowed?.Invoke(r.Zone) ?? true))
            .GroupBy(r => r.Zone, StringComparer.OrdinalIgnoreCase)
            .Select(z =>
            {
                var players = z.GroupBy(r => r.Player)
                    .Select(p => (Hours: p.Sum(r => r.Seconds) / 3600, Xp: p.Sum(r => r.Xp), Kills: p.Sum(r => r.Kills)))
                    .ToList();
                var counted = players.Where(p => p.Hours >= MinPlayerHours).ToList();
                if (counted.Count == 0)
                {
                    counted = players; // too little time for anyone: still shown, as not enough data
                }

                return new ZoneRank(z.First().Zone, band, group,
                    Median(counted.Select(p => p.Xp / p.Hours)), Median(counted.Select(p => p.Kills / p.Hours)),
                    players.Count, players.Sum(p => p.Hours));
            })
            .OrderByDescending(r => r.Enough)
            .ThenByDescending(r => r.XpPerHour)
            .ToList();

    /// <summary>The best ranked zone for each band from <paramref name="level"/>'s band up to the highest band with data (null: not enough data).</summary>
    public static IReadOnlyList<(int Band, ZoneRank? Best)> Path(IReadOnlyCollection<LevelingRow> rows, int level, bool group, Func<string, bool>? allowed = null)
    {
        int first = BandOf(level);
        int last = rows.Where(r => r.Group == group).Select(r => r.Band).DefaultIfEmpty(first).Max();
        var path = new List<(int, ZoneRank?)>();
        for (int band = first; band <= Math.Max(first, last); band += BandSize)
        {
            path.Add((band, Rank(rows, band, group, allowed).FirstOrDefault(r => r.Enough)));
        }

        return path;
    }

    /// <summary>The expansion of a zone by its full name (the oldest when several zones share it), or null when the name is not known.</summary>
    public static int? ExpansionOf(ZoneDirectory zones, string zone)
    {
        IReadOnlyList<ZoneInfo> found = zones.ByName(zone);
        return found.Count == 0 ? null : found.Min(z => z.Expansion);
    }

    /// <summary>
    /// The expansions a server has, from the zones seen on it: every expansion up to the newest one a zone was seen in (servers open
    /// expansions in order). A name several expansions share (Chardok, and the newer Chardok) counts as its oldest zone.
    /// </summary>
    public static IReadOnlySet<int> InferExpansions(ZoneDirectory zones, IEnumerable<string> seen)
    {
        int newest = 1;
        foreach (string zone in seen.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ExpansionOf(zones, zone) is { } expansion)
            {
                newest = Math.Max(newest, expansion);
            }
        }

        return Enumerable.Range(1, newest).ToHashSet();
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0)
        {
            return 0;
        }

        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
    }
}
