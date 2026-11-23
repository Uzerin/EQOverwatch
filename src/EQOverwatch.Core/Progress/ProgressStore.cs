using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Roster;

namespace EQOverwatch.Core.Progress;

/// <summary>One skill of a character: its value after the last skill-up, and the skill-ups made today (local time).</summary>
public sealed record SkillProgress(string Skill, int Value, int GainedToday, DateTime LastUtc);

/// <summary>One recipe: combines that made the item and combines that failed.</summary>
/// <param name="SkillUps">Tradeskill skill-ups that came with this recipe's combines.</param>
public sealed record TradeskillProgress(string Recipe, int Made, int Failed, DateTime LastUtc, int SkillUps = 0)
{
    public int Combines => Made + Failed;

    public double SuccessPercent => Combines == 0 ? 0 : 100.0 * Made / Combines;
}

/// <summary>
/// One faction of a character: the change since the app opened, all the changes the logs show, how many hits there were, the most
/// common size of a hit, the mob whose kills cause it most, and whether it is at its best (+1) or worst (-1).
/// </summary>
public sealed record FactionProgress(string Faction, int Session, long AllTime, int Hits, int UsualHit, string ComesFrom, int Limit, DateTime LastUtc);

/// <summary>One class persona of a character: its class name ("Cleric", or empty when not known yet) and level (0 when not seen).</summary>
public sealed record PersonaInfo(string Persona, int Level, bool IsCurrent);

/// <summary>A character flag the log announced, with when it came.</summary>
public sealed record CharacterFlag(string Text, DateTime Utc);

/// <summary>
/// Skill-ups, tradeskill combines and faction changes per character (and server), from live lines and each log's history.
/// <para>A character can switch class persona ("You successfully loaded your Bard equipment set."). Skills, faction and level belong to
/// the persona that was on when the line came; tradeskills belong to the character. Lines before a log's first persona line go to the
/// persona that line names when it is "Your Cleric equipment set has been saved." or a /who of the character, and otherwise to the
/// character's known class.</para>
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

    [GeneratedRegex(@"^Your (?<persona>.+) equipment set has been saved\.$", RegexOptions.CultureInvariant)]
    private static partial Regex SetSavedRegex();

    [GeneratedRegex(@"^You (?:successfully loaded|loaded only part of) your (?<persona>.+?) equipment set\.", RegexOptions.CultureInvariant)]
    private static partial Regex SetLoadedRegex();

    [GeneratedRegex(@"Welcome to level (?<level>\d+)!$", RegexOptions.CultureInvariant)]
    private static partial Regex LevelUpRegex();

    [GeneratedRegex(@"^\[(?<level>\d+) (?<class>[^\]]+)\] (?<name>[A-Za-z]+) ", RegexOptions.CultureInvariant)]
    private static partial Regex WhoRegex();

    private const int MaxTimeline = 20000;
    private const int MaxRecentFactionHits = 300;

    private static readonly HashSet<string> TradeskillNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alchemy", "Baking", "Blacksmithing", "Brewing", "Fletching", "Jewelry Making", "Make Poison", "Pottery", "Research", "Tailoring", "Tinkering",
    };
    private const int FileFormat = 7; // 3: the persona timeline; 4: character flags; 5: key ring and achievement lines; 6: /played and recent faction hits; 7: legacy bonus and birthdate (the logs are read again for them)

    private readonly object _gate = new();
    private readonly Dictionary<string, CharacterState> _characters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Time, string Mob)> _lastKill = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Message, long Time)>> _liveDuringScan = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _sessionStart;
    private Func<string, bool> _isPlayer = _ => false;
    private Func<string, string, string> _classOf = (_, _) => string.Empty;
    private string? _filePath;
    private bool _dirty;
    private long _version;

    public ProgressStore(DateTime? sessionStartUtc = null) =>
        _sessionStart = LogHistoryReader.ToUnix(sessionStartUtc ?? DateTime.UtcNow);

    /// <summary>Goes up whenever anything changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Raised (outside the lock) when a live line switches a character's persona or changes its level: character, server, persona
    /// (class name) and level (0 when not known).
    /// </summary>
    public event Action<string, string, string, int>? PersonaChanged;

    /// <summary>The character's known class, for the lines before a log names a persona.</summary>
    public void SetClassProvider(Func<string, string, string> classOf) => _classOf = classOf;

    /// <summary>True for players and pets, whose deaths are not the kills that move faction.</summary>
    public void SetPlayerProbe(Func<string, bool> isPlayer) => _isPlayer = isPlayer;

    /// <summary>True for a line this store reads (a cheap test, used before parsing a log line's time).</summary>
    public static bool Wanted(string message) =>
        message.StartsWith("You have become better at ", StringComparison.Ordinal)
        || message.Contains(" equipment set", StringComparison.Ordinal)
        || message.Contains("Welcome to level ", StringComparison.Ordinal)
        || (message.Length > 6 && message[0] == '[' && char.IsAsciiDigit(message[1]))
        || message.StartsWith("Your faction standing with ", StringComparison.Ordinal)
        || message.StartsWith("You have fashioned the items", StringComparison.Ordinal)
        || message.StartsWith("You lacked the skills to fashion ", StringComparison.Ordinal)
        || IsFlagLine(message)
        || message.EndsWith(KeyRingSuffix, StringComparison.Ordinal)
        || message.StartsWith(AchievementPrefix, StringComparison.Ordinal)
        || message.StartsWith(PlayedPrefix, StringComparison.Ordinal)
        || message.StartsWith(LegacyPrefix, StringComparison.Ordinal)
        || message.Contains(BirthdateMarker, StringComparison.Ordinal)
        || (message.Length > 10 && message[^1] == '!' && message.Contains(" slain ", StringComparison.Ordinal));

    private const string KeyRingSuffix = " has been added to your key ring.";
    private const string AchievementPrefix = "You have completed achievement: ";
    private const string PlayedPrefix = "Total time playing ";
    private const string LegacyPrefix = "Your legacy server experience bonus";
    private const string BirthdateMarker = "'s birthdate: ";

    /// <summary>"Your legacy server experience bonus for reaching the maximum level has increased to 100%!" gives 100, or null.</summary>
    public static int? LegacyBonus(string message)
    {
        if (!message.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        Match m = Regex.Match(message, @"(\d+)%", RegexOptions.CultureInvariant);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    /// <summary>"Stackprime's birthdate: Wednesday, May 27, 2026 12:22:26." gives that date and time, or null.</summary>
    public static DateTime? Birthdate(string message)
    {
        int at = message.IndexOf(BirthdateMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        string text = message[(at + BirthdateMarker.Length)..].TrimEnd('.', ' ');
        return DateTime.TryParseExact(text, new[] { "dddd, MMMM d, yyyy HH:mm:ss", "dddd, MMMM d, yyyy H:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime born)
            ? born
            : null;
    }

    /// <summary>"Total time playing Stackprime: 13 days, 22 hours, and 59 minutes" in minutes, or null for any other line.</summary>
    public static long? PlayedMinutes(string message)
    {
        if (!message.StartsWith(PlayedPrefix, StringComparison.Ordinal) || message.IndexOf(':', PlayedPrefix.Length) is var colon && colon < 0)
        {
            return null;
        }

        long Part(string unit)
        {
            Match m = Regex.Match(message[colon..], @"(\d+) " + unit, RegexOptions.CultureInvariant);
            return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        long total = Part("day") * 1440 + Part("hour") * 60 + Part("minute");
        return total > 0 || message.Contains(" minute", StringComparison.Ordinal) ? total : null;
    }

    /// <summary>True for a line announcing a character flag, such as "You receive a character flag!".</summary>
    public static bool IsFlagLine(string message) =>
        message.StartsWith("You ", StringComparison.Ordinal) && message.Contains("character flag", StringComparison.OrdinalIgnoreCase);

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
        (string Persona, int Level) before, after;
        lock (_gate)
        {
            if (_liveDuringScan.TryGetValue(key, out List<(string, long)>? held))
            {
                held.Add((text, time));
            }

            before = CurrentOf(_characters, key);
            if (Read(_characters, _lastKill, characterName, serverName, key, text, time))
            {
                Changed();
            }

            after = CurrentOf(_characters, key);
        }

        if (after != before && after.Persona.Length > 0)
        {
            PersonaChanged?.Invoke(characterName, serverName, after.Persona, after.Level);
        }
    }

    private static (string Persona, int Level) CurrentOf(Dictionary<string, CharacterState> states, string key) =>
        states.TryGetValue(key, out CharacterState? state)
            ? (state.Current, state.Personas.GetValueOrDefault(state.Current)?.Level ?? 0)
            : (string.Empty, 0);

    /// <summary>Applies one line to <paramref name="states"/>. True when something was counted.</summary>
    private bool Read(
        Dictionary<string, CharacterState> states, Dictionary<string, (long Time, string Mob)> kills, string character, string server,
        string key, string text, long time)
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
        if (LegacyBonus(text) is { } bonus)
        {
            state.LegacyBonus = bonus;
            state.LegacyBonusAt = time;
            return true;
        }

        if (Birthdate(text) is { } born)
        {
            state.Born = born;
            return true;
        }

        if (PlayedMinutes(text) is { } played)
        {
            if (time >= state.PlayedAt)
            {
                state.Played = played;
                state.PlayedAt = time;
            }

            return true;
        }

        if (text.EndsWith(KeyRingSuffix, StringComparison.Ordinal) || text.StartsWith(AchievementPrefix, StringComparison.Ordinal))
        {
            bool keyRing = text.EndsWith(KeyRingSuffix, StringComparison.Ordinal);
            string name = (keyRing ? text[..^KeyRingSuffix.Length] : text[AchievementPrefix.Length..]).Trim();
            List<FlagMark> marks = keyRing ? (state.KeyRing ??= new List<FlagMark>()) : (state.Achievements ??= new List<FlagMark>());
            if (name.Length == 0 || marks.Any(m => m.Text.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            marks.Add(new FlagMark { Time = time, Text = name });
            return true;
        }

        if (IsFlagLine(text))
        {
            state.Flags ??= new List<FlagMark>();
            if (!state.Flags.Any(f => f.Time == time && f.Text == text))
            {
                state.Flags.Add(new FlagMark { Time = time, Text = text });
            }

            return true;
        }

        Match saved = SetSavedRegex().Match(text);
        Match loaded = saved.Success ? Match.Empty : SetLoadedRegex().Match(text);
        if ((saved.Success || loaded.Success) && EqClass.TryNormalize((saved.Success ? saved : loaded).Groups["persona"].Value, out string named))
        {
            // "saved" names the persona that was on; "loaded" names the next one, so what came before it is not known from it.
            SwitchTo(state, named, unknownWasThis: saved.Success, character, server, time);
            return true;
        }

        if (text[0] == '[' && WhoRegex().Match(text) is { Success: true } who
            && who.Groups["name"].Value.Equals(character, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(who.Groups["level"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whoLevel))
        {
            if (EqClass.TryNormalize(who.Groups["class"].Value, out string whoClass))
            {
                SwitchTo(state, whoClass, unknownWasThis: true, character, server, time);
            }

            SetLevel(Active(state), whoLevel, time); // a title such as "High Priest" still gives the level
            return true;
        }

        if (LevelUpRegex().Match(text) is { Success: true } levelUp
            && int.TryParse(levelUp.Groups["level"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int newLevel))
        {
            SetLevel(Active(state), newLevel, time);
            return true;
        }

        PersonaState persona = Active(state);
        if (SkillRegex().Match(text) is { Success: true } skill
            && int.TryParse(skill.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            string name = skill.Groups["skill"].Value.Trim();
            if (!persona.Skills.TryGetValue(name, out SkillState? s))
            {
                persona.Skills[name] = s = new SkillState();
            }

            s.Value = value;
            s.Last = Math.Max(s.Last, time);
            s.Ups.Add(time);
            s.Ups.RemoveAll(t => t < time - RecentUpSeconds);

            // A tradeskill skill-up comes in the same second as its combine's result line, before or after it.
            if (TradeskillNames.Contains(name))
            {
                if (state.LastCombine is { } combine && Math.Abs(time - combine.Time) <= 1 && state.Recipes.TryGetValue(combine.Item, out RecipeState? recipe))
                {
                    recipe.SkillUps++;
                    state.LastCombine = null;
                }
                else
                {
                    state.PendingTradeUp = time;
                }
            }

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
            if (state.PendingTradeUp > 0 && time - state.PendingTradeUp <= 1)
            {
                r.SkillUps++;
                state.PendingTradeUp = 0;
                state.LastCombine = null;
            }
            else
            {
                state.LastCombine = (item, time);
            }

            return true;
        }

        Match faction = FactionRegex().Match(text);
        Match limit = faction.Success ? Match.Empty : FactionLimitRegex().Match(text);
        if (!faction.Success && !limit.Success)
        {
            return false;
        }

        string factionName = (faction.Success ? faction : limit).Groups["faction"].Value.Trim();
        if (!persona.Factions.TryGetValue(factionName, out FactionState? f))
        {
            persona.Factions[factionName] = f = new FactionState();
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
            f.Recent ??= new List<long[]>();
            f.Recent.Add(new[] { time, amount });
            if (f.Recent.Count > MaxRecentFactionHits)
            {
                f.Recent.RemoveRange(0, f.Recent.Count - MaxRecentFactionHits);
            }
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

    private static PersonaState Active(CharacterState state)
    {
        if (!state.Personas.TryGetValue(state.Current, out PersonaState? persona))
        {
            state.Personas[state.Current] = persona = new PersonaState();
        }

        return persona;
    }

    private static void SetLevel(PersonaState persona, int level, long time)
    {
        if (level > 0 && time >= persona.LevelTime)
        {
            persona.Level = level;
            persona.LevelTime = time;
        }
    }

    /// <summary>
    /// Makes <paramref name="persona"/> the one that is on. What was counted while no persona was known goes to it when the line says
    /// it was already on, otherwise to the character's known class.
    /// </summary>
    private void SwitchTo(CharacterState state, string persona, bool unknownWasThis, string character, string server, long time)
    {
        string before = unknownWasThis ? persona
            : EqClass.TryNormalize(_classOf(character, server), out string knownClass) ? knownClass : string.Empty;
        if (state.Timeline.Count == 0)
        {
            if (before.Length > 0)
            {
                state.Timeline.Add(new PersonaMark { Since = 0, Persona = before }); // what was on before the first line that names one
            }
        }

        if (state.Timeline.Count == 0 || !state.Timeline[^1].Persona.Equals(persona, StringComparison.OrdinalIgnoreCase))
        {
            state.Timeline.Add(new PersonaMark { Since = time, Persona = persona });
            if (state.Timeline.Count > MaxTimeline)
            {
                state.Timeline.RemoveRange(0, state.Timeline.Count - MaxTimeline);
            }
        }

        if (state.Current.Length == 0 && state.Personas.Remove(string.Empty, out PersonaState? unknown))
        {
            string owner = before;
            if (!state.Personas.TryGetValue(owner, out PersonaState? into))
            {
                state.Personas[owner] = into = new PersonaState();
            }

            Merge(unknown, into);
        }

        state.Current = persona;
    }

    /// <summary>Adds the earlier numbers in <paramref name="from"/> to <paramref name="into"/>; a later skill value or level wins.</summary>
    private static void Merge(PersonaState from, PersonaState into)
    {
        foreach ((string name, SkillState skill) in from.Skills)
        {
            if (!into.Skills.TryGetValue(name, out SkillState? have) || have.Last < skill.Last)
            {
                if (have is not null)
                {
                    skill.Ups.AddRange(have.Ups);
                }

                into.Skills[name] = skill;
            }
            else
            {
                have.Ups.AddRange(skill.Ups);
            }
        }

        foreach ((string name, FactionState faction) in from.Factions)
        {
            if (!into.Factions.TryGetValue(name, out FactionState? have))
            {
                into.Factions[name] = faction;
                continue;
            }

            have.AllTime += faction.AllTime;
            have.Hits += faction.Hits;
            if (faction.Recent is { Count: > 0 })
            {
                have.Recent ??= new List<long[]>();
                have.Recent.AddRange(faction.Recent);
                have.Recent.Sort((a, b) => a[0].CompareTo(b[0]));
                if (have.Recent.Count > MaxRecentFactionHits)
                {
                    have.Recent.RemoveRange(0, have.Recent.Count - MaxRecentFactionHits);
                }
            }

            have.Session += faction.Session;
            foreach ((int amount, int count) in faction.Amounts)
            {
                have.Amounts[amount] = have.Amounts.GetValueOrDefault(amount) + count;
            }

            foreach ((string mob, int count) in faction.Mobs)
            {
                have.Mobs[mob] = have.Mobs.GetValueOrDefault(mob) + count;
            }

            if (faction.Last > have.Last)
            {
                have.Limit = faction.Limit;
                have.Last = faction.Last;
            }
        }

        if (from.LevelTime > into.LevelTime)
        {
            into.Level = from.Level;
            into.LevelTime = from.LevelTime;
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

    /// <summary>Starts reading a log's history: lines go to <see cref="LogScan.Line"/>, and <see cref="LogScan.Commit"/> replaces what that log gave before.</summary>
    public LogScan BeginScan(string character, string server)
    {
        string key = Key(character, server);
        lock (_gate)
        {
            _liveDuringScan[key] = new List<(string, long)>();
        }

        return new LogScan(this, key, character, server);
    }

    /// <summary>One log being read from the start.</summary>
    public sealed class LogScan
    {
        private readonly ProgressStore _store;
        private readonly string _key;
        private readonly string _character;
        private readonly string _server;
        private readonly Dictionary<string, CharacterState> _states = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (long Time, string Mob)> _kills = new(StringComparer.OrdinalIgnoreCase);
        private long _last;

        internal LogScan(ProgressStore store, string key, string character, string server)
        {
            _store = store;
            _key = key;
            _character = character;
            _server = server;
        }

        public void Line(string message, long time)
        {
            if (Wanted(message))
            {
                _store.Read(_states, _kills, _character, _server, _key, message, time);
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
                if (state.Current.Length == 0 && state.Personas.ContainsKey(string.Empty)
                    && EqClass.TryNormalize(_store._classOf(_character, _server), out string known))
                {
                    _store.SwitchTo(state, known, unknownWasThis: true, _character, _server, 0); // a log that never names a persona
                }

                _store._characters[_key] = state;
                foreach ((string message, long time) in live ?? new List<(string, long)>())
                {
                    if (time > _last + 1)
                    {
                        _store.Read(_store._characters, _store._lastKill, _character, _server, _key, message, time);
                    }
                }

                _store._scanned.Add(_key);
                _store.Changed();
            }
        }
    }

    // ───────────────────────── reading the numbers ─────────────────────────

    /// <summary>The character's personas, A to Z, with their levels and which one is on.</summary>
    public IReadOnlyList<PersonaInfo> Personas(string character, string server)
    {
        lock (_gate)
        {
            if (!_characters.TryGetValue(Key(character, server), out CharacterState? state))
            {
                return Array.Empty<PersonaInfo>();
            }

            return state.Personas
                .Select(p => new PersonaInfo(p.Key, p.Value.Level, p.Key.Equals(state.Current, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Persona.Length == 0 ? 1 : 0)
                .ThenBy(p => p.Persona, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// The persona that was on at <paramref name="utc"/> ("Bard"), from the lines that switched personas, or empty when no log has
    /// named one. A time before the first switch gives the persona that was on before it, when that is known.
    /// </summary>
    public string PersonaAt(string character, string server, DateTime utc)
    {
        long time = LogHistoryReader.ToUnix(utc);
        lock (_gate)
        {
            if (!_characters.TryGetValue(Key(character, server), out CharacterState? state))
            {
                return string.Empty;
            }

            if (state.Timeline.Count == 0)
            {
                return state.Current;
            }

            PersonaMark? on = state.Timeline.LastOrDefault(m => m.Since <= time);
            return (on ?? state.Timeline[0]).Persona;
        }
    }

    /// <summary>The persona that is on ("Cleric"), or empty when no log has said.</summary>
    public string CurrentPersona(string character, string server)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(Key(character, server), out CharacterState? state) ? state.Current : string.Empty;
        }
    }

    private PersonaState? FindPersona(string character, string server, string? persona)
    {
        if (!_characters.TryGetValue(Key(character, server), out CharacterState? state))
        {
            return null;
        }

        return state.Personas.GetValueOrDefault(persona ?? state.Current);
    }

    /// <summary>A persona's skills (the current one when <paramref name="persona"/> is null), A to Z.</summary>
    public IReadOnlyList<SkillProgress> Skills(string character, string server, DateTime nowUtc, string? persona = null)
    {
        DateTime today = nowUtc.ToLocalTime().Date;
        lock (_gate)
        {
            if (FindPersona(character, server, persona) is not { } state)
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

    /// <summary>The character flags a character's log announced, oldest first.</summary>
    public IReadOnlyList<CharacterFlag> Flags(string character, string server)
    {
        lock (_gate)
        {
            if (!_characters.TryGetValue(Key(character, server), out CharacterState? state) || state.Flags is null)
            {
                return Array.Empty<CharacterFlag>();
            }

            return state.Flags.OrderBy(f => f.Time).Select(f => new CharacterFlag(f.Text, LogHistoryReader.FromUnix(f.Time))).ToList();
        }
    }

    /// <summary>The character's last /played total in minutes and when it was said, or null when the log never showed one.</summary>
    public (long Minutes, DateTime Utc)? Played(string character, string server)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(Key(character, server), out CharacterState? state) && state.PlayedAt > 0
                ? (state.Played, LogHistoryReader.FromUnix(state.PlayedAt))
                : null;
        }
    }

    /// <summary>The character's legacy server XP bonus % and creation date, as far as its log says.</summary>
    public (int? LegacyBonus, DateTime? Born) Legacy(string character, string server)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(Key(character, server), out CharacterState? state) ? (state.LegacyBonus, state.Born) : (null, null);
        }
    }

    /// <summary>When the character's log last showed its legacy bonus (UTC), or null when not known.</summary>
    public DateTime? LegacyBonusSeen(string character, string server)
    {
        lock (_gate)
        {
            return _characters.TryGetValue(Key(character, server), out CharacterState? state) && state.LegacyBonusAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(state.LegacyBonusAt).UtcDateTime
                : null;
        }
    }

    /// <summary>The items the character's log said were added to the key ring, oldest first.</summary>
    public IReadOnlyList<CharacterFlag> KeyRingAdds(string character, string server) => Marks(character, server, s => s.KeyRing);

    /// <summary>The achievements the character's log said were completed, oldest first.</summary>
    public IReadOnlyList<CharacterFlag> AchievementsDone(string character, string server) => Marks(character, server, s => s.Achievements);

    private IReadOnlyList<CharacterFlag> Marks(string character, string server, Func<CharacterState, List<FlagMark>?> pick)
    {
        lock (_gate)
        {
            if (!_characters.TryGetValue(Key(character, server), out CharacterState? state) || pick(state) is not { } marks)
            {
                return Array.Empty<CharacterFlag>();
            }

            return marks.OrderBy(f => f.Time).Select(f => new CharacterFlag(f.Text, LogHistoryReader.FromUnix(f.Time))).ToList();
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
                .Select(p => new TradeskillProgress(p.Key, p.Value.Made, p.Value.Failed, LogHistoryReader.FromUnix(p.Value.Last), p.Value.SkillUps))
                .OrderBy(r => r.Recipe, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>A persona's factions (the current one when <paramref name="persona"/> is null), A to Z.</summary>
    public IReadOnlyList<FactionProgress> Factions(string character, string server, string? persona = null)
    {
        lock (_gate)
        {
            if (FindPersona(character, server, persona) is not { } state)
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

    /// <summary>
    /// How much each of a persona's factions changed after <paramref name="sinceUtc"/>, from the last hits kept, and which hit their
    /// limit since then (+1 best, -1 worst).
    /// </summary>
    public IReadOnlyDictionary<string, (long Change, int Limit)> FactionChangesSince(string character, string server, string? persona, DateTime sinceUtc)
    {
        long since = LogHistoryReader.ToUnix(sinceUtc);
        lock (_gate)
        {
            var result = new Dictionary<string, (long, int)>(StringComparer.OrdinalIgnoreCase);
            if (FindPersona(character, server, persona) is not { } state)
            {
                return result;
            }

            foreach ((string name, FactionState f) in state.Factions)
            {
                long change = f.Recent?.Where(r => r[0] > since).Sum(r => r[1]) ?? 0;
                int limit = f.Last > since ? f.Limit : 0;
                if (change != 0 || limit != 0)
                {
                    result[name] = (change, limit);
                }
            }

            return result;
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
                // An older file (before personas) is not read: the logs are read again instead, which takes seconds.
                if (File.Exists(path) && JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path)) is { Format: FileFormat } saved)
                {
                    foreach (string key in saved.Scanned)
                    {
                        _scanned.Add(key);
                    }

                    foreach ((string key, CharacterState state) in saved.Characters)
                    {
                        state.Personas = new Dictionary<string, PersonaState>(state.Personas, StringComparer.OrdinalIgnoreCase);
                        state.Timeline ??= new List<PersonaMark>();
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
                Format = FileFormat,
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
        public int Format { get; set; }

        public List<string> Scanned { get; set; } = new();

        public Dictionary<string, CharacterState> Characters { get; set; } = new();
    }

    public sealed class CharacterState
    {
        /// <summary>The persona that is on ("Cleric"); empty until a log names one.</summary>
        public string Current { get; set; } = string.Empty;

        /// <summary>Skills, faction and level per persona; the empty name holds what came before any persona was known.</summary>
        public Dictionary<string, PersonaState> Personas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When each persona came on (oldest first), for which persona wore the gear in an inventory file.</summary>
        public List<PersonaMark> Timeline { get; set; } = new();

        /// <summary>Tradeskills are the character's, whatever persona is on.</summary>
        public Dictionary<string, RecipeState> Recipes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last combine's result (item and time) and a tradeskill skill-up waiting for its combine, while reading.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public (string Item, long Time)? LastCombine { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public long PendingTradeUp { get; set; }

        /// <summary>Character flags, oldest first.</summary>
        public List<FlagMark>? Flags { get; set; }

        /// <summary>The last /played total in minutes, and when it was said (unix seconds; 0 when never).</summary>
        public long Played { get; set; }

        public long PlayedAt { get; set; }

        /// <summary>The last legacy server XP bonus %, or null when never seen.</summary>
        public int? LegacyBonus { get; set; }

        /// <summary>When the last legacy bonus line was seen (unix seconds; 0 when not known).</summary>
        public long LegacyBonusAt { get; set; }

        /// <summary>When the character was created (as /played says it), or null.</summary>
        public DateTime? Born { get; set; }

        /// <summary>Items added to the key ring, by name (the first time each was seen).</summary>
        public List<FlagMark>? KeyRing { get; set; }

        /// <summary>Achievements completed, by name (the first time each was seen).</summary>
        public List<FlagMark>? Achievements { get; set; }
    }

    public sealed class FlagMark
    {
        public long Time { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    public sealed class PersonaMark
    {
        public long Since { get; set; }

        public string Persona { get; set; } = string.Empty;
    }

    public sealed class PersonaState
    {
        public int Level { get; set; }

        public long LevelTime { get; set; }

        public Dictionary<string, SkillState> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

        public int SkillUps { get; set; }
    }

    public sealed class FactionState
    {
        public long AllTime { get; set; }

        public int Hits { get; set; }

        public int Limit { get; set; }

        public long Last { get; set; }

        public Dictionary<int, int> Amounts { get; set; } = new();

        public Dictionary<string, int> Mobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The last hits as [unix time, amount], for the standing since a faction file.</summary>
        public List<long[]>? Recent { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public int Session { get; set; }
    }
}
