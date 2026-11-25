using System.Globalization;

namespace EQOverwatch.Core.Models;

/// <summary>One parse for several picked fights ("Combined (5)"), timed by the time spent in combat.</summary>
public static class CombinedFights
{
    /// <summary>"Combined (5)".</summary>
    public static string Title(int count) => "Combined (" + count.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>"Combined (5) - Total Damage: 97,632 - DPS: 1,356".</summary>
    public static string Header(EncounterSnapshot combined) =>
        $"{combined.TargetName} - Total Damage: {combined.TotalDamage.ToString("N0", CultureInfo.InvariantCulture)} - DPS: " +
        Math.Round(combined.TotalDamage / Math.Max(1.0, combined.DurationSeconds)).ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Seconds in combat across the fights: each fight's start to end, with overlapping fights counted once and the time between
    /// fights left out.
    /// </summary>
    public static double CombatSeconds(IEnumerable<EncounterSnapshot> fights)
    {
        double seconds = 0;
        DateTime? from = null, to = null;
        foreach (EncounterSnapshot fight in fights.OrderBy(f => f.StartTime))
        {
            DateTime end = fight.EndTime < fight.StartTime ? fight.StartTime : fight.EndTime;
            if (from is null || fight.StartTime > to)
            {
                if (from is not null)
                {
                    seconds += (to!.Value - from.Value).TotalSeconds;
                }

                from = fight.StartTime;
                to = end;
            }
            else if (end > to)
            {
                to = end;
            }
        }

        if (from is not null)
        {
            seconds += (to!.Value - from.Value).TotalSeconds;
        }

        return seconds;
    }

    /// <summary>Fights that overlap in time, grouped (adds fought together), in time order.</summary>
    private static List<List<EncounterSnapshot>> OverlapGroups(IEnumerable<EncounterSnapshot> fights)
    {
        var groups = new List<List<EncounterSnapshot>>();
        DateTime groupEnd = DateTime.MinValue;
        foreach (EncounterSnapshot fight in fights.OrderBy(f => f.StartTime))
        {
            if (groups.Count == 0 || fight.StartTime > groupEnd)
            {
                groups.Add(new List<EncounterSnapshot>());
                groupEnd = fight.EndTime;
            }

            groups[^1].Add(fight);
            if (fight.EndTime > groupEnd)
            {
                groupEnd = fight.EndTime;
            }
        }

        return groups;
    }

    /// <summary>
    /// The picked fights as one parse: everyone's damage, damage taken, healing, deaths and casts added up, DPS over the time in combat,
    /// and the fights' graphs back to back. Heals logged under several mobs fought at the same time are counted once.
    /// </summary>
    public static EncounterSnapshot Combine(IReadOnlyList<EncounterSnapshot> fights)
    {
        ArgumentNullException.ThrowIfNull(fights);
        if (fights.Count == 0)
        {
            throw new ArgumentException("No fights to combine.", nameof(fights));
        }

        if (fights.Count == 1)
        {
            return fights[0];
        }

        EncounterSnapshot merged = EncounterSnapshot.Combine(fights);
        double duration = Math.Max(0.0, CombatSeconds(fights));
        double seconds = Math.Max(1.0, duration);
        List<List<EncounterSnapshot>> groups = OverlapGroups(fights);

        ParticipantSnapshot[] participants = merged.Participants
            .Select(p => p with { ScaledDps = p.TotalDamage / seconds, Dps = Math.Max(p.Dps, p.TotalDamage / seconds) })
            .ToArray();

        // Within a group of mobs fought at once each heal was filed under every mob: take each healer's biggest row there, then add the groups.
        HealingSnapshot[] healing = groups
            .SelectMany(g => g.SelectMany(f => f.HealingRows).GroupBy(h => h.Name, StringComparer.Ordinal).Select(h => h.OrderByDescending(x => x.Effective).First()))
            .GroupBy(h => h.Name, StringComparer.Ordinal)
            .Select(g =>
            {
                long effective = g.Sum(h => h.Effective);
                long overheal = g.Sum(h => h.Overheal);
                return new HealingSnapshot(
                    g.Key, effective, overheal, effective + overheal > 0 ? 100.0 * overheal / (effective + overheal) : 0.0,
                    g.Sum(h => h.Heals), g.Max(h => h.MaxHeal), effective / seconds, g.OrderByDescending(h => h.Effective).First().TopSpell,
                    g.Sum(h => h.CritEffective), g.Sum(h => h.CritHeals));
            })
            .OrderByDescending(h => h.Effective)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ToArray();

        DpsTimeline? timeline = DpsTimeline.Concat(groups.Select(g => g.OrderByDescending(f => f.TotalDamage).First().Timeline).ToList());

        return merged with
        {
            Id = int.MinValue + 1,
            TargetName = Title(fights.Count),
            DurationSeconds = duration,
            IsActive = fights.Any(f => f.IsActive),
            Participants = Array.AsReadOnly(participants),
            Healing = Array.AsReadOnly(healing),
            Timeline = timeline,
            Killed = false,
            TankSpans = Array.Empty<TankSpanSnapshot>(),
            CombinedCount = fights.Count,
        };
    }
}
