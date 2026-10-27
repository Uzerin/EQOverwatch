using System.Globalization;
using System.Text;
using EQCompanion.Core.Models;

namespace EQCompanion.Core.Export;

/// <summary>One line of the Discord screenshot, already formatted.</summary>
/// <param name="Fill">Damage relative to the top attacker (0 to 1); sizes the row's background bar.</param>
public sealed record EncounterExportRow(
    int Rank,
    string Name,
    string Total,
    string Sdps,
    string MaxHit,
    string MaxCrit,
    string CritPercent,
    double Fill,
    string Class = "");

/// <summary>Text and table versions of an encounter, for pasting into EverQuest chat or rendering as a Discord screenshot.</summary>
public static class EncounterExport
{
    /// <summary>The attackers in the chat text (EverQuest chat has little room).</summary>
    public const int DefaultTopCount = 12;

    /// <summary>Pass as the top count to include every attacker (the Discord picture lists everyone).</summary>
    public const int AllAttackers = int.MaxValue;

    /// <summary>The column titles of the screenshot's table, in order.</summary>
    public static IReadOnlyList<string> ColumnHeaders { get; } =
        new[] { "#", "Name", "Class", "Total", "SDPS", "Max Hit", "Max Crit", "Crit %" };

    /// <summary>The rows of the screenshot's table: the top attackers with every number formatted.</summary>
    public static IReadOnlyList<EncounterExportRow> BuildRows(EncounterSnapshot encounter, int topCount = DefaultTopCount)
    {
        IReadOnlyList<ParticipantSnapshot> top = TopParticipants(encounter, topCount);
        long best = top.Count > 0 ? top[0].TotalDamage : 0;

        var rows = new List<EncounterExportRow>(top.Count);
        for (int i = 0; i < top.Count; i++)
        {
            ParticipantSnapshot p = top[i];
            bool hasCrits = p.CritHitCount > 0;
            rows.Add(new EncounterExportRow(
                i + 1,
                p.Name,
                p.TotalDamage.ToString("N0", CultureInfo.InvariantCulture),
                Math.Round(p.ScaledDps).ToString("N0", CultureInfo.InvariantCulture),
                p.MaxHit.ToString("N0", CultureInfo.InvariantCulture),
                hasCrits ? p.MaxCritHit.ToString("N0", CultureInfo.InvariantCulture) : "-",
                hasCrits ? p.CritRatePercent.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "-",
                best > 0 ? (double)p.TotalDamage / best : 0.0,
                p.Class));
        }

        return rows;
    }

    /// <summary>
    /// A header line followed by the top attackers, one per line: <c>1. Name Total (SDPS)</c>.
    /// <example>
    /// <code>
    /// a frost giant - 0:42, 2,200 damage, group DPS 52
    /// 1. Stackprime 1,200 (29)
    /// 2. Oodles 700 (17)
    /// </code>
    /// </example>
    /// Numbers use thousands separators and never depend on the PC's regional settings. SDPS is the scaled DPS: the attacker's damage
    /// divided by the whole fight's length.
    /// </summary>
    public static string BuildChatText(EncounterSnapshot encounter, int topCount = DefaultTopCount)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        var sb = new StringBuilder();
        sb.Append(BuildHeader(encounter));

        int rank = 0;
        foreach (ParticipantSnapshot participant in TopParticipants(encounter, topCount))
        {
            rank++;
            sb.Append('\n')
              .Append(rank.ToString(CultureInfo.InvariantCulture)).Append(". ")
              .Append(participant.Name).Append(' ')
              .Append(participant.TotalDamage.ToString("N0", CultureInfo.InvariantCulture))
              .Append(" (").Append(Math.Round(participant.ScaledDps).ToString("N0", CultureInfo.InvariantCulture)).Append(')');
        }

        return sb.ToString();
    }

    /// <summary>"a frost giant - 0:42, 2,200 damage, group DPS 52".</summary>
    public static string BuildHeader(EncounterSnapshot encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        double groupDps = encounter.TotalDamage / Math.Max(1.0, encounter.DurationSeconds);
        return $"{encounter.TargetName} - {FormatDuration(encounter.DurationSeconds)}, " +
               $"{encounter.TotalDamage.ToString("N0", CultureInfo.InvariantCulture)} damage, " +
               $"group DPS {Math.Round(groupDps).ToString("N0", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The attackers ranked by damage (ties by name), at most <paramref name="topCount"/> of them.</summary>
    public static IReadOnlyList<ParticipantSnapshot> TopParticipants(EncounterSnapshot encounter, int topCount = DefaultTopCount)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        return encounter.Participants
            .OrderByDescending(p => p.TotalDamage)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, topCount))
            .ToArray();
    }

    /// <summary>"m:ss" ("1:05:09" past an hour).</summary>
    public static string FormatDuration(double seconds)
    {
        long total = (long)Math.Max(0, Math.Round(seconds));
        long hours = total / 3600;
        long minutes = total % 3600 / 60;
        long secs = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}");
    }
}
