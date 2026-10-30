using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.Core.Services;

/// <summary>A raid trigger worth adding: a boss spell cast again and again at nearly the same interval.</summary>
/// <param name="RecastSeconds">The usual time between casts.</param>
/// <param name="Casts">Casts seen across all the fights.</param>
/// <param name="Fights">Fights the spell was seen in.</param>
/// <param name="Steadiness">Share of the gaps that were close to the usual one (0 to 1).</param>
public sealed record RaidTriggerSuggestion(
    string MobName, string SpellName, string MatchText, int RecastSeconds, int Casts, int Fights, double Steadiness);

/// <summary>
/// Looks through the fights against one boss for spells that come back on a regular interval. A spell qualifies with at least
/// three gaps between casts, and at least 70% of its gaps within 15% (and never less than 2 seconds) of the median gap.
/// </summary>
public static class RaidTriggerSuggester
{
    public const int MinimumGaps = 3;
    public const double MinimumSteadiness = 0.7;
    private const double MinimumGapSeconds = 3.0;

    public static IReadOnlyList<RaidTriggerSuggestion> Suggest(string mobName, IEnumerable<EncounterSnapshot> encounters)
    {
        ArgumentNullException.ThrowIfNull(encounters);
        if (string.IsNullOrWhiteSpace(mobName))
        {
            return Array.Empty<RaidTriggerSuggestion>();
        }

        List<EncounterSnapshot> fights = encounters
            .Where(e => e.TargetName.Equals(mobName.Trim(), StringComparison.OrdinalIgnoreCase) && e.CastRows.Count > 0)
            .ToList();

        var gapsBySpell = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var castsBySpell = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fightsBySpell = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (EncounterSnapshot fight in fights)
        {
            foreach (IGrouping<string, BossCastSnapshot> spell in fight.CastRows.OrderBy(c => c.Time).GroupBy(c => c.Spell, StringComparer.OrdinalIgnoreCase))
            {
                BossCastSnapshot[] casts = spell.ToArray();
                castsBySpell[spell.Key] = castsBySpell.GetValueOrDefault(spell.Key) + casts.Length;
                fightsBySpell[spell.Key] = fightsBySpell.GetValueOrDefault(spell.Key) + 1;

                if (!gapsBySpell.TryGetValue(spell.Key, out List<double>? gaps))
                {
                    gapsBySpell[spell.Key] = gaps = new List<double>();
                }

                for (int i = 1; i < casts.Length; i++)
                {
                    double gap = (casts[i].Time - casts[i - 1].Time).TotalSeconds;
                    if (gap >= MinimumGapSeconds)
                    {
                        gaps.Add(gap);
                    }
                }
            }
        }

        var suggestions = new List<RaidTriggerSuggestion>();
        foreach (KeyValuePair<string, List<double>> pair in gapsBySpell)
        {
            if (pair.Value.Count < MinimumGaps)
            {
                continue;
            }

            double median = Median(pair.Value);
            double tolerance = Math.Max(2.0, median * 0.15);
            double steadiness = pair.Value.Count(g => Math.Abs(g - median) <= tolerance) / (double)pair.Value.Count;
            if (steadiness < MinimumSteadiness)
            {
                continue;
            }

            suggestions.Add(new RaidTriggerSuggestion(
                mobName.Trim(), pair.Key, RaidBossTimerRule.CastingText(pair.Key), (int)Math.Round(median),
                castsBySpell[pair.Key], fightsBySpell[pair.Key], steadiness));
        }

        return suggestions.OrderByDescending(s => s.Casts).ThenBy(s => s.SpellName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
