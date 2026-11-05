namespace EQOverwatch.Core.Loot;

/// <summary>
/// Searches the loot history. The search text is split into words and every word has to appear in the item, the looter or the mob
/// (so "seru sword" finds swords that dropped from Seru), and a time range can limit how far back to look.
/// </summary>
public static class LootSearch
{
    /// <summary>The time ranges offered, in days; 0 means all time.</summary>
    public static readonly int[] RangeDays = { 0, 7, 30, 90 };

    public static readonly string[] RangeNames = { "All time", "Last 7 days", "Last 30 days", "Last 90 days" };

    /// <summary>The words of <paramref name="text"/> (split on spaces, a quoted phrase stays together).</summary>
    public static IReadOnlyList<string> Terms(string? text)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return terms;
        }

        bool quoted = false;
        var current = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                Flush();
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }

        Flush();
        return terms;

        void Flush()
        {
            if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }
    }

    /// <summary>True when every term is found in the item, the looter or the mob (capitals ignored). No terms matches everything.</summary>
    public static bool Matches(LootEntry entry, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (string term in terms)
        {
            if (!entry.Item.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !entry.Looter.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !entry.Mob.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when <paramref name="when"/> is inside the range (<paramref name="rangeDays"/> of 0 or less is all time).</summary>
    public static bool InRange(DateTime when, DateTime now, int rangeDays) => rangeDays <= 0 || when >= now.AddDays(-rangeDays);

    /// <summary>
    /// When the matches are all one item, a line saying who looted it last and how often ("Mnemonic of Khati Sha: looted 3 times, last by
    /// Warshield on Nov 2 from Lord Inquisitor Seru."). Empty when there are no matches or they are several items.
    /// </summary>
    public static string LastLootedLine(IReadOnlyList<LootEntry> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0 || matches.Select(m => m.Item).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            return string.Empty;
        }

        LootEntry last = matches.OrderByDescending(m => m.Time).First();
        return string.Create(
            System.Globalization.CultureInfo.CurrentCulture,
            $"{last.Item}: looted {matches.Count} time{(matches.Count == 1 ? string.Empty : "s")}, last by {last.Looter} on {last.Time:MMM d} from {last.Mob}.");
    }
}
