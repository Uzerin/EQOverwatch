using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Progress;

/// <summary>One skill of a character: its value after the last skill-up, and the skill-ups made today (local time).</summary>
public sealed record SkillProgress(string Skill, int Value, int GainedToday, DateTime LastUtc);

/// <summary>One recipe: combines that made the item and combines that failed.</summary>
public sealed record TradeskillProgress(string Recipe, int Made, int Failed, DateTime LastUtc)
{
    public int Combines => Made + Failed;

    public double SuccessPercent => Combines == 0 ? 0 : 100.0 * Made / Combines;
}

/// <summary>
/// One faction of a character: the change since the app opened, all the changes the logs show, how many hits there were, the most
/// common size of a hit, the mob whose kills cause it most, and whether it is at its best (+1) or worst (-1).
/// </summary>
public sealed record FactionProgress(string Faction, int Session, long AllTime, int Hits, int UsualHit, string ComesFrom, int Limit, DateTime LastUtc);

/// <summary>
/// Skill-ups, tradeskill combines and faction changes per character (and server), from live lines and each log's history.
/// <list type="bullet">
/// <item><description>"You have become better at Offense! (212)" sets Offense to 212.</description></item>
/// <item><description>"You have fashioned the items together to create something new: X." made X; "You lacked the skills to fashion X." failed.</description></item>
/// <item><description>"Your faction standing with X has been adjusted by -2." changes X; "... could not possibly get any better/worse." marks its limit.
/// The mob is the one the same log saw killed in the two seconds before.</description></item>
/// </list>
/// </summary>
public sealed partial class ProgressStore : ITriggerLineSink
{
    private const int KillWindowSeconds = 2;
    private const int RecentUpSeconds = 2 * 24 * 3600;

    [GeneratedRegex(@"^You have become better at (?<skill>.+)! \((?<value>\d+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillRegex();

    [GeneratedRegex(@"^You have fashioned the items together to create [^:]+: (?<item>.+?)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex MadeRegex();

    [GeneratedRegex(@"^You lacked the skills to fashion (?<item>.+?)\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex FailedRegex();

    [GeneratedRegex(@"^Your faction standing with (?<faction>.+?) has been adjusted by (?<amount>-?\d+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex FactionRegex();

    [GeneratedRegex(@"^Your faction standing with (?<faction>.+?) could not possibly get any (?<way>better|worse)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex FactionLimitRegex();

    [GeneratedRegex(@"^(?:You have slain (?<mob>.+)|(?<mob>.+?) (?:was|has been) slain by .+)!$", RegexOptions.CultureInvariant)]
    private static partial Regex KillRegex();

    private readonly object _gate = new();
    private readonly Dictionary<string, CharacterState> _characters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Time, string Mob)> _lastKill = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Message, long Time)>> _liveDuringScan = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _sessionStart;
    private Func<string, bool> _isPlayer = _ => false;
    private string? _filePath;
    private bool _dirty;
    private long _version;

    public ProgressStore(DateTime? sessionStartUtc = null) =>
        _sessionStart = LogHistoryReader.ToUnix(sessionStartUtc ?? DateTime.UtcNow);

    /// <summary>Goes up whenever anything changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>True for players and pets, whose deaths are not the kills that move faction.</summary>
    public void SetPlayerProbe(Func<string, bool> isPlayer) => _isPlayer = isPlayer;

    /// <summary>True for a line this store reads (a cheap test, used before parsing a log line's time).</summary>
    public static bool Wanted(string message) =>
        message.StartsWith("You have become better at ", StringComparison.Ordinal)
        || message.StartsWith("Your faction standing with ", StringComparison.Ordinal)
        || message.StartsWith("You have fashioned the items", StringComparison.Ordinal)
        || message.StartsWith("You lacked the skills to fashion ", StringComparison.Ordinal)
        || (message.Length > 10 && message[^1] == '!' && message.Contains(" slain ", StringComparison.Ordinal));

    // ───────────────────────── live lines ─────────────────────────

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
        OnLine(characterName, string.Empty, message, receivedUtc);

    public void OnLine(string characterName, string serverName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        string text = message.ToString();
        if (!Wanted(text))
        {
            return;
        }

        string key = Key(characterName, serverName);
        long time = LogHistoryReader.ToUnix(receivedUtc);
        lock (_gate)
        {
            if (_liveDuringScan.TryGetValue(key, out List<(string, long)>? held))
            {
                held.Add((text, time));
            }

            if (Read(_characters, _lastKill, key, text, time))
            {
                Changed();
            }
        }
    }

    /// <summary>Applies one line to <paramref name="states"/>. True when something was counted.</summary>
    private bool Read(Dictionary<string, CharacterState> states, Dictionary<string, (long Time, string Mob)> kills, string key, string text, long time)
    {
        if (text[^1] == '!' && KillRegex().Match(text) is { Success: true } kill)
        {
            string mob = kill.Groups["mob"].Value.Trim();
            if (mob.StartsWith("A ", StringComparison.Ordinal) || mob.StartsWith("An ", StringComparison.Ordinal))
            {
                mob = char.ToLowerInvariant(mob[0]) + mob[1..]; // "A temple guard" as the corpse and con lines write it
            }

            if (!_isPlayer(mob))
            {
                kills[key] = (time, mob);
            }

            return false;
        }

        CharacterState state = State(states, key);
        if (SkillRegex().Match(text) is { Success: true } skill
            && int.TryParse(skill.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            string name = skill.Groups["skill"].Value.Trim();
            if (!state.Skills.TryGetValue(name, out SkillState? s))
            {
                state.Skills[name] = s = new SkillState();
            }

            s.Value = value;
            s.Last = Math.Max(s.Last, time);
            s.Ups.Add(time);
            s.Ups.RemoveAll(t => t < time - RecentUpSeconds);
            return true;
        }

        Match made = MadeRegex().Match(text);
        Match failed = made.Success ? Match.Empty : FailedRegex().Match(text);
        if (made.Success || failed.Success)
        {
            string item = (made.Success ? made : failed).Groups["item"].Value.Trim();
            if (!state.Recipes.TryGetValue(item, out RecipeState? r))
            {
                state.Recipes[item] = r = new RecipeState();
            }

            if (made.Success)
            {
                r.Made++;
            }
            else
            {
                r.Failed++;
            }

            r.Last = Math.Max(r.Last, time);
            return true;
        }

        Match faction = FactionRegex().Match(text);
        Match limit = faction.Success ? Match.Empty : FactionLimitRegex().Match(text);
        if (!faction.Success && !limit.Success)
        {
            return false;
        }

        string factionName = (faction.Success ? faction : limit).Groups["faction"].Value.Trim();
        if (!state.Factions.TryGetValue(factionName, out FactionState? f))
        {
            state.Factions[factionName] = f = new FactionState();
        }

        if (kills.TryGetValue(key, out (long Time, string Mob) last) && time - last.Time <= KillWindowSeconds && last.Mob.Length > 0)
        {
            f.Mobs[last.Mob] = f.Mobs.GetValueOrDefault(last.Mob) + 1;
        }

        f.Last = Math.Max(f.Last, time);
        if (faction.Success && int.TryParse(faction.Groups["amount"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
        {
            f.AllTime += amount;
            f.Hits++;
            f.Amounts[amount] = f.Amounts.GetValueOrDefault(amount) + 1;
            f.Limit = 0;
            if (time >= _sessionStart)
            {
                f.Session += amount;
            }
        }
        else
        {
            f.Limit = limit.Groups["way"].Value == "better" ? 1 : -1;
        }

        return true;
    }

    // ───────────────────────── log history ─────────────────────────

    public bool IsScanned(string character, string server)
    {
        lock (_gate)
        {
            return _scanned.Contains(Key(character, server));
        }
    }

    /// <summary>Starts reading a log's history: lines go to <see cref="LogScan.Line"/>, and <see cref="LogScan.Commit"/> replaces what that log gave before.</summary>
    public LogScan BeginScan(string character, string server)
    {
        string key = Key(character, server);
        lock (_gate)
        {
            _liveDuringScan[key] = new List<(string, long)>();
        }

        return new LogScan(this, key);
    }

    /// <summary>One log being read from the start.</summary>
    public sealed class LogScan
    {
        private readonly ProgressStore _store;
        private readonly string _key;
        private readonly Dictionary<string, CharacterState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (long Time, string Mob)> _kills = new(StringComparer.OrdinalIgnoreCase);
        private long _last;

        internal LogScan(ProgressStore store, string key)
        {
            _store = store;
            _key = key;
        }

        public void Line(string message, long time)
        {
            if (Wanted(message))
            {
                _store.Read(_states, _kills, _key, message, time);
                _last = time;
            }
        }

        /// <summary>Puts what was read in place of the log's earlier numbers, then adds the live lines newer than the end of what was read.</summary>
        public void Commit(bool ok)
        {
            lock (_store._gate)
            {
                _store._liveDuringScan.Remove(_key, out List<(string Message, long Time)>? live);
                if (!ok)
                {
                    return;
                }

                CharacterState state = _states.GetValueOrDefault(_key) ?? new CharacterState();
                _store._characters[_key] = state;
                foreach ((string message, long time) in live ?? new List<(string, long)>())
                {
                    if (time > _last + 1)
                    {
                        _store.Read(_store._characters, _store._lastKill, _key, message, time);
                    }
                }

                _store._scanned.Add(_key);
                _store.Changed();
            }
        }
    }

    // ───────────────────────── reading the numbers ─────────────────────────

    /// <summary>A character's skills, A to Z.</summary>
    public IReadOnlyList<SkillProgress> Skills(string character, string server, DateTime nowUtc)
    {
        DateTime today = nowUtc.ToLocalTime().Date;
        lock (_gate)
        {
            if (!_characters.TryGetValue(Key(character, server), out CharacterState? state))
            {
                return Array.Empty<SkillProgress>();
            }

            return state.Skills
                .Select(p => new SkillProgress(p.Key, p.Value.Value,
                    p.Value.Ups.Count(t => LogHistoryReader.FromUnix(t).ToLocalTime().Date == today),
                    LogHistoryReader.FromUnix(p.Value.Last)))
                .OrderBy(s => s.Skill, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>A character's recipes, A to Z.</summary>
    public IReadOnlyList<TradeskillProgress> Tradeskills(string character, string server)
    {
        lock (_gate)
        {
            if (!_characters.TryGetValue(Key(character, server), out CharacterState? state))
            {
                return Array.Empty<TradeskillProgress>();
            }

            return state.Recipes
                .Select(p => new TradeskillProgress(p.Key, p.Value.Made, p.Value.Failed, LogHistoryReader.FromUnix(p.Value.Last)))
                .OrderBy(r => r.Recipe, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>A character's factions, A to Z.</summary>
    public IReadOnlyList<FactionProgress> Factions(string character, string server)
    {
        lock (_gate)
        {
            if (!_characters.TryGetValue(Key(character, server), out CharacterState? state))
            {
                return Array.Empty<FactionProgress>();
            }

            return state.Factions
                .Select(p => new FactionProgress(
                    p.Key,
                    p.Value.Session,
                    p.Value.AllTime,
                    p.Value.Hits,
                    p.Value.Amounts.Count == 0 ? 0 : p.Value.Amounts.MaxBy(a => a.Value).Key,
                    p.Value.Mobs.Count == 0 ? string.Empty : p.Value.Mobs.MaxBy(m => m.Value).Key,
                    p.Value.Limit,
                    LogHistoryReader.FromUnix(p.Value.Last)))
                .OrderBy(f => f.Faction, StringComparer.OrdinalIgnoreCase)
                .ToList();
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
                    foreach (string key in saved.Scanned)
                    {
                        _scanned.Add(key);
                    }

                    foreach ((string key, CharacterState state) in saved.Characters)
                    {
                        foreach (FactionState f in state.Factions.Values)
                        {
                            f.Session = 0; // a new session starts with the app
                        }

                        _characters[key] = state;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged file starts empty
            }
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>Writes the file when anything changed since the last write.</summary>
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
            json = JsonSerializer.Serialize(new StoreFile
            {
                Scanned = _scanned.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                Characters = new Dictionary<string, CharacterState>(_characters, StringComparer.OrdinalIgnoreCase),
            });
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

    private void Changed()
    {
        _dirty = true;
        Interlocked.Increment(ref _version);
    }

    private static CharacterState State(Dictionary<string, CharacterState> states, string key)
    {
        if (!states.TryGetValue(key, out CharacterState? state))
        {
            states[key] = state = new CharacterState();
        }

        return state;
    }

    private static string Key(string character, string server) => character + "|" + server.ToLowerInvariant();

    // ───────────────────────── saved shapes ─────────────────────────

    public sealed class StoreFile
    {
        public List<string> Scanned { get; set; } = new();

        public Dictionary<string, CharacterState> Characters { get; set; } = new();
    }

    public sealed class CharacterState
    {
        public Dictionary<string, SkillState> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, RecipeState> Recipes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, FactionState> Factions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SkillState
    {
        public int Value { get; set; }

        public long Last { get; set; }

        /// <summary>Skill-up times of the last two days, for "today".</summary>
        public List<long> Ups { get; set; } = new();
    }

    public sealed class RecipeState
    {
        public int Made { get; set; }

        public int Failed { get; set; }

        public long Last { get; set; }
    }

    public sealed class FactionState
    {
        public long AllTime { get; set; }

        public int Hits { get; set; }

        public int Limit { get; set; }

        public long Last { get; set; }

        public Dictionary<int, int> Amounts { get; set; } = new();

        public Dictionary<string, int> Mobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [System.Text.Json.Serialization.JsonIgnore]
        public int Session { get; set; }
    }
}
