namespace EQOverwatch.Core.Pipeline;

/// <summary>Told about every live log line, so the app can tell which characters are logged in and fighting.</summary>
public interface ILogActivityObserver
{
    /// <summary>Any line was written to <paramref name="character"/>'s log.</summary>
    void OnLogLine(string character, DateTime receivedUtc);

    /// <summary>Any line was written to <paramref name="character"/>'s log on <paramref name="server"/> (one name can be a character on several servers).</summary>
    void OnLogLine(string character, string server, DateTime receivedUtc) => OnLogLine(character, receivedUtc);

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

    /// <summary>Last line per "name|server", so Ream on one server does not make Ream on another look logged in.</summary>
    private readonly Dictionary<string, DateTime> _onServer = new(StringComparer.OrdinalIgnoreCase);

    public void OnLogLine(string character, string server, DateTime receivedUtc)
    {
        OnLogLine(character, receivedUtc);
        if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(server))
        {
            return;
        }

        lock (_gate)
        {
            _onServer[character + "|" + server] = receivedUtc;
        }
    }

    /// <summary>
    /// True when a line was written to <paramref name="character"/>'s log on <paramref name="server"/> in the last
    /// <see cref="ActiveWindow"/>. With no server, any server counts.
    /// </summary>
    public bool IsActive(string character, string? server, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return IsActive(character, nowUtc);
        }

        lock (_gate)
        {
            return _onServer.TryGetValue(character + "|" + server, out DateTime last) && IsRecent(last, nowUtc);
        }
    }

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

    /// <summary>The character (and server) whose log had the newest line, while it is still logged in; null when nobody is.</summary>
    public (string Character, string Server)? MostRecentActive(DateTime nowUtc)
    {
        lock (_gate)
        {
            (string Character, string Server)? best = null;
            DateTime newest = default;
            foreach ((string key, DateTime last) in _onServer)
            {
                if (last > newest && IsRecent(last, nowUtc))
                {
                    int bar = key.IndexOf('|');
                    newest = last;
                    best = (key[..bar], key[(bar + 1)..]);
                }
            }

            return best;
        }
    }

    /// <summary>When each character's log last had a line (UTC).</summary>
    public IReadOnlyList<(string Character, DateTime LastLineUtc)> LastLines()
    {
        lock (_gate)
        {
            return _characters.Select(p => (p.Key, p.Value.LastLineUtc)).ToList();
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

    /// <summary>The character whose log had the newest line, or null before any line arrived.</summary>
    public string? MostRecentCharacter()
    {
        lock (_gate)
        {
            string? best = null;
            DateTime newest = default;
            foreach ((string name, Seen seen) in _characters)
            {
                if (seen.LastLineUtc > newest)
                {
                    newest = seen.LastLineUtc;
                    best = name;
                }
            }

            return best;
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
