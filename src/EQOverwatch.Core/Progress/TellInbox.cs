using System.Text.Json;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;

namespace EQOverwatch.Core.Progress;

/// <summary>A tell a player sent to one of your characters.</summary>
public sealed record TellEntry(string Character, string Server, string From, string Text, DateTime Utc, bool Answered, bool Read)
{
    /// <summary>Still waiting: not answered and not marked read.</summary>
    public bool IsUnanswered => !Answered && !Read;
}

/// <summary>
/// Every player tell to your characters ("Jannder tells you, 'hi'"), kept for a number of days you choose. A tell counts as answered
/// once the same character sends that player a tell ("You told Jannder, 'np'"). Merchants and pets ("... told you, ...") and tells
/// between your own characters are left out.
/// </summary>
public sealed partial class TellInbox : ITriggerLineSink
{
    /// <summary>Tells read from a log's history older than this count as already seen.</summary>
    public static readonly TimeSpan HistoryUnreadWindow = TimeSpan.FromHours(12);

    private const int MaxTells = 5000;

    [GeneratedRegex(@"^You told (?<to>[A-Za-z]+), '", RegexOptions.CultureInvariant)]
    private static partial Regex ToldRegex();

    private readonly object _gate = new();
    private readonly List<Tell> _tells = new();
    private readonly HashSet<string> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Message, long Time)>> _liveDuringScan = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, bool> _isOwnCharacter = _ => false;
    private int _keepDays = 30;
    private string? _filePath;
    private bool _dirty;
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    /// <summary>Days tells are kept; 0 keeps them for good.</summary>
    public int KeepDays
    {
        get => _keepDays;
        set
        {
            lock (_gate)
            {
                _keepDays = Math.Max(0, value);
                Prune(DateTime.UtcNow);
            }
        }
    }

    /// <summary>True for your own characters, whose tells to each other are not listed.</summary>
    public void SetOwnCharacterProbe(Func<string, bool> isOwn) => _isOwnCharacter = isOwn;

    public static bool Wanted(string message) =>
        message.StartsWith("You told ", StringComparison.Ordinal) || message.Contains(" tells you, '", StringComparison.Ordinal);

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
        OnLine(characterName, string.Empty, message, receivedUtc);

    public void OnLine(string characterName, string serverName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        string text = message.ToString();
        if (!Wanted(text))
        {
            return;
        }

        long time = LogHistoryReader.ToUnix(receivedUtc);
        lock (_gate)
        {
            string key = Key(characterName, serverName);
            if (_liveDuringScan.TryGetValue(key, out List<(string, long)>? held))
            {
                held.Add((text, time));
            }

            if (Read(_tells, characterName, serverName, text, time, read: false))
            {
                Prune(DateTime.UtcNow);
                Changed();
            }
        }
    }

    private bool Read(List<Tell> into, string character, string server, string text, long time, bool read)
    {
        if (ToldRegex().Match(text) is { Success: true } told)
        {
            string to = told.Groups["to"].Value;
            bool any = false;
            foreach (Tell t in into)
            {
                if (!t.Answered && t.Time <= time && Same(t, character, server) && t.From.Equals(to, StringComparison.OrdinalIgnoreCase))
                {
                    t.Answered = true;
                    any = true;
                }
            }

            return any;
        }

        if (!TellLineParser.TryParse(text, out string from, out string message) || _isOwnCharacter(from))
        {
            return false;
        }

        into.Add(new Tell
        {
            Character = character, Server = server.ToLowerInvariant(), From = from, Text = message, Time = time, Read = read,
        });
        return true;
    }

    /// <summary>The tells kept, newest first.</summary>
    public IReadOnlyList<TellEntry> All()
    {
        lock (_gate)
        {
            return _tells.OrderByDescending(t => t.Time)
                .Select(t => new TellEntry(t.Character, t.Server, t.From, t.Text, LogHistoryReader.FromUnix(t.Time), t.Answered, t.Read))
                .ToList();
        }
    }

    public int UnansweredCount()
    {
        lock (_gate)
        {
            return _tells.Count(t => !t.Answered && !t.Read);
        }
    }

    /// <summary>Marks every tell as seen, so none counts as unanswered.</summary>
    public void MarkAllRead()
    {
        lock (_gate)
        {
            foreach (Tell t in _tells)
            {
                t.Read = true;
            }

            Changed();
        }
    }

    // ───────────────────────── log history ─────────────────────────

    public bool IsScanned(string character, string server)
    {
        lock (_gate)
        {
            return _scanned.Contains(Key(character, server));
        }
    }

    public LogScan BeginScan(string character, string server)
    {
        lock (_gate)
        {
            _liveDuringScan[Key(character, server)] = new List<(string, long)>();
        }

        return new LogScan(this, character, server);
    }

    /// <summary>One log being read from the start. Tells older than the keep time are skipped as they are read.</summary>
    public sealed class LogScan
    {
        private readonly TellInbox _inbox;
        private readonly string _character;
        private readonly string _server;
        private readonly List<Tell> _found = new();
        private readonly long _oldest;
        private readonly long _unreadFrom;
        private long _last;

        internal LogScan(TellInbox inbox, string character, string server)
        {
            _inbox = inbox;
            _character = character;
            _server = server;
            DateTime now = DateTime.UtcNow;
            _oldest = inbox._keepDays == 0 ? long.MinValue : LogHistoryReader.ToUnix(now.AddDays(-inbox._keepDays));
            _unreadFrom = LogHistoryReader.ToUnix(now - HistoryUnreadWindow);
        }

        public void Line(string message, long time)
        {
            if (time >= _oldest && Wanted(message))
            {
                _inbox.Read(_found, _character, _server, message, time, read: time < _unreadFrom);
                _last = time;
            }
        }

        /// <summary>Puts what was read in place of the log's older tells, keeping the live ones newer than the end of what was read.</summary>
        public void Commit(bool ok)
        {
            lock (_inbox._gate)
            {
                string key = Key(_character, _server);
                _inbox._liveDuringScan.Remove(key, out List<(string Message, long Time)>? live);
                if (!ok)
                {
                    return;
                }

                long last = _last;
                _inbox._tells.RemoveAll(t => Same(t, _character, _server) && t.Time <= last + 1);
                _inbox._tells.AddRange(_found);
                foreach ((string message, long time) in live ?? new List<(string, long)>())
                {
                    if (time > last + 1 && message.StartsWith("You told ", StringComparison.Ordinal))
                    {
                        _inbox.Read(_inbox._tells, _character, _server, message, time, read: false);
                    }
                }

                _inbox._scanned.Add(key);
                _inbox.Prune(DateTime.UtcNow);
                _inbox.Changed();
            }
        }
    }

    // ───────────────────────── saving ─────────────────────────

    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _filePath = path;
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path)) is { } saved)
                {
                    _scanned.UnionWith(saved.Scanned);
                    _tells.AddRange(saved.Tells);
                    Prune(DateTime.UtcNow);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        Interlocked.Increment(ref _version);
    }

    public void Flush()
    {
        string json;
        string path;
        lock (_gate)
        {
            if (!_dirty || _filePath is null)
            {
                return;
            }

            _dirty = false;
            path = _filePath;
            json = JsonSerializer.Serialize(new StoreFile { Scanned = _scanned.ToList(), Tells = _tells.ToList() });
        }

        try
        {
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    private void Prune(DateTime nowUtc)
    {
        if (_keepDays > 0)
        {
            long oldest = LogHistoryReader.ToUnix(nowUtc.AddDays(-_keepDays));
            if (_tells.RemoveAll(t => t.Time < oldest) > 0)
            {
                Changed();
            }
        }

        if (_tells.Count > MaxTells)
        {
            _tells.Sort((a, b) => a.Time.CompareTo(b.Time));
            _tells.RemoveRange(0, _tells.Count - MaxTells);
            Changed();
        }
    }

    private void Changed()
    {
        _dirty = true;
        Interlocked.Increment(ref _version);
    }

    private static bool Same(Tell t, string character, string server) =>
        t.Character.Equals(character, StringComparison.OrdinalIgnoreCase) && t.Server.Equals(server, StringComparison.OrdinalIgnoreCase);

    private static string Key(string character, string server) => character + "|" + server.ToLowerInvariant();

    public sealed class Tell
    {
        public string Character { get; set; } = string.Empty;

        public string Server { get; set; } = string.Empty;

        public string From { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public long Time { get; set; }

        public bool Answered { get; set; }

        public bool Read { get; set; }
    }

    public sealed class StoreFile
    {
        public List<string> Scanned { get; set; } = new();

        public List<Tell> Tells { get; set; } = new();
    }
}
