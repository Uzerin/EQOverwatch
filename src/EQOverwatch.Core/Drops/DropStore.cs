using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Drops;

/// <summary>One item under a mob in the drop table: how many times it dropped.</summary>
public sealed record DropItemRow(string Item, int Drops);

/// <summary>A mob in the drop table: its zone, how many times it was killed, and what it dropped.</summary>
public sealed record DropMobRow(string Zone, string Mob, int Kills, IReadOnlyList<DropItemRow> Items);

/// <summary>Drops imported from someone else's file.</summary>
public sealed record DropSource(string Tag, string Server, int Kills, int Drops, DateTime ImportedUtc);

/// <summary>Which kills and drops the table counts.</summary>
public enum DropScope
{
    /// <summary>Only this PC's logs.</summary>
    Mine,

    /// <summary>This PC's logs and drops files imported by hand.</summary>
    ImportedFiles,

    /// <summary>All of that and the files other players shared online.</summary>
    Everyone,
}

/// <summary>The result of reading a drops file.</summary>
public sealed record DropImportResult(string Tag, string Server, int Kills, int Drops, bool Replaced);

/// <summary>
/// The drop table: which mobs drop which items and how often, per server (random-loot servers never mix with others).
/// <para>Kills come from "You have slain X!" and "X was slain by Y!", drops from "--Y has looted an item from X's corpse.--". An
/// advanced-loot item that nobody looted (a roll won with no loot line after it, or "you already have one and it is lore") counts as a
/// drop when only one mob was killed just before it.</para>
/// <para>The same kill or drop seen by several logs counts once: for each mob (and item), lines a few seconds apart are one bunch, and a
/// bunch counts as many as the most any single log saw in it. Two orc pawns killed together still count two. Imported files from other
/// people are counted by the same rule, so a fight two people logged counts once.</para>
/// </summary>
public sealed partial class DropStore : ITriggerLineSink
{
    /// <summary>Lines of the same mob (and item) closer than this are one bunch.</summary>
    public const int BunchSeconds = 5;

    /// <summary>How long an advanced-loot item waits for its loot line before it counts as left on the corpse.</summary>
    public const int SeenWaitSeconds = 10;

    /// <summary>How far back a kill can be for an item left on the corpse to be put down to it.</summary>
    public const int SeenKillSeconds = 30;

    private const string FileFormat = "EQOverwatchDrops";
    private const int MaxImportEvents = 3_000_000;
    private const long MaxSharedBytes = 64L * 1024 * 1024; // a shared file once unpacked

    /// <summary>A shared drop counts only when the same file has a kill of that mob this many seconds before it (or just after).</summary>
    public const int SharedKillBackingSeconds = 120;
    private const int OwnSource = 0;
    private const int OwnObserver = 0; // this PC's events once its logs' copies are merged

    [GeneratedRegex(@"^(?:You have slain (?<mob>.+)|(?<mob>.+?) (?:was|has been) slain by .+)!$", RegexOptions.CultureInvariant)]
    private static partial Regex KillRegex();

    [GeneratedRegex(@"^--(?<looter>.+?) (?:has|have) looted (?<item>.+?) from (?<mob>.+?)(?:'s corpse)?\.--$", RegexOptions.CultureInvariant)]
    private static partial Regex LootRegex();

    // "--a Shissar Fangs was left on Eom Centien's corpse.--": an item nobody took.
    [GeneratedRegex(@"^--(?<item>.+?) (?:was|were) left on (?<mob>.+?)'s corpse\.--$", RegexOptions.CultureInvariant)]
    private static partial Regex LeftRegex();

    /// <summary>Goes up when the way logs are read changes, so every log is read again once.</summary>
    private const int ReadRules = 2;

    [GeneratedRegex(@"^\S+ won the (?:need|greed) roll on \d+ item\(s\): (?<item>.+) with a roll of \d+\.$", RegexOptions.CultureInvariant)]
    private static partial Regex RollWonRegex();

    [GeneratedRegex(@"^You cannot choose to need or greed on (?<item>.+) because you already have one and it is lore\.$", RegexOptions.CultureInvariant)]
    private static partial Regex LoreRegex();

    [GeneratedRegex(@"^(?:\d+ )?(?:platinum|gold|silver|copper)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CoinRegex();

    private readonly object _gate = new();
    private readonly Names _names = new();
    private readonly List<Event> _events = new();
    private readonly List<Source> _sources = new() { new Source(string.Empty, string.Empty, DateTime.MinValue) };
    private readonly List<string> _observers = new() { string.Empty };
    private readonly Dictionary<string, int> _observerIndex = new(StringComparer.OrdinalIgnoreCase) { [string.Empty] = OwnObserver };
    private readonly Dictionary<string, LogState> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Event>> _liveDuringScan = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, string, string?> _zoneProvider = (_, _) => null;
    private Func<string, bool> _isPlayer = _ => false;
    private string? _filePath;
    private bool _dirty;
    private long _version;
    private string _tag = NewTag();

    /// <summary>Goes up whenever the table changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>The name this install's drops go out under in files it exports: random, and changeable; never a character name.</summary>
    public string Tag
    {
        get
        {
            lock (_gate)
            {
                return _tag;
            }
        }
    }

    /// <summary>The zone a character is in (live lines), from the zone tracker.</summary>
    public void SetZoneProvider(Func<string, string, string?> zoneProvider) => _zoneProvider = zoneProvider;

    /// <summary>True for players and pets, whose deaths are not mob kills.</summary>
    public void SetPlayerProbe(Func<string, bool> isPlayer) => _isPlayer = isPlayer;

    /// <summary>Sets the tag (letters, digits, spaces, - and _, at most 24 characters). False when it is not allowed.</summary>
    public bool SetTag(string tag)
    {
        string text = tag.Trim();
        if (text.Length is 0 or > 24 || !text.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' '))
        {
            return false;
        }

        lock (_gate)
        {
            _tag = text;
            _dirty = true;
        }

        return true;
    }

    public static string NewTag()
    {
        string[] words = { "Aqua", "Brim", "Cinder", "Dusk", "Ember", "Frost", "Gale", "Hollow", "Iron", "Jade", "Kel", "Lumen", "Moss", "Nox", "Onyx", "Pyre", "Quill", "Rune", "Sable", "Thorn", "Umber", "Vex", "Wisp", "Zeph" };
        return $"{words[RandomNumberGenerator.GetInt32(words.Length)]}-{RandomNumberGenerator.GetInt32(0x1000):X3}";
    }

    // ───────────────────────── live lines ─────────────────────────

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
        OnLine(characterName, string.Empty, message, receivedUtc);

    public void OnLine(string characterName, string serverName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (!Interesting(message))
        {
            return;
        }

        string key = LogKey(characterName, serverName);
        lock (_gate)
        {
            if (!_live.TryGetValue(key, out LogState? state))
            {
                _live[key] = state = new LogState(characterName, serverName);
            }

            string zone = _zoneProvider(characterName, serverName) ?? string.Empty;
            List<Event> into = _liveDuringScan.TryGetValue(key, out List<Event>? held) ? held : _events;
            int before = into.Count;
            Read(state, message, ToUnix(receivedUtc), zone, ObserverFor(key), into);
            if (into.Count != before && ReferenceEquals(into, _events))
            {
                Changed();
            }
        }
    }

    /// <summary>Counts advanced-loot items whose loot line never came as left on the corpse. Call from a timer.</summary>
    public void ResolveExpired(DateTime nowUtc)
    {
        long now = ToUnix(nowUtc);
        lock (_gate)
        {
            int before = _events.Count;
            foreach ((string key, LogState state) in _live)
            {
                if (!_liveDuringScan.ContainsKey(key))
                {
                    Expire(state, now, ObserverFor(key), _events);
                }
            }

            if (_events.Count != before)
            {
                Changed();
            }
        }
    }

    private static bool Interesting(ReadOnlySpan<char> message) =>
        message.Length > 10
        && ((message[^1] == '!' && message.Contains(" slain ", StringComparison.Ordinal))
            || message.StartsWith("--", StringComparison.Ordinal)
            || message.Contains(" roll on ", StringComparison.Ordinal)
            || message.StartsWith("You cannot choose to need or greed on ", StringComparison.Ordinal));

    /// <summary>Reads one line of a log into events.</summary>
    private void Read(LogState state, ReadOnlySpan<char> message, long time, string zone, int observer, List<Event> into)
    {
        Expire(state, time, observer, into);
        string text = message.ToString();
        if (text[^1] == '!' && KillRegex().Match(text) is { Success: true } kill)
        {
            string mob = kill.Groups["mob"].Value.Trim();
            if (mob.Length == 0 || mob.Equals("You", StringComparison.OrdinalIgnoreCase) || mob.Contains("'s corpse", StringComparison.Ordinal)
                || _isPlayer(mob))
            {
                return;
            }

            int mobId = _names.Mob(mob);
            int zoneId = _names.Zone(zone);
            into.Add(new Event(EventKind.Kill, time, _names.Server(state.Server), zoneId, mobId, -1, observer, OwnSource));
            state.Kills.Add((time, mobId, zoneId));
            state.Kills.RemoveAll(k => k.Time < time - SeenKillSeconds);
            return;
        }

        if (text.StartsWith("--", StringComparison.Ordinal) && LootRegex().Match(text) is { Success: true } loot)
        {
            string item = CleanItem(loot.Groups["item"].Value, out int stack);
            if (item.Length == 0 || CoinRegex().IsMatch(item))
            {
                return;
            }

            state.Pending.RemoveAll(p => string.Equals(p.Item, item, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < stack; i++)
            {
                into.Add(new Event(EventKind.Drop, time, _names.Server(state.Server), _names.Zone(zone), _names.Mob(loot.Groups["mob"].Value.Trim()),
                    _names.Item(item), observer, OwnSource));
            }

            return;
        }

        if (text.StartsWith("--", StringComparison.Ordinal) && LeftRegex().Match(text) is { Success: true } left)
        {
            string item = CleanItem(left.Groups["item"].Value, out int stack);
            int mobId = _names.Mob(left.Groups["mob"].Value.Trim());
            int itemId = _names.Item(item);
            if (item.Length == 0 || CoinRegex().IsMatch(item) || state.Claimed.Any(c => c.Mob == mobId && c.Item == itemId))
            {
                return; // already counted as a won roll nobody looted
            }

            for (int i = 0; i < stack; i++)
            {
                into.Add(new Event(EventKind.Drop, time, _names.Server(state.Server), _names.Zone(zone), mobId, itemId, observer, OwnSource));
            }

            return;
        }

        Match seen = RollWonRegex().Match(text);
        if (!seen.Success)
        {
            seen = LoreRegex().Match(text);
        }

        if (seen.Success)
        {
            string item = CleanItem(seen.Groups["item"].Value);
            if (item.Length > 0 && !state.Pending.Any(p => string.Equals(p.Item, item, StringComparison.OrdinalIgnoreCase)))
            {
                state.Pending.Add((time, item));
            }
        }
    }

    /// <summary>Advanced-loot items still without a loot line after <see cref="SeenWaitSeconds"/> count as dropped by the one mob killed just before.</summary>
    private void Expire(LogState state, long now, int observer, List<Event> into)
    {
        for (int i = state.Pending.Count - 1; i >= 0; i--)
        {
            (long time, string item) = state.Pending[i];
            if (now - time < SeenWaitSeconds)
            {
                continue;
            }

            state.Pending.RemoveAt(i);
            var kills = state.Kills.Where(k => k.Time <= time && k.Time >= time - SeenKillSeconds).ToList();
            if (kills.Count == 0 || kills.Any(k => k.Mob != kills[0].Mob))
            {
                continue; // no kill, or more than one kind of mob: it can't be told which one dropped it
            }

            (long killTime, int mob, int zone) = kills[^1];
            int itemId = _names.Item(item);
            if (state.Claimed.Add((killTime, mob, itemId)))
            {
                into.Add(new Event(EventKind.Drop, time, _names.Server(state.Server), zone, mob, itemId, observer, OwnSource));
            }
        }

        if (state.Claimed.Count > 500)
        {
            state.Claimed.RemoveWhere(c => c.Time < now - 600);
        }
    }

    private static string CleanItem(string text) => CleanItem(text, out _);

    /// <summary>The item's name without "a", "an" or a count; <paramref name="stack"/> is the count ("3 Bone Chips" is 3).</summary>
    private static string CleanItem(string text, out int stack)
    {
        stack = 1;
        string item = text.Trim();
        if (item.StartsWith("a ", StringComparison.Ordinal))
        {
            item = item[2..];
        }
        else if (item.StartsWith("an ", StringComparison.Ordinal))
        {
            item = item[3..];
        }
        else
        {
            int space = item.IndexOf(' ');
            if (space is > 0 and <= 3 && item[..space].All(char.IsAsciiDigit))
            {
                stack = Math.Clamp(int.Parse(item[..space], System.Globalization.CultureInfo.InvariantCulture), 1, 100);
                item = item[(space + 1)..]; // "3 Bone Chips"
            }
        }

        return item.Trim();
    }

    // ───────────────────────── log history ─────────────────────────

    public bool IsScanned(string character, string server)
    {
        lock (_gate)
        {
            return _scanned.Contains(LogKey(character, server));
        }
    }

    /// <summary>
    /// Reads a whole log for its kills and drops and replaces what it gave before. Lines that arrive live while it reads are kept when
    /// they are newer than the end of what was read.
    /// </summary>
    public bool ScanLog(string path, string character, string server, CancellationToken cancellation = default)
    {
        string key = LogKey(character, server);
        int observer;
        lock (_gate)
        {
            _liveDuringScan[key] = new List<Event>();
            observer = ObserverFor(key);
        }

        var found = new List<Event>();
        long last = 0;
        bool ok = false;
        try
        {
            var state = new LogState(character, server);
            string zone = string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
            long count = 0;
            while (reader.ReadLine() is { } line)
            {
                if ((++count & 0xFFFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                if (line.Length <= LogLineFormat.MessageOffset)
                {
                    continue;
                }

                ReadOnlySpan<char> message = line.AsSpan(LogLineFormat.MessageOffset);
                bool entered = message.StartsWith("You have entered ", StringComparison.Ordinal);
                if (!entered && !Interesting(message))
                {
                    continue;
                }

                if (!LogLineFormat.TryParseTimestamp(line, out DateTime stamp))
                {
                    continue;
                }

                long time = ToUnix(DateTime.SpecifyKind(stamp, DateTimeKind.Local).ToUniversalTime());
                last = time;
                if (entered)
                {
                    zone = ZoneTracker.ParseZone(message) ?? zone;
                    continue;
                }

                lock (_gate)
                {
                    Read(state, message, time, zone, observer, found);
                }
            }

            lock (_gate)
            {
                Expire(state, long.MaxValue, observer, found);
            }

            ok = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
        }
        finally
        {
            lock (_gate)
            {
                _liveDuringScan.Remove(key, out List<Event>? live);
                if (ok)
                {
                    _events.RemoveAll(e => e.Observer == observer && e.Source == OwnSource);
                    _events.AddRange(found);
                    _events.AddRange((live ?? new List<Event>()).Where(e => e.Time > last + 1));
                    _scanned.Add(key);
                    Compact();
                    Changed();
                }
            }
        }

        if (ok)
        {
            Flush();
        }

        return ok;
    }

    // ───────────────────────── the table ─────────────────────────

    /// <summary>The servers there are drops for.</summary>
    /// <summary>
    /// What your logs saw drop from <paramref name="mob"/> after a kill that ended at <paramref name="killLocal"/> (local time): loot lines
    /// from just before it to <paramref name="afterSeconds"/> after, each item once per time it dropped. "Item" or "Item ×2".
    /// </summary>
    public IReadOnlyList<string> DropsAfterKill(string mob, DateTime killLocal, int afterSeconds = 300)
    {
        long kill = ToUnix(DateTime.SpecifyKind(killLocal, DateTimeKind.Local).ToUniversalTime());
        lock (_gate)
        {
            return _events
                .Where(e => e.Kind == EventKind.Drop && e.Source == OwnSource && e.Time >= kill - 30 && e.Time <= kill + afterSeconds
                            && _names.MobName(e.Mob).Equals(mob, StringComparison.OrdinalIgnoreCase))
                .Select(e => (Item: _names.ItemName(e.Item), Time: e.Time / 5)) // the same drop seen by several of your logs
                .Distinct()
                .GroupBy(d => d.Item, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)
                .ToList();
        }
    }

    public IReadOnlyList<string> Servers()
    {
        lock (_gate)
        {
            return _events.Where(e => e.Kind == EventKind.Drop).Select(e => e.Server).Concat(_merged.Keys).Distinct()
                .Select(_names.ServerName).Where(s => s.Length > 0)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>
    /// The drop table for one server: each mob that dropped something, with its kills and each item's drops, most-killed first. Kills
    /// come from the kill lines; a mob with drops but no kill line seen counts each round of looting (a gap of <see cref="LootRoundSeconds"/>)
    /// as one kill. One kill can drop several of the same item, so an item's drops can be more than the kills.
    /// </summary>
    public IReadOnlyList<DropMobRow> Table(string server, bool includeImported) =>
        Table(server, includeImported ? DropScope.Everyone : DropScope.Mine);

    /// <summary>
    /// The drop table for one server, counting the kills and drops in <paramref name="scope"/>. With <see cref="DropScope.Everyone"/>, the
    /// merged tables of <paramref name="pooled"/> (other servers whose loot works the same) are added too.
    /// </summary>
    public IReadOnlyList<DropMobRow> Table(string server, DropScope scope, IEnumerable<string>? pooled = null)
    {
        lock (_gate)
        {
            int serverId = _names.FindServer(server);
            if (serverId < 0)
            {
                return Array.Empty<DropMobRow>();
            }

            IEnumerable<Event> events = _events.Where(e => e.Server == serverId
                && (e.Source == OwnSource || scope == DropScope.Everyone || (scope == DropScope.ImportedFiles && !_sources[e.Source].Shared)));

            // With the merged table: everyone's counts from the last daily merge, plus this PC's own events since the upload it holds.
            Merged? merged = scope == DropScope.Everyone && _merged.TryGetValue(serverId, out Merged? m) ? m : null;
            if (merged is not null)
            {
                long since = OwnSinceLocked(serverId, merged);
                events = events.Where(e => (e.Source == OwnSource && e.Time > since) || (e.Source != OwnSource && !_sources[e.Source].Shared));
            }

            var kills = new Dictionary<(int Zone, int Mob), int>();
            var drops = new Dictionary<(int Zone, int Mob), Dictionary<int, int>>();
            var lootTimes = new Dictionary<(int Zone, int Mob), List<long>>();
            foreach (IGrouping<(EventKind Kind, int Zone, int Mob, int Item), Event> group in events.GroupBy(e => (e.Kind, e.Zone, e.Mob, e.Item)))
            {
                int count = Count(group);
                if (group.Key.Kind == EventKind.Kill)
                {
                    kills[(group.Key.Zone, group.Key.Mob)] = count;
                }
                else
                {
                    if (!drops.TryGetValue((group.Key.Zone, group.Key.Mob), out Dictionary<int, int>? items))
                    {
                        drops[(group.Key.Zone, group.Key.Mob)] = items = new Dictionary<int, int>();
                    }

                    items[group.Key.Item] = count;
                    if (!lootTimes.TryGetValue((group.Key.Zone, group.Key.Mob), out List<long>? times))
                    {
                        lootTimes[(group.Key.Zone, group.Key.Mob)] = times = new List<long>();
                    }

                    times.AddRange(group.Select(e => e.Time));
                }
            }

            var mergedTables = new List<Merged>();
            if (merged is not null)
            {
                mergedTables.Add(merged);
            }

            if (scope == DropScope.Everyone && pooled is not null)
            {
                foreach (string other in pooled.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    int otherId = _names.FindServer(other);
                    if (otherId >= 0 && otherId != serverId && _merged.TryGetValue(otherId, out Merged? o))
                    {
                        mergedTables.Add(o);
                    }
                }
            }

            foreach (Merged table in mergedTables)
            {
                foreach (((int zone, int mob), int count) in table.Kills)
                {
                    kills[(zone, mob)] = kills.GetValueOrDefault((zone, mob)) + count;
                }

                foreach (((int zone, int mob, int item), int count) in table.Drops)
                {
                    if (!drops.TryGetValue((zone, mob), out Dictionary<int, int>? items))
                    {
                        drops[(zone, mob)] = items = new Dictionary<int, int>();
                    }

                    items[item] = items.GetValueOrDefault(item) + count;
                }
            }

            var rows = new List<DropMobRow>();
            foreach (((int zone, int mob), Dictionary<int, int> items) in drops)
            {
                int killCount = kills.GetValueOrDefault((zone, mob));
                if (killCount <= 0)
                {
                    killCount = Math.Max(1, LootRounds(lootTimes.GetValueOrDefault((zone, mob))));
                }

                rows.Add(new DropMobRow(
                    _names.ZoneName(zone),
                    _names.MobName(mob),
                    killCount,
                    items.OrderByDescending(i => i.Value).ThenBy(i => _names.ItemName(i.Key), StringComparer.OrdinalIgnoreCase)
                        .Select(i => new DropItemRow(_names.ItemName(i.Key), i.Value)).ToList()));
            }

            return rows.OrderByDescending(r => r.Kills).ThenBy(r => r.Mob, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Looting of the same mob with a gap longer than this is another corpse.</summary>
    public const int LootRoundSeconds = 300;

    private static int LootRounds(List<long>? times)
    {
        if (times is null || times.Count == 0)
        {
            return 0;
        }

        times.Sort();
        int rounds = 1;
        for (int i = 1; i < times.Count; i++)
        {
            if (times[i] - times[i - 1] > LootRoundSeconds)
            {
                rounds++;
            }
        }

        return rounds;
    }

    /// <summary>
    /// How many events a group of the same kill (or drop) stands for: lines closer than <see cref="BunchSeconds"/> are one bunch, and a
    /// bunch counts as many as the most any single log (observer) saw in it.
    /// </summary>
    private static int Count(IEnumerable<Event> group)
    {
        int total = 0;
        var perObserver = new Dictionary<(int Source, int Observer), int>();
        long lastTime = long.MinValue;
        foreach (Event e in group.OrderBy(e => e.Time))
        {
            if (e.Time - lastTime > BunchSeconds && perObserver.Count > 0)
            {
                total += perObserver.Values.Max();
                perObserver.Clear();
            }

            perObserver[(e.Source, e.Observer)] = perObserver.GetValueOrDefault((e.Source, e.Observer)) + 1;
            lastTime = e.Time;
        }

        return perObserver.Count > 0 ? total + perObserver.Values.Max() : total;
    }

    /// <summary>
    /// Merges this PC's logs' copies of the same kills and drops into one (kept as this PC's events), so a kill seen by five boxed
    /// characters is stored once. Counting gives the same numbers before and after.
    /// </summary>
    private void Compact()
    {
        var own = _events.Where(e => e.Source == OwnSource).ToList();
        var merged = new List<Event>(own.Count / 2);
        foreach (IGrouping<(EventKind, int, int, int, int), Event> group in own.GroupBy(e => (e.Kind, e.Server, e.Zone, e.Mob, e.Item)))
        {
            var bunch = new List<Event>();
            long lastTime = long.MinValue;
            foreach (Event e in group.OrderBy(e => e.Time))
            {
                if (e.Time - lastTime > BunchSeconds && bunch.Count > 0)
                {
                    KeepMost(bunch, merged);
                    bunch.Clear();
                }

                bunch.Add(e);
                lastTime = e.Time;
            }

            KeepMost(bunch, merged);
        }

        _events.RemoveAll(e => e.Source == OwnSource);
        _events.AddRange(merged);

        // The events of the observer that saw most of the bunch stand for it.
        static void KeepMost(List<Event> bunch, List<Event> into)
        {
            if (bunch.Count == 0)
            {
                return;
            }

            IGrouping<int, Event> most = bunch.GroupBy(e => e.Observer).MaxBy(g => g.Count())!;
            into.AddRange(most.Select(e => e with { Observer = OwnObserver }));
        }
    }

    // ───────────────────────── files ─────────────────────────

    /// <summary>Writes this PC's own kills and drops on one server to a file others can import. Returns the number of drops written.</summary>
    public int Export(string server, string path)
    {
        string json;
        int dropCount;
        lock (_gate)
        {
            DropFile file = OwnFile(server, _tag);
            dropCount = file.Drops.Count;
            json = JsonSerializer.Serialize(file);
        }

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, json, new UTF8Encoding(false));
        return dropCount;
    }

    /// <summary>
    /// This PC's own kills and drops on one server as a gzip drops file for the online shared table, without the tag: everything since
    /// the last upload (with a few minutes' overlap, which the service ignores), or everything when nothing was uploaded yet. Null when
    /// nothing new happened since the last upload, or there are no drops on that server at all.
    /// </summary>
    public SharedDropsUpload? ExportShared(string server)
    {
        DropFile file;
        long through;
        lock (_gate)
        {
            int serverId = _names.FindServer(server);
            long last = serverId >= 0 ? LastUploadLocked(serverId)?.Through ?? long.MinValue : long.MinValue;
            if (last != long.MinValue && !_events.Any(e => e.Source == OwnSource && e.Server == serverId && e.Time > last))
            {
                return null;
            }

            file = OwnFile(server, string.Empty, last == long.MinValue ? long.MinValue : last - UploadOverlapSeconds);
            through = Math.Max(last, file.Kills.Concat(file.Drops).Select(e => e[0]).DefaultIfEmpty(long.MinValue).Max());
            if (last == long.MinValue && !_events.Any(e => e.Source == OwnSource && e.Server == serverId && e.Kind == EventKind.Drop))
            {
                return null;
            }
        }

        if (file.Kills.Count + file.Drops.Count == 0)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            JsonSerializer.Serialize(gzip, file);
        }

        return new SharedDropsUpload(file.Server, file.Kills.Count, file.Drops.Count, buffer.ToArray(), through);
    }

    /// <summary>This PC's own events on a server as a drops file. Call inside the lock.</summary>
    private DropFile OwnFile(string server, string tag, long since = long.MinValue)
    {
        Compact();
        int serverId = _names.FindServer(server);
        var file = new DropFile { Format = FileFormat, Version = 1, Tag = tag, Server = server.ToLowerInvariant(), ExportedUtc = DateTime.UtcNow };
        var zones = new Dictionary<int, int>();
        var mobs = new Dictionary<int, int>();
        var items = new Dictionary<int, int>();
        int Index(Dictionary<int, int> map, List<string> list, int id, string name)
        {
            if (!map.TryGetValue(id, out int index))
            {
                map[id] = index = list.Count;
                list.Add(name);
            }

            return index;
        }

        foreach (Event e in _events.Where(e => e.Source == OwnSource && e.Server == serverId && e.Time >= since).OrderBy(e => e.Time))
        {
            int zone = Index(zones, file.Zones, e.Zone, _names.ZoneName(e.Zone));
            int mob = Index(mobs, file.Mobs, e.Mob, _names.MobName(e.Mob));
            if (e.Kind == EventKind.Kill)
            {
                file.Kills.Add(new[] { e.Time, zone, mob });
            }
            else
            {
                file.Drops.Add(new[] { e.Time, zone, mob, Index(items, file.Items, e.Item, _names.ItemName(e.Item)) });
            }
        }

        return file;
    }

    /// <summary>
    /// Adds the kills and drops in someone's drops file. A newer file from the same tag and server replaces the older one. Throws
    /// <see cref="InvalidDataException"/> for a file that is not a drops file, or that is this install's own.
    /// </summary>
    public DropImportResult Import(string path)
    {
        DropFile? file;
        try
        {
            file = JsonSerializer.Deserialize<DropFile>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            throw new InvalidDataException("That isn't an EQ Overwatch drops file.");
        }

        if (file is null || file.Format != FileFormat || string.IsNullOrWhiteSpace(file.Server) || string.IsNullOrWhiteSpace(file.Tag)
            || file.Tag.StartsWith(SharedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("That isn't an EQ Overwatch drops file.");
        }

        if (file.Kills.Count + file.Drops.Count > MaxImportEvents)
        {
            throw new InvalidDataException("That drops file is too large.");
        }

        lock (_gate)
        {
            if (string.Equals(file.Tag, _tag, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("That file is your own export.");
            }

            int existing = _sources.FindIndex(s => !s.Shared && string.Equals(s.Tag, file.Tag, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Server, file.Server, StringComparison.OrdinalIgnoreCase));
            int source;
            if (existing > 0)
            {
                source = existing;
                _events.RemoveAll(e => e.Source == source);
                _sources[source] = new Source(file.Tag, file.Server, DateTime.UtcNow);
            }
            else
            {
                source = _sources.Count;
                _sources.Add(new Source(file.Tag, file.Server, DateTime.UtcNow));
            }

            int server = _names.Server(file.Server.Trim());
            int observer = ObserverFor("import:" + file.Tag);
            int kills = 0, drops = 0;
            foreach (long[] kill in file.Kills)
            {
                if (kill.Length >= 3 && Name(file.Zones, kill[1]) is { } zone && Name(file.Mobs, kill[2]) is { Length: > 0 } mob)
                {
                    _events.Add(new Event(EventKind.Kill, kill[0], server, _names.Zone(zone), _names.Mob(mob), -1, observer, source));
                    kills++;
                }
            }

            foreach (long[] drop in file.Drops)
            {
                if (drop.Length >= 4 && Name(file.Zones, drop[1]) is { } zone && Name(file.Mobs, drop[2]) is { Length: > 0 } mob
                    && Name(file.Items, drop[3]) is { Length: > 0 } item)
                {
                    _events.Add(new Event(EventKind.Drop, drop[0], server, _names.Zone(zone), _names.Mob(mob), _names.Item(item), observer, source));
                    drops++;
                }
            }

            Changed();
            return new DropImportResult(file.Tag, file.Server, kills, drops, existing > 0);
        }

        static string? Name(List<string> names, long index) => index >= 0 && index < names.Count ? names[(int)index] : null;
    }

    /// <summary>The files imported so far.</summary>
    public IReadOnlyList<DropSource> Sources()
    {
        lock (_gate)
        {
            var list = new List<DropSource>();
            for (int i = 1; i < _sources.Count; i++)
            {
                Source s = _sources[i];
                if (s.Tag.Length == 0 || s.Shared)
                {
                    continue; // removed, or shared online
                }

                int source = i;
                list.Add(new DropSource(s.Tag, s.Server, _events.Count(e => e.Source == source && e.Kind == EventKind.Kill),
                    _events.Count(e => e.Source == source && e.Kind == EventKind.Drop), s.ImportedUtc));
            }

            return list;
        }
    }

    /// <summary>Takes an imported file's kills and drops out of the table.</summary>
    public void RemoveSource(string tag, string server)
    {
        lock (_gate)
        {
            int source = _sources.FindIndex(s => !s.Shared && string.Equals(s.Tag, tag, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Server, server, StringComparison.OrdinalIgnoreCase));
            if (source > 0)
            {
                _events.RemoveAll(e => e.Source == source);
                _sources[source] = new Source(string.Empty, string.Empty, DateTime.MinValue);
                Changed();
            }
        }
    }

    // ───────────────────────── shared online ─────────────────────────

    private const string SharedPrefix = "shared:";

    /// <summary>The online files already read for a server: id and the upload time it had.</summary>
    public IReadOnlyDictionary<string, long> SharedFiles(string server)
    {
        lock (_gate)
        {
            return _sources.Where(s => s.Shared && string.Equals(s.Server, server, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(s => s.Tag, s => s.Updated, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Adds (or replaces) another player's online file. Drops count only when the same file has a kill of that mob just before them, so
    /// a file of made-up drops adds nothing. Returns the drops kept. Throws <see cref="InvalidDataException"/> for a damaged file.
    /// </summary>
    public int ImportShared(string id, string server, long updated, byte[] gzipped)
    {
        DropFile? file;
        try
        {
            using var input = new GZipStream(new MemoryStream(gzipped), CompressionMode.Decompress);
            using var limited = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (limited.Length + read > MaxSharedBytes)
                {
                    throw new InvalidDataException("That shared file is too large.");
                }

                limited.Write(chunk, 0, read);
            }

            limited.Position = 0;
            file = JsonSerializer.Deserialize<DropFile>(limited);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidDataException("That isn't an EQ Overwatch drops file.");
        }

        if (file is null || file.Format != FileFormat || file.Kills.Count + file.Drops.Count > MaxImportEvents)
        {
            throw new InvalidDataException("That isn't an EQ Overwatch drops file.");
        }

        // Kill times per (zone, mob), for the backing check.
        var killTimes = new Dictionary<(long Zone, long Mob), List<long>>();
        foreach (long[] kill in file.Kills)
        {
            if (kill.Length >= 3)
            {
                if (!killTimes.TryGetValue((kill[1], kill[2]), out List<long>? times))
                {
                    killTimes[(kill[1], kill[2])] = times = new List<long>();
                }

                times.Add(kill[0]);
            }
        }

        foreach (List<long> times in killTimes.Values)
        {
            times.Sort();
        }

        bool Backed(long time, long zone, long mob)
        {
            if (!killTimes.TryGetValue((zone, mob), out List<long>? times))
            {
                return false;
            }

            int at = times.BinarySearch(time + 2);
            int before = at >= 0 ? at : ~at - 1;
            return before >= 0 && times[before] >= time - SharedKillBackingSeconds;
        }

        lock (_gate)
        {
            int existing = _sources.FindIndex(s => s.Shared && s.Tag == id && string.Equals(s.Server, server, StringComparison.OrdinalIgnoreCase));
            int source;
            if (existing > 0)
            {
                source = existing;
                _events.RemoveAll(e => e.Source == source);
            }
            else
            {
                source = _sources.Count;
                _sources.Add(null!);
            }

            _sources[source] = new Source(id, server.ToLowerInvariant(), DateTime.UtcNow, Shared: true, Updated: updated);
            int serverId = _names.Server(server);
            int observer = ObserverFor(SharedPrefix + id);
            foreach (long[] kill in file.Kills)
            {
                if (kill.Length >= 3 && Name(file.Zones, kill[1]) is { } zone && Name(file.Mobs, kill[2]) is { Length: > 0 } mob)
                {
                    _events.Add(new Event(EventKind.Kill, kill[0], serverId, _names.Zone(zone), _names.Mob(mob), -1, observer, source));
                }
            }

            int drops = 0;
            foreach (long[] drop in file.Drops)
            {
                if (drop.Length >= 4 && Backed(drop[0], drop[1], drop[2]) && Name(file.Zones, drop[1]) is { } zone
                    && Name(file.Mobs, drop[2]) is { Length: > 0 } mob && Name(file.Items, drop[3]) is { Length: > 0 } item)
                {
                    _events.Add(new Event(EventKind.Drop, drop[0], serverId, _names.Zone(zone), _names.Mob(mob), _names.Item(item), observer, source));
                    drops++;
                }
            }

            Changed();
            return drops;
        }

        static string? Name(List<string> names, long index) => index >= 0 && index < names.Count ? names[(int)index] : null;
    }

    /// <summary>Takes one online file (or, with no id, every online file) out of the table.</summary>
    public void RemoveShared(string? server = null, string? id = null)
    {
        lock (_gate)
        {
            bool any = false;
            for (int i = 1; i < _sources.Count; i++)
            {
                Source s = _sources[i];
                if (s.Shared && (server is null || string.Equals(s.Server, server, StringComparison.OrdinalIgnoreCase)) && (id is null || s.Tag == id))
                {
                    int source = i;
                    _events.RemoveAll(e => e.Source == source);
                    _sources[i] = new Source(string.Empty, string.Empty, DateTime.MinValue);
                    any = true;
                }
            }

            if (any)
            {
                Changed();
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
                    Load(saved);
                }
                else
                {
                    _dirty = true; // keep the new tag
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged file starts empty
            }
        }

        Interlocked.Increment(ref _version);
    }

    private void Load(StoreFile saved)
    {
        if (!string.IsNullOrWhiteSpace(saved.Tag))
        {
            _tag = saved.Tag;
        }

        if (saved.ReadRules >= ReadRules)
        {
            foreach (string key in saved.Scanned)
            {
                _scanned.Add(key);
            }
        }
        else
        {
            _dirty = true; // every log is read again under the new rules
        }

        // Saved indexes are into the file's own name lists.
        int[] servers = saved.Servers.Select(_names.Server).ToArray();
        int[] zones = saved.Zones.Select(_names.Zone).ToArray();
        int[] mobs = saved.Mobs.Select(_names.Mob).ToArray();
        int[] items = saved.Items.Select(_names.Item).ToArray();
        int[] sources = new int[saved.Sources.Count + 1];
        for (int i = 0; i < saved.Sources.Count; i++)
        {
            SavedSource s = saved.Sources[i];
            sources[i + 1] = _sources.Count;
            _sources.Add(new Source(s.Tag, s.Server, s.ImportedUtc, s.Shared, s.Updated));
        }

        foreach (long[] e in saved.Events)
        {
            if (e.Length < 7 || e[2] < 0 || e[2] >= servers.Length || e[3] < 0 || e[3] >= zones.Length || e[4] < 0 || e[4] >= mobs.Length
                || e[6] < 0 || e[6] >= sources.Length)
            {
                continue;
            }

            var kind = (EventKind)e[0];
            int item = kind == EventKind.Drop && e[5] >= 0 && e[5] < items.Length ? items[e[5]] : -1;
            if (kind == EventKind.Drop && item < 0)
            {
                continue;
            }

            int source = sources[e[6]];
            int observer = source == OwnSource ? OwnObserver
                : ObserverFor((_sources[source].Shared ? SharedPrefix : "import:") + _sources[source].Tag);
            _events.Add(new Event(kind, e[1], servers[e[2]], zones[e[3]], mobs[e[4]], item, observer, source));
        }

        foreach (SavedUpload u in saved.Uploads)
        {
            _uploads.Add((_names.Server(u.Server), u.At, u.Through));
        }

        foreach (SavedMerged m in saved.Merged)
        {
            try
            {
                if (ParseMerged(m.Gzip) is { } parsed)
                {
                    LoadMergedLocked(m.Server, m.Updated, m.Players, m.Gzip, parsed);
                }
            }
            catch (InvalidDataException)
            {
                // a damaged copy is fetched again
            }
        }
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
            Compact();
            var file = new StoreFile { Tag = _tag, ReadRules = ReadRules, Scanned = _scanned.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList() };
            file.Servers.AddRange(_names.Servers);
            file.Zones.AddRange(_names.Zones);
            file.Mobs.AddRange(_names.Mobs);
            file.Items.AddRange(_names.Items);
            var sourceIndex = new Dictionary<int, int> { [OwnSource] = 0 };
            for (int i = 1; i < _sources.Count; i++)
            {
                if (_sources[i].Tag.Length > 0)
                {
                    sourceIndex[i] = file.Sources.Count + 1;
                    file.Sources.Add(new SavedSource
                    {
                        Tag = _sources[i].Tag, Server = _sources[i].Server, ImportedUtc = _sources[i].ImportedUtc,
                        Shared = _sources[i].Shared, Updated = _sources[i].Updated,
                    });
                }
            }

            foreach (Event e in _events)
            {
                if (sourceIndex.TryGetValue(e.Source, out int source))
                {
                    file.Events.Add(new[] { (long)e.Kind, e.Time, e.Server, e.Zone, e.Mob, e.Item, source });
                }
            }

            foreach ((int server, long at, long through) in _uploads)
            {
                file.Uploads.Add(new SavedUpload { Server = _names.ServerName(server), At = at, Through = through });
            }

            foreach ((int server, Merged m) in _merged)
            {
                file.Merged.Add(new SavedMerged { Server = _names.ServerName(server), Updated = m.Updated, Players = m.Players, Gzip = m.Gzip });
            }

            json = JsonSerializer.Serialize(file);
        }

        try
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true; // try again next time
            }
        }
    }

    private void Changed()
    {
        _dirty = true;
        Interlocked.Increment(ref _version);
    }

    private int ObserverFor(string key)
    {
        if (!_observerIndex.TryGetValue(key, out int index))
        {
            _observerIndex[key] = index = _observers.Count;
            _observers.Add(key);
        }

        return index;
    }

    private static string LogKey(string character, string server) => character + "|" + server;

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    // ───────────────────────── types ─────────────────────────

    private enum EventKind : byte
    {
        Kill = 0,
        Drop = 1,
    }

    /// <summary>A kill or a drop, seen by one log (observer) of this PC or of an imported file (source). Item is -1 for kills.</summary>
    private readonly record struct Event(EventKind Kind, long Time, int Server, int Zone, int Mob, int Item, int Observer, int Source);

    /// <summary>Where events came from: this PC (0), an imported file, or a file shared online (Tag is then its id).</summary>
    private sealed record Source(string Tag, string Server, DateTime ImportedUtc, bool Shared = false, long Updated = 0);

    /// <summary>What one log is in the middle of: kills just made, and advanced-loot items waiting for their loot line.</summary>
    private sealed class LogState(string character, string server)
    {
        public string Character { get; } = character;

        public string Server { get; } = server;

        public List<(long Time, int Mob, int Zone)> Kills { get; } = new();

        public List<(long Time, string Item)> Pending { get; } = new();

        public HashSet<(long Time, int Mob, int Item)> Claimed { get; } = new();
    }

    /// <summary>Server, zone, mob and item names, each kept once (compared without regard to capitals).</summary>
    private sealed class Names
    {
        private readonly NameList _servers = new(), _zones = new(), _mobs = new(), _items = new();

        public IReadOnlyList<string> Servers => _servers.All;

        public IReadOnlyList<string> Zones => _zones.All;

        public IReadOnlyList<string> Mobs => _mobs.All;

        public IReadOnlyList<string> Items => _items.All;

        public int Server(string name) => _servers.Index(name.ToLowerInvariant());

        public int FindServer(string name) => _servers.Find(name);

        public int Zone(string name) => _zones.Index(name);

        public int Mob(string name) => _mobs.Index(name, preferLower: true);

        public int Item(string name) => _items.Index(name);

        public string ServerName(int id) => _servers.All[id];

        public string ZoneName(int id) => _zones.All[id];

        public string MobName(int id) => _mobs.All[id];

        public string ItemName(int id) => _items.All[id];
    }

    private sealed class NameList
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

        public List<string> All { get; } = new();

        public int Find(string name) => _index.TryGetValue(name.Trim(), out int index) ? index : -1;

        /// <summary>
        /// The name's index. For mobs, "orc pawn" (as a corpse line writes it) is kept over "Orc pawn" (the start of a kill line), and
        /// "A decaying skeleton" is shown as "a decaying skeleton".
        /// </summary>
        public int Index(string name, bool preferLower = false)
        {
            string text = name.Trim();
            if (preferLower && (text.StartsWith("A ", StringComparison.Ordinal) || text.StartsWith("An ", StringComparison.Ordinal)))
            {
                text = char.ToLowerInvariant(text[0]) + text[1..];
            }

            if (_index.TryGetValue(text, out int index))
            {
                if (preferLower && char.IsUpper(All[index][0]) && char.IsLower(text[0]))
                {
                    All[index] = text;
                }

                return index;
            }

            _index[text] = index = All.Count;
            All.Add(text);
            return index;
        }
    }

    /// <summary>The drops file passed between players.</summary>
    public sealed class DropFile
    {
        public string Format { get; set; } = string.Empty;

        public int Version { get; set; }

        public string Tag { get; set; } = string.Empty;

        public string Server { get; set; } = string.Empty;

        public DateTime ExportedUtc { get; set; }

        public List<string> Zones { get; set; } = new();

        public List<string> Mobs { get; set; } = new();

        public List<string> Items { get; set; } = new();

        /// <summary>[unix time, zone, mob]</summary>
        public List<long[]> Kills { get; set; } = new();

        /// <summary>[unix time, zone, mob, item]</summary>
        public List<long[]> Drops { get; set; } = new();
    }

    public sealed class SavedSource
    {
        public string Tag { get; set; } = string.Empty;

        public string Server { get; set; } = string.Empty;

        public DateTime ImportedUtc { get; set; }

        /// <summary>True for a file shared online; Tag is then its id.</summary>
        public bool Shared { get; set; }

        /// <summary>For a file shared online, the upload time it had (unix seconds).</summary>
        public long Updated { get; set; }
    }

    /// <summary>drops.json: the tag, which logs were read, the names, imported files, and every event.</summary>
    public sealed class StoreFile
    {
        public string Tag { get; set; } = string.Empty;

        public List<string> Scanned { get; set; } = new();

        /// <summary>The <see cref="ReadRules"/> the logs were read under.</summary>
        public int ReadRules { get; set; }

        public List<string> Servers { get; set; } = new();

        public List<string> Zones { get; set; } = new();

        public List<string> Mobs { get; set; } = new();

        public List<string> Items { get; set; } = new();

        public List<SavedSource> Sources { get; set; } = new();

        /// <summary>[kind, unix time, server, zone, mob, item, source]</summary>
        public List<long[]> Events { get; set; } = new();

        /// <summary>This PC's uploads to the shared table: when, and up to which event time.</summary>
        public List<SavedUpload> Uploads { get; set; } = new();

        /// <summary>The merged shared table of each server, as downloaded.</summary>
        public List<SavedMerged> Merged { get; set; } = new();
    }

    public sealed class SavedUpload
    {
        public string Server { get; set; } = string.Empty;

        /// <summary>When it was uploaded (unix seconds).</summary>
        public long At { get; set; }

        /// <summary>The time of the newest event in it (unix seconds).</summary>
        public long Through { get; set; }
    }

    public sealed class SavedMerged
    {
        public string Server { get; set; } = string.Empty;

        public long Updated { get; set; }

        public int Players { get; set; }

        public byte[] Gzip { get; set; } = Array.Empty<byte>();
    }
}
