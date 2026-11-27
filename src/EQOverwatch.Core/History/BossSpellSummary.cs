using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.History;

/// <summary>One spell a boss was seen casting: how many casts and the shortest time between two of them (null with one cast).</summary>
public sealed record BossSpellSeen(string Spell, int Casts, int? MinGapSeconds);

/// <summary>The spells a boss cast in the fights the app has, for its mob card.</summary>
public static class BossSpellSummary
{
    /// <summary>Casts closer than this are one cast that several logs saw a second apart.</summary>
    public const int SameCastSeconds = 2;

    /// <summary>Gaps longer than this are between fights (or across a long pause), not a recast.</summary>
    public const int MaxGapSeconds = 300;

    /// <summary>Each spell <paramref name="mob"/> cast in <paramref name="fights"/>, most-cast first.</summary>
    public static IReadOnlyList<BossSpellSeen> For(IEnumerable<EncounterSnapshot> fights, string mob)
    {
        ArgumentNullException.ThrowIfNull(fights);
        string name = (mob ?? string.Empty).Trim();
        var bySpell = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        foreach (EncounterSnapshot fight in fights)
        {
            if (!fight.TargetName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (BossCastSnapshot cast in fight.CastRows)
            {
                if (string.IsNullOrWhiteSpace(cast.Spell))
                {
                    continue;
                }

                string spell = cast.Spell.Trim();
                if (!bySpell.TryGetValue(spell, out List<DateTime>? times))
                {
                    bySpell[spell] = times = new List<DateTime>();
                }

                times.Add(cast.Time);
            }
        }

        var seen = new List<BossSpellSeen>();
        foreach ((string spell, List<DateTime> times) in bySpell)
        {
            times.Sort();
            int casts = 0;
            int? minGap = null;
            DateTime? last = null;
            foreach (DateTime time in times)
            {
                if (last is DateTime previous)
                {
                    double gap = (time - previous).TotalSeconds;
                    if (gap < SameCastSeconds)
                    {
                        continue; // the same cast again (one fight listed twice, or two logs)
                    }

                    if (gap <= MaxGapSeconds && (minGap is null || gap < minGap))
                    {
                        minGap = (int)Math.Round(gap);
                    }
                }

                casts++;
                last = time;
            }

            seen.Add(new BossSpellSeen(spell, casts, minGap));
        }

        return seen.OrderByDescending(s => s.Casts).ThenBy(s => s.Spell, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
