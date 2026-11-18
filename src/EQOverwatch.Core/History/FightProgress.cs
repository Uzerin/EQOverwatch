namespace EQOverwatch.Core.History;

/// <summary>How far along a fight is: the share of a usual kill's damage done so far, and the time left at the raid's DPS so far.</summary>
/// <param name="Fraction">0 to 0.99 (never 100% while the mob is alive).</param>
/// <param name="SecondsLeft">At the DPS so far; 0 once the usual kill's damage is reached; null before any damage.</param>
/// <param name="Kills">How many recorded kills the estimate comes from.</param>
public sealed record FightEstimate(double Fraction, double? SecondsLeft, int Kills)
{
    /// <summary>"~62% · about 1:40 left (from 3 kills)".</summary>
    public string Describe()
    {
        int percent = (int)Math.Floor(Fraction * 100);
        string left = SecondsLeft switch
        {
            null => string.Empty,
            <= 0 => " · any moment now",
            { } seconds => " · about " + BossHistoryStore.FormatDuration(seconds) + " left",
        };
        return $"~{percent}%{left} (from {Kills} kill{(Kills == 1 ? string.Empty : "s")})";
    }
}

/// <summary>Estimates a fight in progress from the damage the recorded kills of the same mob took.</summary>
public static class FightProgress
{
    /// <summary>Recorded kills needed before an estimate is shown.</summary>
    public const int MinKills = 2;

    /// <summary>
    /// The estimate for <paramref name="mob"/> on <paramref name="server"/> (kills whose server is not known count for every server), or
    /// null with fewer than <see cref="MinKills"/> kills. The usual kill is the median of their total damage.
    /// </summary>
    public static FightEstimate? Estimate(IEnumerable<BossFight> fights, string mob, string? server, long damageSoFar, double secondsSoFar)
    {
        ArgumentNullException.ThrowIfNull(fights);
        if (string.IsNullOrWhiteSpace(mob))
        {
            return null;
        }

        long[] kills = fights
            .Where(f => f.Killed && f.Damage > 0 && f.Boss.Equals(mob.Trim(), StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(f.Server) || f.Server.Equals(server, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Damage)
            .OrderBy(d => d)
            .ToArray();
        if (kills.Length < MinKills)
        {
            return null;
        }

        double usual = kills.Length % 2 == 1
            ? kills[kills.Length / 2]
            : (kills[kills.Length / 2 - 1] + kills[kills.Length / 2]) / 2.0;
        double done = Math.Max(0, damageSoFar);
        double fraction = Math.Min(0.99, done / usual);
        double rate = secondsSoFar > 0 ? done / secondsSoFar : 0;
        double? left = done >= usual ? 0 : rate > 0 ? (usual - done) / rate : null;
        return new FightEstimate(fraction, left, kills.Length);
    }
}
