using System.Globalization;
using System.Text;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.Core.Resists;

/// <summary>
/// Reads one character's own spells on mobs from its log lines:
/// <list type="bullet">
/// <item><description>"You begin casting X." remembers X for its cast time plus 2.5 s.</description></item>
/// <item><description>"A temple guard resisted your X!" counts a resist of X's resist type on that mob.</description></item>
/// <item><description>X's landing text on a mob while X is remembered ("a horror guard's skin gleams with a dull red aura.") counts a landing.</description></item>
/// </list>
/// Spells on players (your characters, and anyone the roster knows as a player), unresistable spells, bard songs and spells without landing
/// text are not counted.
/// </summary>
public sealed class ResistObserver
{
    private const string YouBeginCasting = "You begin casting ";
    private const string ResistedYour = " resisted your ";
    private const string YouBeginSinging = "You begin singing ";

    private sealed record Pending(SpellDefinition Spell, DateTime Expires);

    private readonly SpellDatabaseService _spells;
    private readonly MobResistStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, bool>? _isPlayer;

    public ResistObserver(SpellDatabaseService spells, MobResistStore store)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Handles one line of <paramref name="character"/>'s log. <paramref name="when"/> is the line's time (any one clock per log).</summary>
    public void Observe(string character, ReadOnlySpan<char> message, DateTime when)
    {
        if (message.Length < 10 || string.IsNullOrEmpty(character))
        {
            return;
        }

        if (message.StartsWith(YouBeginSinging, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _pending.Remove(character); // songs are not counted (see Countable)
            }

            return;
        }

        if (message.StartsWith(YouBeginCasting, StringComparison.Ordinal))
        {
            if (message[^1] != '.')
            {
                return;
            }

            SpellDefinition? spell = _spells.Database.FindDefinitionByName(message[YouBeginCasting.Length..^1].ToString());
            lock (_gate)
            {
                if (spell is not null && Countable(spell))
                {
                    _pending[character] = new Pending(spell, when.AddMilliseconds(spell.CastTimeMs) + TimeSpan.FromMilliseconds(2500));
                }
                else
                {
                    _pending.Remove(character);
                }
            }

            return;
        }

        if (message[^1] == '!')
        {
            int resisted = message.IndexOf(ResistedYour, StringComparison.Ordinal);
            if (resisted > 0)
            {
                string mob = message[..resisted].ToString();
                string spellName = message[(resisted + ResistedYour.Length)..^1].ToString();
                SpellDefinition? spell = _spells.Database.FindDefinitionByName(spellName);
                lock (_gate)
                {
                    if (_pending.TryGetValue(character, out Pending? pending) && pending.Spell.Name.Equals(spellName, StringComparison.OrdinalIgnoreCase))
                    {
                        _pending.Remove(character);
                    }
                }

                if (spell is not null && Countable(spell) && IsMob(mob, character))
                {
                    _store.AddOwn(mob, character, spell.ResistType, resisted: true, ToUtc(when));
                }

                return;
            }
        }

        Pending? current;
        lock (_gate)
        {
            if (!_pending.TryGetValue(character, out current))
            {
                return;
            }

            if (current.Expires < when)
            {
                _pending.Remove(character);
                return;
            }
        }

        string suffix = current.Spell.CastOnOtherSuffix;
        if (suffix.Length < 3 || message.Length <= suffix.Length || !message.EndsWith(suffix, StringComparison.Ordinal))
        {
            return;
        }

        string target = message[..^suffix.Length].TrimEnd().ToString();
        if (target.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _pending.Remove(character);
        }

        if (IsMob(target, character))
        {
            _store.AddOwn(target, character, current.Spell.ResistType, resisted: false, ToUtc(when));
        }
    }

    /// <summary>
    /// Only spells whose landings can be seen as well as their resists are counted: a resistable spell with landing text that is not a
    /// bard song. Counting the resists of a spell whose landings never show (a song, or a spell without landing text) would make every
    /// mob look like it resists everything.
    /// </summary>
    private bool Countable(SpellDefinition spell) =>
        SpellResist.IsResistable(spell.ResistType)
        && spell.CastOnOtherSuffix.Length >= 3
        && _spells.Database.FindByName(spell.Name)?.Category != EQOverwatch.Core.Models.TimerOverlayCategory.Songs;

    /// <summary>Sets the probe that recognizes players.</summary>
    public void SetIsPlayer(Func<string, bool>? isPlayer) => Volatile.Write(ref _isPlayer, isPlayer);

    private bool IsMob(string name, string character) =>
        !name.Equals(character, StringComparison.OrdinalIgnoreCase)
        && !name.Equals("You", StringComparison.Ordinal)
        && Volatile.Read(ref _isPlayer)?.Invoke(name) != true;

    private static DateTime ToUtc(DateTime when) => when.Kind == DateTimeKind.Local || when.Kind == DateTimeKind.Unspecified ? when.ToUniversalTime() : when;
}

/// <summary>The <c>{EQO:R1:mob:M180-3:F22-6}</c> tokens players paste into raid or guild chat to share resist numbers.</summary>
public static class ResistShareCodec
{
    public const string Prefix = "{EQO:R1:";

    /// <summary>EverQuest cuts chat lines off a little past this length.</summary>
    public const int MaxLineLength = 450;

    /// <summary>One token for a mob, or null when there is nothing to share.</summary>
    public static string? Encode(string mob, IReadOnlyDictionary<int, ResistCount> counts)
    {
        if (string.IsNullOrWhiteSpace(mob) || mob.Contains(':') || mob.Contains('}'))
        {
            return null;
        }

        var builder = new StringBuilder(Prefix).Append(mob.Trim());
        bool any = false;
        foreach (int type in SpellResist.All)
        {
            if (counts.TryGetValue(type, out ResistCount count) && count.Total > 0)
            {
                builder.Append(':').Append(SpellResist.Code(type))
                    .Append(count.Landed.ToString(CultureInfo.InvariantCulture)).Append('-')
                    .Append(count.Resisted.ToString(CultureInfo.InvariantCulture));
                any = true;
            }
        }

        return any ? builder.Append('}').ToString() : null;
    }

    /// <summary>Every valid token in a chat message.</summary>
    public static IEnumerable<(string Mob, Dictionary<int, ResistCount> Counts)> Decode(string text)
    {
        int start = 0;
        while ((start = text.IndexOf(Prefix, start, StringComparison.Ordinal)) >= 0)
        {
            int end = text.IndexOf('}', start);
            if (end < 0)
            {
                yield break;
            }

            string[] parts = text[(start + Prefix.Length)..end].Split(':');
            start = end + 1;
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            var counts = new Dictionary<int, ResistCount>();
            bool valid = true;
            foreach (string part in parts.Skip(1))
            {
                int dash = part.IndexOf('-');
                int type = part.Length > 0 ? SpellResist.FromCode(part[0]) : 0;
                if (type == 0 || dash < 2
                    || !int.TryParse(part.AsSpan(1, dash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int landed)
                    || !int.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int resisted))
                {
                    valid = false;
                    break;
                }

                counts[type] = new ResistCount(landed, resisted);
            }

            if (valid && counts.Count > 0)
            {
                yield return (parts[0].Trim(), counts);
            }
        }
    }

    /// <summary>Packs tokens into chat lines of at most <see cref="MaxLineLength"/> characters each.</summary>
    public static IReadOnlyList<string> PackLines(IEnumerable<string> tokens)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (string token in tokens)
        {
            if (current.Length > 0 && current.Length + 1 + token.Length > MaxLineLength)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(token);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }
}

/// <summary>
/// The live side of resist tracking: every line of every trigger-enabled log is read for your characters' own resists and landings, and
/// raid or guild chat is read for other players' shared numbers ("Trick tells the raid, '{EQO:R1:...}'"). Your own characters' shares are
/// ignored (their numbers are already counted). Live lines are marked as counted so reading the same log in later does not count them twice.
/// </summary>
public sealed class ResistTrackerService : ITriggerLineSink
{
    private const string TellsTheRaid = " tells the raid,";
    private const string TellsTheGuild = " tells the guild,";

    private readonly SpellDatabaseService _spells;
    private readonly MobResistStore _store;
    private readonly ResistObserver _observer;
    private Func<string, bool>? _isMine;
    private Func<string, bool>? _isPlayer;

    public ResistTrackerService(SpellDatabaseService spells, MobResistStore store)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observer = new ResistObserver(spells, store);
    }

    public MobResistStore Store => _store;

    /// <summary>Recognizes players (your characters and verified players), so spells on them are not counted as on mobs.</summary>
    public void SetPlayerProbe(Func<string, bool>? isPlayer)
    {
        Volatile.Write(ref _isPlayer, isPlayer);
        _observer.SetIsPlayer(isPlayer);
    }

    /// <summary>Recognizes your own characters, whose shares are ignored.</summary>
    public void SetOwnCharacterProbe(Func<string, bool>? isMine) => Volatile.Write(ref _isMine, isMine);

    /// <summary>Wraps the DPS sink of a log being read in, so the log's own spells are counted too.</summary>
    public IDpsEventSink WrapImport(IDpsEventSink inner)
    {
        var observer = new ResistObserver(_spells, _store);
        observer.SetIsPlayer(Volatile.Read(ref _isPlayer));
        return new ResistImportSink(inner, _store, observer);
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        DateTime logTime = receivedUtc.ToLocalTime();
        _store.TouchLive(characterName, DateTime.SpecifyKind(logTime, DateTimeKind.Unspecified));
        _observer.Observe(characterName, message, receivedUtc);

        if (message.Length > 30 && message[^1] == '\'' && message.IndexOf(ResistShareCodec.Prefix, StringComparison.Ordinal) > 0)
        {
            ReadShare(message, receivedUtc);
        }
    }

    private void ReadShare(ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        int at = message.IndexOf(TellsTheRaid, StringComparison.Ordinal);
        int markerLength = TellsTheRaid.Length;
        if (at <= 0)
        {
            at = message.IndexOf(TellsTheGuild, StringComparison.Ordinal);
            markerLength = TellsTheGuild.Length;
        }

        if (at <= 0)
        {
            return; // only raid and guild chat count
        }

        string player = message[..at].ToString();
        if (player.Any(c => !char.IsLetter(c)) || Volatile.Read(ref _isMine)?.Invoke(player) == true)
        {
            return;
        }

        ReadOnlySpan<char> rest = message[(at + markerLength)..].TrimStart();
        if (rest.Length < 2 || rest[0] != '\'')
        {
            return;
        }

        string text = rest[1..^1].ToString();
        foreach ((string mob, Dictionary<int, ResistCount> counts) in ResistShareCodec.Decode(text))
        {
            _store.ApplyShare(new ResistShare(mob, player, receivedUtc, counts));
        }
    }
}

/// <summary>
/// Passes everything to the DPS tracker of a log being read in, and also reads the log's own spells for resist tracking. Parts of the log
/// already counted (live, or by an earlier read) are skipped; the part read is then marked as counted.
/// </summary>
public sealed class ResistImportSink : IDpsEventSink
{
    private readonly IDpsEventSink _inner;
    private readonly MobResistStore _store;
    private readonly ResistObserver _observer;
    private string _owner = string.Empty;
    private DateTime _clock;
    private DateTime _first;
    private DateTime _last;

    public ResistImportSink(IDpsEventSink inner, MobResistStore store, ResistObserver observer)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    public void BeginBatch() => _inner.BeginBatch();

    public void OnCombatEvent(in CombatEvent combatEvent)
    {
        Note(combatEvent.Timestamp);
        _inner.OnCombatEvent(in combatEvent);
    }

    public void EndBatch()
    {
        try
        {
            if (_owner.Length > 0 && _first != default)
            {
                _store.AddCovered(_owner, _first, _last);
            }

            _store.Flush();
        }
        finally
        {
            _inner.EndBatch();
        }
    }

    public void OnLogClock(DateTime logTimestamp)
    {
        _clock = logTimestamp;
        Note(logTimestamp);
        _inner.OnLogClock(logTimestamp);
    }

    public void OnLogOwner(string characterName)
    {
        if (_owner.Length == 0)
        {
            _owner = characterName;
        }

        _inner.OnLogOwner(characterName);
    }

    public void OnLogServer(string serverName) => _inner.OnLogServer(serverName);

    public void OnNonCombatMessage(ReadOnlySpan<char> message)
    {
        _inner.OnNonCombatMessage(message);
        if (_owner.Length > 0 && _clock != default && !_store.IsCovered(_owner, _clock))
        {
            _observer.Observe(_owner, message, _clock);
        }
    }

    private void Note(DateTime time)
    {
        if (time == default)
        {
            return;
        }

        if (_first == default || time < _first)
        {
            _first = time;
        }

        if (time > _last)
        {
            _last = time;
        }
    }
}
