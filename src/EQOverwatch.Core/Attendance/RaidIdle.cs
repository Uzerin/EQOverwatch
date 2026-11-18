using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.Attendance;

/// <summary>A boss fight during a raid and the raid members who did no damage and no healing in it.</summary>
public sealed record IdleInFight(string Boss, DateTime Start, double Seconds, IReadOnlyList<string> Idle);

/// <summary>Finds the raid members who did nothing in each boss fight around a raid roster dump.</summary>
public static class RaidIdle
{
    /// <summary>Fights this long before the dump are counted (the dump is often taken during the raid).</summary>
    public static readonly TimeSpan Before = TimeSpan.FromHours(3);

    /// <summary>Fights up to this long after the dump are counted.</summary>
    public static readonly TimeSpan After = TimeSpan.FromHours(5);

    /// <summary>A boss fight is a kill with at least this many attackers.</summary>
    public const int MinAttackers = 6;

    public const double MinSeconds = 20;

    /// <summary>Each killed boss fight near <paramref name="raid"/>'s dump, oldest first, with the members who neither attacked nor healed.</summary>
    public static IReadOnlyList<IdleInFight> Find(RaidAttendance raid, IEnumerable<EncounterSnapshot> fights)
    {
        ArgumentNullException.ThrowIfNull(raid);
        ArgumentNullException.ThrowIfNull(fights);
        DateTime from = raid.Taken - Before;
        DateTime to = raid.Taken + After;
        var result = new List<IdleInFight>();
        foreach (EncounterSnapshot fight in fights
                     .Where(f => f.Killed && !f.IsActive && f.DurationSeconds >= MinSeconds && f.Participants.Count >= MinAttackers)
                     .Where(f => f.StartTime >= from && f.StartTime <= to)
                     .Where(f => f.Server.Length == 0 || raid.Server.Length == 0 || f.Server.Equals(raid.Server, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(f => (f.TargetName, f.StartTime))
                     .Select(g => g.First())
                     .OrderBy(f => f.StartTime))
        {
            var active = new HashSet<string>(fight.Participants.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            active.UnionWith(fight.HealingRows.Select(h => h.Name));
            active.UnionWith(fight.PlayerCastRows.Select(c => c.Caster));
            List<string> idle = raid.Members
                .Select(m => m.Name)
                .Where(n => !active.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.Add(new IdleInFight(fight.TargetName, fight.StartTime, fight.DurationSeconds, idle));
        }

        return result;
    }
}
