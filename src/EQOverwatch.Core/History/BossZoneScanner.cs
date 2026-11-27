namespace EQOverwatch.Core.History;

/// <summary>
/// Finds the zone each boss was fought in by reading logs: the zone of the latest "You have entered X." before each line that names the
/// boss. Lines that name a boss from anywhere are skipped (chat, tells, faction changes and the server's kill announcements), and
/// the zone with the most lines wins, so a mention elsewhere does not move it.
/// </summary>
public static class BossZoneScanner
{
    private const string Entered = "You have entered ";

    public static Dictionary<string, string> Scan(IEnumerable<string> logPaths, IReadOnlyCollection<string> bosses, CancellationToken cancel = default)
    {
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        string[] names = bosses.Where(b => b.Trim().Length > 0).Select(b => b.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (string path in logPaths)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
                string? zone = null;
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    cancel.ThrowIfCancellationRequested();
                    int start = line.IndexOf("] ", StringComparison.Ordinal);
                    if (start < 0)
                    {
                        continue;
                    }

                    ReadOnlySpan<char> message = line.AsSpan(start + 2);
                    if (message.StartsWith(Entered, StringComparison.Ordinal) && message.EndsWith(".", StringComparison.Ordinal))
                    {
                        zone = message[Entered.Length..^1].ToString();
                        continue;
                    }

                    if (zone is null || NamesBossFromAnywhere(message))
                    {
                        continue;
                    }

                    foreach (string boss in names)
                    {
                        if (message.Contains(boss, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!counts.TryGetValue(boss, out Dictionary<string, int>? zones))
                            {
                                counts[boss] = zones = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            }

                            zones[zone] = zones.GetValueOrDefault(zone) + 1;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // a log that cannot be read adds nothing
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return counts.ToDictionary(c => c.Key, c => c.Value.OrderByDescending(z => z.Value).First().Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True for lines that can name a boss fought in another zone: chat, tells, faction changes, kill announcements.</summary>
    public static bool NamesBossFromAnywhere(ReadOnlySpan<char> message) =>
        message.Contains(", '", StringComparison.Ordinal)
        || message.Contains(" tells you", StringComparison.Ordinal)
        || message.Contains("faction", StringComparison.OrdinalIgnoreCase)
        || message.Contains(" has been defeated by ", StringComparison.Ordinal);
}
