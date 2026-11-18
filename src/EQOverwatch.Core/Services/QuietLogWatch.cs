namespace EQOverwatch.Core.Services;

/// <summary>
/// Spots a character whose log went quiet during a fight (a disconnect or a crash): its last line is at least <see cref="QuietAfter"/>
/// old but no older than <see cref="ForgetAfter"/>, while a fight is going on. Each quiet spell is reported once; a new line starts over.
/// </summary>
public sealed class QuietLogWatch
{
    public static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(2);

    /// <summary>A character quiet for longer than this camped or logged off earlier; it is not reported.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTime> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The characters to alert about now. <paramref name="lastLines"/> holds each watched character's last log line (UTC).</summary>
    public IReadOnlyList<string> Check(DateTime nowUtc, IEnumerable<(string Character, DateTime LastLineUtc)> lastLines, bool fightInProgress)
    {
        var due = new List<string>();
        foreach ((string character, DateTime last) in lastLines)
        {
            if (last == default)
            {
                continue;
            }

            TimeSpan quiet = nowUtc - last;
            if (!fightInProgress || quiet < QuietAfter || quiet > ForgetAfter)
            {
                continue;
            }

            if (_reported.TryGetValue(character, out DateTime reportedFor) && reportedFor == last)
            {
                continue;
            }

            _reported[character] = last;
            due.Add(character);
        }

        return due;
    }
}
