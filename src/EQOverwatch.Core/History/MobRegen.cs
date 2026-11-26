using System.Globalization;

namespace EQOverwatch.Core.History;

/// <summary>A mob's estimated regeneration and hit points, worked out from its recorded kills.</summary>
/// <param name="RegenPerSecond">Hit points the mob gains back each second (never below 0).</param>
/// <param name="HitPoints">The damage a kill with no regen would take.</param>
/// <param name="Kills">How many kills the estimate comes from.</param>
public sealed record MobRegen(double RegenPerSecond, long HitPoints, int Kills)
{
    /// <summary>"~45/s" (one decimal below 10).</summary>
    public string RegenText => "~" + (RegenPerSecond < 10
        ? RegenPerSecond.ToString("0.#", CultureInfo.CurrentCulture)
        : Math.Round(RegenPerSecond).ToString("N0", CultureInfo.CurrentCulture)) + "/s";

    /// <summary>"~1.2M", "~15.5k", "~950".</summary>
    public string HpText => "~" + Compact(HitPoints);

    /// <summary>"Regen ~45/s · HP ~1.2M".</summary>
    public string Describe() => $"Regen {RegenText} · HP {HpText}";

    public static string Compact(long value)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        return value switch
        {
            >= 1_000_000 => (value / 1_000_000.0).ToString("0.#", culture) + "M",
            >= 10_000 => (value / 1_000.0).ToString("0.#", culture) + "k",
            _ => value.ToString("N0", culture),
        };
    }
}

/// <summary>
/// Estimates regen and hit points from kills of different lengths: a kill takes the mob's hit points plus its regen over the fight, so
/// longer kills take more damage. Only the kills with the most damage are used (the top five or the top half), so kills where the logs saw
/// only part of the damage are left out; the slope is the median of the pairwise slopes (so a few odd kills do not move it), and nothing is returned when the kills disagree.
/// </summary>
public static class MobRegenEstimator
{
    /// <summary>Kills needed (after incomplete ones are left out).</summary>
    public const int MinKills = 3;

    /// <summary>The shortest and longest kill used must be at least this far apart, in seconds.</summary>
    public const double MinSpreadSeconds = 30;

    private const int MaxKills = 200;

    /// <summary>At least this many of the kills with the most damage are used (more when half the kills is more).</summary>
    public const int TopKills = 5;
    private const double MinPairSeconds = 10;

    /// <summary>
    /// The estimate for <paramref name="mob"/> on <paramref name="server"/> (kills whose server is not known count for every server;
    /// null server: every server), or null with too few kills, too little spread in kill times, or kills that do not agree.
    /// </summary>
    public static MobRegen? Estimate(IEnumerable<BossFight> fights, string mob, string? server = null)
    {
        ArgumentNullException.ThrowIfNull(fights);
        if (string.IsNullOrWhiteSpace(mob))
        {
            return null;
        }

        string name = mob.Trim();
        (double T, double D)[] kills = fights
            .Where(f => f.Killed && f.Damage > 0 && f.Seconds >= 5 && f.Boss.Equals(name, StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(f.Server) || f.Server.Equals(server, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Start)
            .TakeLast(MaxKills)
            .Select(f => (f.Seconds, (double)f.Damage))
            .ToArray();
        return Estimate(kills);
    }

    /// <summary>The estimate from (seconds, damage) pairs of kills.</summary>
    public static MobRegen? Estimate(IReadOnlyList<(double Seconds, double Damage)> kills)
    {
        if (kills.Count < MinKills)
        {
            return null;
        }

        // Kills where the logs missed damage (players out of range) show too little, so only the kills with the most damage are used:
        // the top five or the top half, whichever is more.
        int take = Math.Max(TopKills, (kills.Count + 1) / 2);
        (double T, double D)[] kept = kills.OrderByDescending(k => k.Damage).Take(take).Select(k => (k.Seconds, k.Damage)).ToArray();
        if (kept.Length < MinKills || kept.Max(k => k.T) - kept.Min(k => k.T) < MinSpreadSeconds)
        {
            return null;
        }

        var slopes = new List<double>();
        for (int i = 0; i < kept.Length; i++)
        {
            for (int j = i + 1; j < kept.Length; j++)
            {
                double dt = kept[j].T - kept[i].T;
                if (Math.Abs(dt) >= MinPairSeconds)
                {
                    slopes.Add((kept[j].D - kept[i].D) / dt);
                }
            }
        }

        if (slopes.Count == 0)
        {
            return null;
        }

        double regen = Math.Max(0, Median(slopes));
        double hp = Median(kept.Select(k => k.D - regen * k.T).ToList());
        double spread = Median(kept.Select(k => Math.Abs(k.D - regen * k.T - hp)).ToList());
        if (hp <= 0 || spread > 0.15 * hp)
        {
            return null; // the kills disagree (often one name used by different mobs)
        }

        return new MobRegen(regen, (long)Math.Round(hp), kept.Length);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }
}
