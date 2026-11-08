namespace EQOverwatch.Core.Pipeline;

/// <summary>Told about every live log line, so the app can tell which characters are logged in and fighting.</summary>
public interface ILogActivityObserver
{
    /// <summary>Any line was written to <paramref name="character"/>'s log.</summary>
    void OnLogLine(string character, DateTime receivedUtc);

    /// <summary>A combat line (a hit, miss or death) was read from <paramref name="character"/>'s log.</summary>
    void OnCombatLine(string character, DateTime receivedUtc);
}

/// <summary>
/// Knows which characters are logged in. A log only grows while its character is in the game, so a character is active while a line
/// has been written to its log within <see cref="ActiveWindow"/>. Overlays show only for active characters. Thread-safe; only lines
/// read live count, so a history import or a restart does not make a character look active.
/// </summary>
public sealed class CharacterActivityService : ILogActivityObserver
{
    /// <summary>How long after its last log line a character still counts as logged in.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(60);

    private sealed class Seen
    {
        public DateTime LastLineUtc;
        public DateTime LastCombatUtc;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Seen> _characters = new(StringComparer.OrdinalIgnoreCase);

    public void OnLogLine(string character, DateTime receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(character))
        {
            return;
        }

        lock (_gate)
        {
            Entry(character).LastLineUtc = receivedUtc;
        }
    }

    public void OnCombatLine(string character, DateTime receivedUtc)
    {
        if (string.IsNullOrWhiteSpace(character))
        {
            return;
        }

        lock (_gate)
        {
            Seen seen = Entry(character);
            seen.LastCombatUtc = receivedUtc;
            if (receivedUtc > seen.LastLineUtc)
            {
                seen.LastLineUtc = receivedUtc;
            }
        }
    }

    /// <summary>True when a line was written to <paramref name="character"/>'s log in the last <see cref="ActiveWindow"/>.</summary>
    public bool IsActive(string character, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(character))
        {
            return false;
        }

        lock (_gate)
        {
            return _characters.TryGetValue(character, out Seen? seen) && IsRecent(seen.LastLineUtc, nowUtc);
        }
    }

    /// <summary>True when at least one character is active.</summary>
    public bool AnyActive(DateTime nowUtc)
    {
        lock (_gate)
        {
            return _characters.Values.Any(s => IsRecent(s.LastLineUtc, nowUtc));
        }
    }

    /// <summary>The newest combat line among the active characters, or null when none of them has fought.</summary>
    public DateTime? LastCombatUtc(DateTime nowUtc)
    {
        lock (_gate)
        {
            DateTime best = default;
            foreach (Seen seen in _characters.Values)
            {
                if (IsRecent(seen.LastLineUtc, nowUtc) && seen.LastCombatUtc > best)
                {
                    best = seen.LastCombatUtc;
                }
            }

            return best == default ? null : best;
        }
    }

    private static bool IsRecent(DateTime lastUtc, DateTime nowUtc) => lastUtc != default && nowUtc - lastUtc <= ActiveWindow;

    private Seen Entry(string character)
    {
        if (!_characters.TryGetValue(character, out Seen? seen))
        {
            seen = new Seen();
            _characters[character] = seen;
        }

        return seen;
    }
}
