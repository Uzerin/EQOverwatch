using System.Globalization;
using System.Text;
using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.Services;

/// <summary>One spell one player cast in a fight.</summary>
/// <param name="Offsets">Seconds from the start of the fight to each cast.</param>
public sealed record PlayerSpellRow(string Spell, int Casts, int Failed, double CastsPerMinute, IReadOnlyList<double> Offsets)
{
    public double FirstOffsetSeconds => Offsets.Count == 0 ? 0 : Offsets[0];
    public double LastOffsetSeconds => Offsets.Count == 0 ? 0 : Offsets[^1];
}

/// <summary>One player's casts in a fight, with a row per spell, most-cast first.</summary>
public sealed record PlayerCastRow(string Name, int Casts, int Failed, double CastsPerMinute, IReadOnlyList<PlayerSpellRow> Spells);

/// <summary>One spell across the whole raid.</summary>
public sealed record SpellTotalRow(string Spell, int Casts, int Casters, int Failed, double CastsPerMinute, string TopCaster, int TopCasterCasts);

/// <summary>One cast on the timeline: when (seconds into the fight), who, and whether it failed.</summary>
public sealed record CastMark(double Offset, string Caster, bool Failed);

/// <summary>A timeline lane: every cast of one spell by anyone in the raid.</summary>
public sealed record CastLane(string Spell, IReadOnlyList<CastMark> Marks);

/// <summary>
/// Everything the Spell Casts tab shows for one fight: a breakdown by player, totals by spell and a lane per spell for the
/// timeline. An optional text filter keeps only spells whose name contains it (e.g. "slow").
/// </summary>
public sealed class SpellCastReport
{
    private SpellCastReport(
        double durationSeconds, int totalCasts, IReadOnlyList<PlayerCastRow> players, IReadOnlyList<SpellTotalRow> spells, IReadOnlyList<CastLane> lanes)
    {
        DurationSeconds = durationSeconds;
        TotalCasts = totalCasts;
        Players = players;
        Spells = spells;
        Lanes = lanes;
    }

    public double DurationSeconds { get; }
    public int TotalCasts { get; }
    public IReadOnlyList<PlayerCastRow> Players { get; }
    public IReadOnlyList<SpellTotalRow> Spells { get; }
    public IReadOnlyList<CastLane> Lanes { get; }

    public static SpellCastReport Build(EncounterSnapshot encounter, string? spellFilter = null)
    {
        ArgumentNullException.ThrowIfNull(encounter);

        string filter = spellFilter?.Trim() ?? string.Empty;
        PlayerCastSnapshot[] casts = encounter.PlayerCastRows
            .Where(c => filter.Length == 0 || c.Spell.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Time)
            .ToArray();

        double minutes = Math.Max(1.0, encounter.DurationSeconds) / 60.0;
        double Offset(PlayerCastSnapshot c) => Math.Max(0.0, (c.Time - encounter.StartTime).TotalSeconds);

        PlayerCastRow[] players = casts
            .GroupBy(c => c.Caster, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                PlayerSpellRow[] spells = g
                    .GroupBy(c => c.Spell, StringComparer.OrdinalIgnoreCase)
                    .Select(sg => new PlayerSpellRow(sg.Key, sg.Count(), sg.Count(c => c.Failed), sg.Count() / minutes, sg.Select(Offset).ToArray()))
                    .OrderByDescending(r => r.Casts)
                    .ThenBy(r => r.Spell, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new PlayerCastRow(g.Key, g.Count(), g.Count(c => c.Failed), g.Count() / minutes, spells);
            })
            .OrderByDescending(p => p.Casts)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        SpellTotalRow[] totals = casts
            .GroupBy(c => c.Spell, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                IGrouping<string, PlayerCastSnapshot> top = g
                    .GroupBy(c => c.Caster, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(cg => cg.Count())
                    .ThenBy(cg => cg.Key, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new SpellTotalRow(
                    g.Key, g.Count(), g.Select(c => c.Caster).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    g.Count(c => c.Failed), g.Count() / minutes, top.Key, top.Count());
            })
            .OrderByDescending(t => t.Casts)
            .ThenBy(t => t.Spell, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        CastLane[] lanes = totals
            .Select(t => new CastLane(
                t.Spell,
                casts.Where(c => c.Spell.Equals(t.Spell, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new CastMark(Offset(c), c.Caster, c.Failed))
                    .ToArray()))
            .ToArray();

        return new SpellCastReport(Math.Max(1.0, encounter.DurationSeconds), casts.Length, players, totals, lanes);
    }

    /// <summary>"1:05" for 65 seconds.</summary>
    public static string FormatOffset(double seconds)
    {
        long total = (long)Math.Max(0, Math.Round(seconds));
        return $"{total / 60}:{total % 60:00}";
    }

    /// <summary>Plain text for pasting into chat or Discord: each player with their spells, then the spell totals.</summary>
    public string BuildText(string bossName)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Spell casts vs {bossName} ({FormatOffset(DurationSeconds)}, {TotalCasts} casts)"));
        foreach (PlayerCastRow player in Players)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{player.Name}: {player.Casts} casts"));
            foreach (PlayerSpellRow spell in player.Spells)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {spell.Spell} x{spell.Casts}{(spell.Failed > 0 ? $" ({spell.Failed} failed)" : string.Empty)}"));
            }
        }

        if (Spells.Count > 0)
        {
            sb.AppendLine("Totals:");
            foreach (SpellTotalRow total in Spells)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {total.Spell}: {total.Casts} by {total.Casters}"));
            }
        }

        return sb.ToString().TrimEnd();
    }
}
