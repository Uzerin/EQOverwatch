using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Loot;

/// <summary>One item looted from a corpse. <see cref="RollType"/> and <see cref="Roll"/> are set when the looter won a roll for it.</summary>
/// <param name="Time">Local time of the log line.</param>
/// <param name="RollType">"need", "greed" or empty.</param>
/// <param name="Roll">The winning roll, or 0 when the item was not rolled for.</param>
/// <param name="Server">The game server of the log it was read from; empty for entries saved before this was recorded.</param>
public sealed record LootEntry(DateTime Time, string Looter, string Item, string Mob, string RollType = "", int Roll = 0, string Server = "");

/// <summary>A money split one of your characters received for a kill, stored as copper (1 sp = 10, 1 gp = 100, 1 pp = 1000).</summary>
public sealed record MoneyEntry(DateTime Time, string Character, long Copper, string Mob, string Server = "");

/// <summary>A won roll: "Warshield won the need roll on 1 item(s): Mnemonic of Khati Sha with a roll of 377."</summary>
public sealed record RollWin(string Winner, string RollType, string Item, int Roll);

/// <summary>Recognizes the loot and roll lines of the log.</summary>
public static partial class LootLineParser
{
    // "--Warshield has looted a Mnemonic of Khati Sha from Lord Inquisitor Seru's corpse.--"  /  "--You have looted ..."
    [GeneratedRegex(
        @"^--(?<looter>.+?) (?:has|have) looted (?:an? )?(?<item>.+?) from (?<mob>.+?)'s corpse\.--$",
        RegexOptions.CultureInvariant)]
    private static partial Regex LootRegex();

    [GeneratedRegex(
        @"^(?<winner>.+?) won the (?<type>\w+) roll on \d+ item\(s\): (?<item>.+?) with a roll of (?<roll>\d+)\.$",
        RegexOptions.CultureInvariant)]
    private static partial Regex WinRegex();

    // "You receive 5 gold, 9 silver and 6 copper as your split (with a bonus)."
    [GeneratedRegex(
        @"^You receive (?<amount>.+?) as your split(?: \(with a bonus\))?\.$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SplitRegex();

    [GeneratedRegex(@"(?<n>\d+) (?<coin>platinum|gold|silver|copper)", RegexOptions.CultureInvariant)]
    private static partial Regex CoinRegex();

    // "Lord Inquisitor Seru's corpse looks positively amazed..."  /  "A Praetorian Pius's corpse says, 'Basta...'"
    [GeneratedRegex(@"^(?<mob>.+?)'s corpse ", RegexOptions.CultureInvariant)]
    private static partial Regex CorpseRegex();

    // "A Praetorian Medicus was slain by Patide!"
    [GeneratedRegex(@"^(?<mob>.+?) was slain by .+!$", RegexOptions.CultureInvariant)]
    private static partial Regex SlainRegex();

    /// <summary>True for "You receive 5 gold, 9 silver and 6 copper as your split.". Returns the amount in copper.</summary>
    public static bool TryParseSplit(ReadOnlySpan<char> message, out long copper)
    {
        copper = 0;
        if (!message.StartsWith("You receive ", StringComparison.Ordinal) || message.IndexOf(" as your split", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        Match match = SplitRegex().Match(message.ToString());
        if (!match.Success)
        {
            return false;
        }

        foreach (Match coin in CoinRegex().Matches(match.Groups["amount"].Value))
        {
            if (!long.TryParse(coin.Groups["n"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
            {
                continue;
            }

            copper += n * coin.Groups["coin"].Value switch
            {
                "platinum" => 1000L,
                "gold" => 100L,
                "silver" => 10L,
                _ => 1L,
            };
        }

        return copper > 0;
    }

    /// <summary>True for the corpse or kill line that names the mob a split came from.</summary>
    public static bool TryParseMobLine(ReadOnlySpan<char> message, out string mob)
    {
        mob = string.Empty;
        if (message.IndexOf("'s corpse ", StringComparison.Ordinal) > 0)
        {
            Match corpse = CorpseRegex().Match(message.ToString());
            if (corpse.Success)
            {
                mob = corpse.Groups["mob"].Value;
                return true;
            }
        }

        if (message.IndexOf(" was slain by ", StringComparison.Ordinal) > 0)
        {
            Match slain = SlainRegex().Match(message.ToString());
            if (slain.Success)
            {
                mob = slain.Groups["mob"].Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>Formats copper as "5g 9s 6c" (with "p" for platinum), or "0c".</summary>
    public static string FormatCopper(long copper)
    {
        if (copper <= 0)
        {
            return "0c";
        }

        long pp = copper / 1000;
        long gp = copper % 1000 / 100;
        long sp = copper % 100 / 10;
        long cp = copper % 10;
        var parts = new List<string>(4);
        if (pp > 0)
        {
            parts.Add(pp.ToString("N0", CultureInfo.InvariantCulture) + "p");
        }

        if (gp > 0)
        {
            parts.Add(gp + "g");
        }

        if (sp > 0)
        {
            parts.Add(sp + "s");
        }

        if (cp > 0)
        {
            parts.Add(cp + "c");
        }

        return string.Join(" ", parts);
    }

    /// <summary>True for "--X has looted a Y from Z's corpse.--". "You" becomes <paramref name="selfName"/>.</summary>
    public static bool TryParseLoot(ReadOnlySpan<char> message, string selfName, out string looter, out string item, out string mob)
    {
        looter = item = mob = string.Empty;
        if (message.Length < 12 || message[0] != '-' || message.IndexOf(" looted ", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        Match match = LootRegex().Match(message.ToString());
        if (!match.Success)
        {
            return false;
        }

        looter = match.Groups["looter"].Value;
        if (looter.Equals("You", StringComparison.OrdinalIgnoreCase))
        {
            looter = selfName;
        }

        item = match.Groups["item"].Value;
        mob = match.Groups["mob"].Value;
        return true;
    }

    /// <summary>True for the line announcing who won a roll.</summary>
    public static bool TryParseWin(ReadOnlySpan<char> message, out RollWin win)
    {
        win = null!;
        if (message.IndexOf(" won the ", StringComparison.Ordinal) < 0 || message.IndexOf(" roll on ", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        Match match = WinRegex().Match(message.ToString());
        if (!match.Success || !int.TryParse(match.Groups["roll"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int roll))
        {
            return false;
        }

        win = new RollWin(match.Groups["winner"].Value, match.Groups["type"].Value.ToLowerInvariant(), match.Groups["item"].Value, roll);
        return true;
    }
}

/// <summary>
/// Collects loot as it is announced in the watched logs (every trigger-enabled log), links each looted item to the roll its looter
/// won, and keeps the list in a JSON file so it survives a restart. Two characters in the same raid both see the same line, so a
/// line already recorded from another log a moment ago is skipped. <see cref="ScanLog"/> adds the loot of an older log.
/// </summary>
public sealed class LootTracker : ITriggerLineSink
{
    private const int MaxEntries = 50_000;
    private const int MaxPendingWins = 64;
    private static readonly TimeSpan SameLineWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RollMatchWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SameEventWindow = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly List<LootEntry> _entries = new();
    private readonly List<(RollWin Win, DateTime Time)> _pendingWins = new();
    private readonly Dictionary<string, SeenLine> _seenLines = new(StringComparer.Ordinal);

    /// <summary>One line text seen in the last few seconds: how many copies each log has shown and how many were recorded.</summary>
    private sealed class SeenLine
    {
        public SeenLine(DateTime start)
        {
            Start = start;
        }

        public DateTime Start { get; }
        public Dictionary<string, int> PerLog { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Recorded { get; set; }
    }
    private string? _filePath;
    private readonly List<MoneyEntry> _money = new();
    private readonly Dictionary<string, (DateTime Utc, DateTime Local, long Copper)> _pendingSplits = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SplitMobWindow = TimeSpan.FromSeconds(3);

    /// <summary>Raised on the pipeline thread after the list changed.</summary>
    public event Action? Changed;

    /// <summary>Gives the game server of a watched character's log, so new entries can be filed under it.</summary>
    public Func<string, string>? ServerLookup { get; set; }

    private string ServerOf(string character) => ServerLookup?.Invoke(character) ?? string.Empty;

    /// <summary>The recorded loot, oldest first, as a snapshot.</summary>
    public IReadOnlyList<LootEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>The money splits received, oldest first, as a snapshot.</summary>
    public IReadOnlyList<MoneyEntry> Money
    {
        get
        {
            lock (_gate)
            {
                return _money.ToList();
            }
        }
    }

    private string? MoneyPath => _filePath is null ? null : Path.ChangeExtension(_filePath, ".money.json");

    /// <summary>Loads the saved list from <paramref name="path"/> and keeps saving to it from now on.</summary>
    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _filePath = path;
            try
            {
                if (File.Exists(path))
                {
                    List<LootEntry>? saved = JsonSerializer.Deserialize<List<LootEntry>>(File.ReadAllText(path));
                    if (saved is not null)
                    {
                        _entries.Clear();
                        _entries.AddRange(saved);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged or locked file starts an empty list
            }

            try
            {
                string? moneyPath = MoneyPath;
                if (moneyPath is not null && File.Exists(moneyPath))
                {
                    List<MoneyEntry>? saved = JsonSerializer.Deserialize<List<MoneyEntry>>(File.ReadAllText(moneyPath));
                    if (saved is not null)
                    {
                        _money.Clear();
                        _money.AddRange(saved);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged or locked file starts an empty list
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _money.Clear();
            _pendingSplits.Clear();
            _pendingWins.Clear();
            Save();
        }

        Changed?.Invoke();
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (HandleMoney(characterName, message, receivedUtc))
        {
            Changed?.Invoke();
            return;
        }

        if (message.Length < 20)
        {
            return;
        }

        bool maybeLoot = message[0] == '-' && message.IndexOf(" looted ", StringComparison.Ordinal) > 0;
        bool maybeWin = !maybeLoot && message.IndexOf(" won the ", StringComparison.Ordinal) > 0;
        if (!maybeLoot && !maybeWin)
        {
            return;
        }

        string text = message.ToString();
        lock (_gate)
        {
            if (IsDuplicateFromOtherLog(text, characterName, receivedUtc))
            {
                return;
            }

            DateTime local = receivedUtc.ToLocalTime();
            if (maybeWin && LootLineParser.TryParseWin(text, out RollWin? win))
            {
                RememberWin(win, local);
                return;
            }

            if (maybeLoot && LootLineParser.TryParseLoot(text, characterName, out string looter, out string item, out string mob))
            {
                AddLoot(local, looter, item, mob, ServerOf(characterName));
                Save();
            }
            else
            {
                return;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Reads the loot lines of a log file and adds those not already recorded (the same looter, item and corpse within a few
    /// seconds counts as recorded). Returns how many were added.
    /// </summary>
    public int ScanLog(string path, DateTime? from = null)
    {
        var scanned = new List<LootEntry>();
        var wins = new List<(RollWin Win, DateTime Time)>();
        var scannedMoney = new List<MoneyEntry>();
        (DateTime Time, long Copper)? pendingSplit = null;
        string ownerServer = string.Empty;
        string owner = LogFileName.TryParse(Path.GetFileName(path), out string name, out string fileServer) ? name : string.Empty;
        if (owner.Length > 0)
        {
            ownerServer = fileServer;
        }

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length <= LogLineFormat.MessageOffset)
                {
                    continue;
                }

                bool interesting = line.IndexOf("looted", StringComparison.Ordinal) >= 0
                    || line.IndexOf(" won the ", StringComparison.Ordinal) >= 0
                    || line.IndexOf(" as your split", StringComparison.Ordinal) >= 0
                    || (pendingSplit is not null
                        && (line.IndexOf("'s corpse ", StringComparison.Ordinal) >= 0 || line.IndexOf(" was slain by ", StringComparison.Ordinal) >= 0));
                if (!interesting)
                {
                    continue;
                }

                if (!LogLineFormat.TryParseTimestamp(line, out DateTime time) || (from.HasValue && time < from.Value))
                {
                    continue;
                }

                ReadOnlySpan<char> message = line.AsSpan(LogLineFormat.MessageOffset);
                if (pendingSplit is { } waiting)
                {
                    if (time - waiting.Time > SplitMobWindow)
                    {
                        scannedMoney.Add(new MoneyEntry(waiting.Time, owner, waiting.Copper, "(unknown)", ownerServer));
                        pendingSplit = null;
                    }
                    else if (LootLineParser.TryParseMobLine(message, out string splitMob))
                    {
                        scannedMoney.Add(new MoneyEntry(waiting.Time, owner, waiting.Copper, splitMob, ownerServer));
                        pendingSplit = null;
                        continue;
                    }
                }

                if (LootLineParser.TryParseSplit(message, out long splitCopper))
                {
                    if (pendingSplit is { } previous)
                    {
                        scannedMoney.Add(new MoneyEntry(previous.Time, owner, previous.Copper, "(unknown)", ownerServer));
                    }

                    pendingSplit = (time, splitCopper);
                    continue;
                }

                if (LootLineParser.TryParseWin(message, out RollWin? win))
                {
                    wins.Add((win, time));
                    if (wins.Count > MaxPendingWins)
                    {
                        wins.RemoveAt(0);
                    }
                }
                else if (LootLineParser.TryParseLoot(message, owner, out string looter, out string item, out string mob))
                {
                    LootEntry entry = new(time, looter, item, mob, Server: ownerServer);
                    scanned.Add(Link(entry, wins));
                }
            }
        }

        if (pendingSplit is { } last)
        {
            scannedMoney.Add(new MoneyEntry(last.Time, owner, last.Copper, "(unknown)", ownerServer));
        }

        int added = 0;
        int addedMoney = 0;
        lock (_gate)
        {
            foreach (MoneyEntry money in scannedMoney)
            {
                bool known = _money.Any(m => m.Character.Equals(money.Character, StringComparison.OrdinalIgnoreCase)
                                             && m.Copper == money.Copper
                                             && (m.Time - money.Time).Duration() <= SameEventWindow);
                if (!known)
                {
                    _money.Add(money);
                    addedMoney++;
                }
            }

            if (addedMoney > 0)
            {
                _money.Sort((a, b) => a.Time.CompareTo(b.Time));
                SaveMoney();
            }

            var used = new HashSet<int>();
            foreach (LootEntry entry in scanned)
            {
                int match = -1;
                for (int i = 0; i < _entries.Count; i++)
                {
                    LootEntry e = _entries[i];
                    if (!used.Contains(i)
                        && e.Looter.Equals(entry.Looter, StringComparison.OrdinalIgnoreCase)
                        && e.Item.Equals(entry.Item, StringComparison.OrdinalIgnoreCase)
                        && e.Mob.Equals(entry.Mob, StringComparison.OrdinalIgnoreCase)
                        && (e.Time - entry.Time).Duration() <= SameEventWindow)
                    {
                        match = i;
                        break;
                    }
                }

                if (match >= 0)
                {
                    used.Add(match);
                    continue;
                }

                _entries.Add(entry);
                added++;
            }

            if (added > 0)
            {
                _entries.Sort((a, b) => a.Time.CompareTo(b.Time));
                Trim();
                Save();
            }
        }

        if (added > 0 || addedMoney > 0)
        {
            Changed?.Invoke();
        }

        return added;
    }

    /// <summary>
    /// Adds loot and money sent by another PC, skipping what is already recorded here (the same looter, item and corpse, or the same
    /// character and amount, within a few seconds). Returns how many entries were added.
    /// </summary>
    public int Merge(IEnumerable<LootEntry>? loot, IEnumerable<MoneyEntry>? money)
    {
        int added = 0;
        lock (_gate)
        {
            var used = new HashSet<int>();
            foreach (LootEntry entry in loot ?? Enumerable.Empty<LootEntry>())
            {
                int match = -1;
                for (int i = 0; i < _entries.Count; i++)
                {
                    LootEntry e = _entries[i];
                    if (!used.Contains(i)
                        && e.Looter.Equals(entry.Looter, StringComparison.OrdinalIgnoreCase)
                        && e.Item.Equals(entry.Item, StringComparison.OrdinalIgnoreCase)
                        && e.Mob.Equals(entry.Mob, StringComparison.OrdinalIgnoreCase)
                        && (e.Time - entry.Time).Duration() <= SameEventWindow)
                    {
                        match = i;
                        break;
                    }
                }

                if (match >= 0)
                {
                    used.Add(match);
                    continue;
                }

                _entries.Add(entry);
                added++;
            }

            int addedLoot = added;
            foreach (MoneyEntry entry in money ?? Enumerable.Empty<MoneyEntry>())
            {
                bool known = _money.Any(m => m.Character.Equals(entry.Character, StringComparison.OrdinalIgnoreCase)
                                             && m.Copper == entry.Copper
                                             && (m.Time - entry.Time).Duration() <= SameEventWindow);
                if (!known)
                {
                    _money.Add(entry);
                    added++;
                }
            }

            if (addedLoot > 0)
            {
                _entries.Sort((a, b) => a.Time.CompareTo(b.Time));
                Trim();
                Save();
            }

            if (added > addedLoot)
            {
                _money.Sort((a, b) => a.Time.CompareTo(b.Time));
                SaveMoney();
            }
        }

        if (added > 0)
        {
            Changed?.Invoke();
        }

        return added;
    }

    // ───────────────────────── money ─────────────────────────

    /// <summary>
    /// Follows split lines. The mob is named by the corpse or kill line right after the split; if none comes within a few seconds the
    /// split is kept with the mob "(unknown)". Returns true when the money list changed.
    /// </summary>
    private bool HandleMoney(string character, ReadOnlySpan<char> message, DateTime utc)
    {
        bool isSplit = message.StartsWith("You receive ", StringComparison.Ordinal) && message.IndexOf(" as your split", StringComparison.Ordinal) > 0;
        lock (_gate)
        {
            bool changed = false;
            if (_pendingSplits.TryGetValue(character, out var pending))
            {
                if (utc - pending.Utc > SplitMobWindow)
                {
                    AddMoney(pending.Local, character, pending.Copper, "(unknown)");
                    _pendingSplits.Remove(character);
                    changed = true;
                }
                else if (!isSplit && LootLineParser.TryParseMobLine(message, out string mob))
                {
                    AddMoney(pending.Local, character, pending.Copper, mob);
                    _pendingSplits.Remove(character);
                    changed = true;
                }
            }

            if (isSplit && LootLineParser.TryParseSplit(message, out long copper))
            {
                if (_pendingSplits.Remove(character, out var previous))
                {
                    AddMoney(previous.Local, character, previous.Copper, "(unknown)");
                    changed = true;
                }

                _pendingSplits[character] = (utc, utc.ToLocalTime(), copper);
            }

            if (changed)
            {
                SaveMoney();
            }

            return changed;
        }
    }

    private void AddMoney(DateTime time, string character, long copper, string mob)
    {
        _money.Add(new MoneyEntry(time, character, copper, mob, ServerOf(character)));
        if (_money.Count > MaxEntries)
        {
            _money.RemoveRange(0, _money.Count - MaxEntries);
        }
    }

    private void SaveMoney()
    {
        string? path = MoneyPath;
        if (path is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_money));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the list stays in memory; the next change tries again
        }
    }

    // ───────────────────────── internals ─────────────────────────

    /// <summary>
    /// Two logs of the same raid both print every loot line. A log's n-th copy of a line within a few seconds is a duplicate while
    /// some log has already shown n copies; so two real copies seen by two logs are recorded twice, not four times.
    /// </summary>
    private bool IsDuplicateFromOtherLog(string text, string character, DateTime utc)
    {
        if (!_seenLines.TryGetValue(text, out SeenLine? seen) || utc - seen.Start > SameLineWindow)
        {
            if (_seenLines.Count > 256)
            {
                _seenLines.Clear();
            }

            _seenLines[text] = seen = new SeenLine(utc);
        }

        int count = seen.PerLog.GetValueOrDefault(character) + 1;
        seen.PerLog[character] = count;
        if (count > seen.Recorded)
        {
            seen.Recorded = count;
            return false;
        }

        return true;
    }

    private void RememberWin(RollWin win, DateTime local)
    {
        _pendingWins.Add((win, local));
        if (_pendingWins.Count > MaxPendingWins)
        {
            _pendingWins.RemoveAt(0);
        }
    }

    private void AddLoot(DateTime local, string looter, string item, string mob, string server)
    {
        _entries.Add(Link(new LootEntry(local, looter, item, mob, Server: server), _pendingWins));
        Trim();
    }

    /// <summary>Attaches the roll the looter won for this item, using each won roll once.</summary>
    private static LootEntry Link(LootEntry entry, List<(RollWin Win, DateTime Time)> wins)
    {
        for (int i = wins.Count - 1; i >= 0; i--)
        {
            (RollWin win, DateTime time) = wins[i];
            if (win.Winner.Equals(entry.Looter, StringComparison.OrdinalIgnoreCase)
                && win.Item.Equals(entry.Item, StringComparison.OrdinalIgnoreCase)
                && (entry.Time - time).Duration() <= RollMatchWindow)
            {
                wins.RemoveAt(i);
                return entry with { RollType = win.RollType, Roll = win.Roll };
            }
        }

        return entry;
    }

    private void Trim()
    {
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
    }

    private void Save()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the list stays in memory; the next change tries again
        }
    }
}
