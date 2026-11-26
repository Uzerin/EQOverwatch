using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Discord;

/// <summary>What a webhook posts: raid boss kills (the Discord picture), loot lists linked to raid chat, or unlooted items (with @here).</summary>
public enum HookKind
{
    Kills,
    Loot,
    Unlooted,
}

/// <summary>
/// Raid keys: "OWK-7QM2-XPAH" (kills), "OWL-..." (loot) or "OWU-..." (unlooted). The owner of a Discord webhook says one in raid chat,
/// and every Overwatch player in the raid then sends that kind of post to it, through the Overwatch server, without seeing the address.
/// </summary>
public static partial class RaidKey
{
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    [GeneratedRegex(@"\bOW(?<k>[KLU])-(?<a>[2-9A-HJ-NP-Z]{4})-(?<b>[2-9A-HJ-NP-Z]{4})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();

    public static string Prefix(HookKind kind) => kind switch
    {
        HookKind.Kills => "OWK",
        HookKind.Loot => "OWL",
        _ => "OWU",
    };

    /// <summary>A new random key for <paramref name="kind"/>.</summary>
    public static string New(HookKind kind)
    {
        Span<char> chars = stackalloc char[8];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"{Prefix(kind)}-{chars[..4]}-{chars[4..]}";
    }

    /// <summary>The keys in a line of text, upper-cased, with their kind.</summary>
    public static IEnumerable<(string Key, HookKind Kind)> Find(string text)
    {
        if (!text.Contains("OW", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (Match m in KeyPattern().Matches(text))
        {
            HookKind kind = char.ToUpperInvariant(m.Groups["k"].Value[0]) switch
            {
                'K' => HookKind.Kills,
                'L' => HookKind.Loot,
                _ => HookKind.Unlooted,
            };
            yield return ($"{Prefix(kind)}-{m.Groups["a"].Value.ToUpperInvariant()}-{m.Groups["b"].Value.ToUpperInvariant()}", kind);
        }
    }

    public static bool IsValid(string key, HookKind kind) =>
        Find(key).Any(k => k.Kind == kind && k.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The raid keys each character heard in raid chat. A character's keys end when it leaves or joins a raid, logs in, or its log has had
/// no lines for 30 minutes; a key also ends 6 hours after it was last said or a raid boss last died, whichever is later (characters
/// left online in a raid for days stop sending).
/// </summary>
public sealed class RaidKeyBook
{
    public static readonly TimeSpan QuietLimit = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan KeyLife = TimeSpan.FromHours(6);

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, (HookKind Kind, DateTime Heard)>> _heard = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastLine = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastBossKill;

    public void Hear(string character, string text, DateTime utc)
    {
        lock (_gate)
        {
            foreach ((string key, HookKind kind) in RaidKey.Find(text))
            {
                if (!_heard.TryGetValue(character, out var keys))
                {
                    _heard[character] = keys = new Dictionary<string, (HookKind, DateTime)>(StringComparer.OrdinalIgnoreCase);
                }

                keys[key] = (kind, utc);
            }
        }
    }

    /// <summary>Any live line of <paramref name="character"/> (a log that keeps writing keeps its keys).</summary>
    public void Saw(string character, DateTime utc)
    {
        lock (_gate)
        {
            _lastLine[character] = utc;
        }
    }

    /// <summary>A raid boss died: every key heard so far lives 6 more hours.</summary>
    public void BossKilled(DateTime utc)
    {
        lock (_gate)
        {
            if (utc > _lastBossKill)
            {
                _lastBossKill = utc;
            }
        }
    }

    public void Left(string character)
    {
        lock (_gate)
        {
            _heard.Remove(character);
        }
    }

    /// <summary>Every key of <paramref name="kind"/> still active at <paramref name="utc"/>.</summary>
    public IReadOnlyList<string> Active(HookKind kind, DateTime utc)
    {
        lock (_gate)
        {
            foreach (string quiet in _heard.Keys.Where(c => !_lastLine.TryGetValue(c, out DateTime last) || utc - last > QuietLimit).ToList())
            {
                _heard.Remove(quiet);
            }

            return _heard.Values.SelectMany(k => k)
                .Where(k => k.Value.Kind == kind && utc - (k.Value.Heard > _lastBossKill ? k.Value.Heard : _lastBossKill) <= KeyLife)
                .Select(k => k.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
    }
}

/// <summary>A post for a webhook. The server writes the message from these fields; <see cref="At"/> is when it happened (UTC).</summary>
public abstract record DiscordPost(HookKind Kind, string Mob, DateTime At);

/// <summary>A loot list a player linked to raid chat, every line of one burst together; repeated items are counted.</summary>
public sealed record LootListPost(string Mob, string Poster, IReadOnlyList<(string Item, int Count)> Items, DateTime At)
    : DiscordPost(HookKind.Loot, Mob, At);

/// <summary>An item given to a player that was not looted within a minute.</summary>
public sealed record UnlootedPost(string Mob, string Item, string Player, DateTime At) : DiscordPost(HookKind.Unlooted, Mob, At);

/// <summary>The items of a raid boss's corpse still not looted 14 minutes after the kill (loot opens to everyone at 15).</summary>
public sealed record LootReminderPost(string Mob, IReadOnlyList<(string Item, int Count)> Items, DateTime At) : DiscordPost(HookKind.Unlooted, Mob, At);

/// <summary>
/// Watches the live loot lines after raid boss kills and makes the loot posts: a loot list linked to raid chat (a burst of lines from
/// one player, posted once 10 seconds pass without another), an item given to a player and not looted within a minute, and 14 minutes
/// after the kill the items still on the corpse. Only loot within 15 minutes of a raid boss kill counts. The same line read from
/// several boxes' logs counts once. Call <see cref="Tick"/> now and then; posts come out of <see cref="Tick"/>.
/// </summary>
public sealed class RaidLootWatch
{
    public static readonly TimeSpan LootWindow = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ListQuiet = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LootGrace = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ReminderAfter = TimeSpan.FromMinutes(14);
    private static readonly TimeSpan SameLine = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly List<Kill> _kills = new();
    private readonly List<Given> _given = new();
    private readonly Dictionary<string, PendingList> _lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recentLines = new(StringComparer.Ordinal);

    private sealed class Kill
    {
        public required string Mob { get; init; }

        public DateTime At { get; init; }

        public bool Reminded { get; set; }

        /// <summary>Items left on the corpse ("No one was interested ...") and how many are still there.</summary>
        public Dictionary<string, int> Left { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Given
    {
        public required string Item { get; init; }

        public required string Player { get; init; }

        public required Kill Kill { get; init; }

        public DateTime At { get; init; }

        public int Count { get; set; }

        public bool Posted { get; set; }
    }

    private sealed class PendingList
    {
        public required string Poster { get; init; }

        public required Kill Kill { get; init; }

        public DateTime Last { get; set; }

        public List<(string Item, int Count)> Items { get; } = new();
    }

    /// <summary>The last loot list linked to raid chat (for the manual button).</summary>
    public LootListPost? LastLootList { get; private set; }

    /// <summary>A raid boss died (live).</summary>
    public void OnRaidBossKill(string mob, DateTime utc)
    {
        lock (_gate)
        {
            if (_kills.Any(k => k.Mob.Equals(mob, StringComparison.OrdinalIgnoreCase) && (utc - k.At).Duration() < TimeSpan.FromMinutes(2)))
            {
                return; // the same kill seen from another box
            }

            _kills.Add(new Kill { Mob = mob, At = utc });
            _kills.RemoveAll(k => utc - k.At > TimeSpan.FromHours(1));
        }
    }

    /// <summary>One live log line of <paramref name="character"/>.</summary>
    public void OnLine(string character, ReadOnlySpan<char> message, DateTime utc)
    {
        if (message.Length < 12)
        {
            return;
        }

        bool loot = message.StartsWith("--", StringComparison.Ordinal) || message.Contains(" given to ", StringComparison.Ordinal)
                    || message.StartsWith("No one was interested", StringComparison.Ordinal) || message.Contains(" raid, ", StringComparison.Ordinal);
        if (!loot)
        {
            return;
        }

        lock (_gate)
        {
            Kill? kill = _kills.LastOrDefault(k => utc >= k.At && utc - k.At <= LootWindow);
            if (kill is null)
            {
                return;
            }

            string text = message.ToString();
            bool own = text.StartsWith("You ", StringComparison.Ordinal) || text.StartsWith("--You ", StringComparison.Ordinal) || text.EndsWith(" given to you.", StringComparison.Ordinal);
            string lineKey = own ? character + "|" + text : text;
            if (_recentLines.TryGetValue(lineKey, out DateTime seen) && (utc - seen).Duration() < SameLine)
            {
                return; // read from another box's log
            }

            _recentLines[lineKey] = utc;
            if (_recentLines.Count > 500)
            {
                foreach (string old in _recentLines.Where(l => utc - l.Value > TimeSpan.FromMinutes(1)).Select(l => l.Key).ToList())
                {
                    _recentLines.Remove(old);
                }
            }

            if (RaidLootLines.TryRaidSay(message, out RaidSay say))
            {
                IReadOnlyList<(string Item, int Count)> items = RaidLootLines.LootList(say.Text);
                if (items.Count > 0)
                {
                    string poster = say.Speaker == "You" ? character : say.Speaker;
                    if (!_lists.TryGetValue(poster, out PendingList? list))
                    {
                        _lists[poster] = list = new PendingList { Poster = poster, Kill = kill };
                    }

                    list.Items.AddRange(items);
                    list.Last = utc;
                }
            }
            else if (RaidLootLines.TryGiven(message, out GivenItem given))
            {
                if (kill.Left.TryGetValue(given.Item, out int onCorpse) && onCorpse > 0)
                {
                    kill.Left[given.Item] = Math.Max(0, onCorpse - given.Count); // randomed again and given out: counted below instead
                }

                _given.Add(new Given
                {
                    Item = given.Item, Player = given.Player == "You" ? character : given.Player, Kill = kill, At = utc, Count = given.Count,
                });
            }
            else if (RaidLootLines.TryLooted(message, out LootedItem looted))
            {
                string player = looted.Player == "You" ? character : looted.Player;
                int count = looted.Count;
                foreach (Given g in _given.Where(g => g.Count > 0 && g.Player.Equals(player, StringComparison.OrdinalIgnoreCase)
                                                      && g.Item.Equals(looted.Item, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    int taken = Math.Min(count, g.Count);
                    g.Count -= taken;
                    count -= taken;
                    if (count == 0)
                    {
                        break;
                    }
                }

                if (count > 0 && kill.Left.TryGetValue(looted.Item, out int left))
                {
                    kill.Left[looted.Item] = Math.Max(0, left - count);
                }
            }
            else if (RaidLootLines.TryNoOneInterested(message, out string item, out int many))
            {
                kill.Left[item] = kill.Left.GetValueOrDefault(item) + many;
            }
        }
    }

    /// <summary>The posts that are due at <paramref name="utc"/>.</summary>
    public IReadOnlyList<DiscordPost> Tick(DateTime utc)
    {
        var posts = new List<DiscordPost>();
        lock (_gate)
        {
            foreach (PendingList list in _lists.Values.Where(l => utc - l.Last >= ListQuiet).ToList())
            {
                _lists.Remove(list.Poster);
                List<(string Item, int Count)> items = list.Items
                    .GroupBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (g.First().Item, g.Sum(i => i.Count)))
                    .ToList();
                var post = new LootListPost(list.Kill.Mob, list.Poster, items, list.Last);
                LastLootList = post;
                posts.Add(post);
            }

            foreach (Given g in _given.Where(g => !g.Posted && g.Count > 0 && utc - g.At >= LootGrace))
            {
                g.Posted = true;
                posts.Add(new UnlootedPost(g.Kill.Mob, g.Item, g.Player, g.At));
            }

            foreach (Kill kill in _kills.Where(k => !k.Reminded && utc - k.At >= ReminderAfter))
            {
                kill.Reminded = true;
                if (Unlooted(kill) is { Count: > 0 } items)
                {
                    posts.Add(new LootReminderPost(kill.Mob, items, kill.At));
                }
            }

            _given.RemoveAll(g => utc - g.Kill.At > TimeSpan.FromHours(1));
        }

        return posts;
    }

    /// <summary>Items given out after a raid boss kill and not looted yet, until the corpse opens to everyone.</summary>
    public IReadOnlyList<(string Item, string Player, string Mob, DateTime Given, DateTime Unlocks)> CurrentUnlooted(DateTime utc)
    {
        lock (_gate)
        {
            return _given.Where(g => g.Count > 0 && utc < g.Kill.At + LootWindow)
                .Select(g => (g.Item, g.Player, g.Kill.Mob, g.At, g.Kill.At + LootWindow))
                .ToList();
        }
    }

    /// <summary>The items of the last raid boss kill that are still not looted (for the manual button), or none.</summary>
    public LootReminderPost? LastUnlooted(DateTime utc)
    {
        lock (_gate)
        {
            Kill? kill = _kills.LastOrDefault(k => utc - k.At <= LootWindow);
            return kill is not null && Unlooted(kill) is { Count: > 0 } items ? new LootReminderPost(kill.Mob, items, kill.At) : null;
        }
    }

    private List<(string Item, int Count)> Unlooted(Kill kill) =>
        _given.Where(g => g.Kill == kill && g.Count > 0).Select(g => (Item: g.Item, Count: g.Count))
            .Concat(kill.Left.Where(l => l.Value > 0).Select(l => (Item: l.Key, Count: l.Value)))
            .GroupBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().Item, g.Sum(i => i.Count)))
            .ToList();
}

/// <summary>Feeds the live lines to the raid keys (heard in raid chat, ended by leaving the raid) and to the loot watch.</summary>
public sealed class RaidDiscordSink : EQOverwatch.Core.Pipeline.ITriggerLineSink
{
    public RaidKeyBook Keys { get; } = new();

    public RaidLootWatch Loot { get; } = new();

    /// <summary>A character entered a zone (Zone set) or started to camp (Zone null), for the loot unlock timer.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<(string Character, string? Zone)> Moves { get; } = new();

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        Keys.Saw(characterName, receivedUtc);
        if (message.StartsWith("You have entered ", StringComparison.Ordinal) && EQOverwatch.Core.Maps.ZoneTracker.ParseZone(message) is { } zone)
        {
            Moves.Enqueue((characterName, zone));
        }
        else if (message.StartsWith("It will take you about ", StringComparison.Ordinal) && message.EndsWith("to prepare your camp.", StringComparison.Ordinal))
        {
            Moves.Enqueue((characterName, null));
        }

        if (RaidLootLines.IsRaidLeft(message) || message.SequenceEqual("You have joined the raid.")
            || message.StartsWith("Welcome to EverQuest!", StringComparison.Ordinal))
        {
            Keys.Left(characterName);
            return;
        }

        if (message.Contains(" raid, ", StringComparison.Ordinal) && message.Contains("OW", StringComparison.OrdinalIgnoreCase)
            && RaidLootLines.TryRaidSay(message, out RaidSay say))
        {
            Keys.Hear(characterName, say.Text, receivedUtc);
        }

        Loot.OnLine(characterName, message, receivedUtc);
    }
}
