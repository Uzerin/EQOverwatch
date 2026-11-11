using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.Core.Roster;

/// <summary>
/// The players and pets the app is sure about, kept per server. A name enters the roster when it shows up in a /who list (with its
/// class and level) or when a pet answers <c>/pet leader</c> ("My leader is Oodles.") - that proves the owner is a player and links
/// the pet to them. Everything is learned on the server of the log that saw the line, so a character with the same name on another
/// server is a different entry. The DPS tracker uses it to tell players from NPCs, to put pet damage on the owner's row, and the
/// Class column reads it.
/// <para>
/// Lookups with a server read that server's entry (or an old entry saved before servers were kept). Lookups without a server read
/// the entry seen most recently on any server.
/// </para>
/// <para>
/// Thread-safe. <see cref="Changed"/> fires on whichever thread learned something, so handlers must marshal to the UI themselves.
/// </para>
/// </summary>
public sealed class PlayerRoster : ITriggerLineSink
{
    public const int MaxPlayers = 5000;
    public const int MaxPets = 2000;

    /// <summary>The highest level a player can be.</summary>
    public const int MaxLevel = 130;

    /// <summary>What the Class column shows for a pet.</summary>
    public const string PetLabel = "Pet";

    private readonly object _gate = new();
    private readonly record struct PlayerInfo(string Class, int Level, long Seen);
    private readonly record struct PetInfo(string Owner, long Seen);

    private readonly Dictionary<RosterKey, PlayerInfo> _players = new(RosterKey.Comparer); // class "" and level 0 when unknown
    private readonly Dictionary<RosterKey, PetInfo> _pets = new(RosterKey.Comparer);       // pet -> owner (same server)

    // The server each name was seen on most recently, for lookups that do not say which server.
    private readonly Dictionary<string, string> _latestPlayerServer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _latestPetServer = new(StringComparer.Ordinal);
    private long _seen;

    /// <summary>When true (the default), the DPS tracker counts a pet's damage under its owner.</summary>
    public bool MergePetDamage { get; set; } = true;

    /// <summary>Raised after the roster changed (a new player, class or pet, or a removal).</summary>
    public event Action? Changed;

    public int PlayerCount
    {
        get
        {
            lock (_gate)
            {
                return _players.Count;
            }
        }
    }

    public int PetCount
    {
        get
        {
            lock (_gate)
            {
                return _pets.Count;
            }
        }
    }

    public bool IsVerifiedPlayer(string name) => IsVerifiedPlayer(name, null);

    public bool IsVerifiedPlayer(string name, string? server)
    {
        lock (_gate)
        {
            return FindPlayerLocked(name, server, out _, out _);
        }
    }

    public bool IsPet(string name) => IsPet(name, null);

    public bool IsPet(string name, string? server)
    {
        lock (_gate)
        {
            return FindPetLocked(name, server, out _);
        }
    }

    public bool TryGetOwner(string pet, out string owner) => TryGetOwner(pet, null, out owner);

    public bool TryGetOwner(string pet, string? server, out string owner)
    {
        lock (_gate)
        {
            if (FindPetLocked(pet, server, out PetInfo info))
            {
                owner = info.Owner;
                return true;
            }

            owner = string.Empty;
            return false;
        }
    }

    /// <summary>The player's class name ("Shadow Knight"), or empty when unknown.</summary>
    public string GetClass(string name) => GetClass(name, null);

    /// <summary>The player's class name on <paramref name="server"/> (any server when empty), or empty when unknown.</summary>
    public string GetClass(string name, string? server)
    {
        lock (_gate)
        {
            return FindPlayerLocked(name, server, out _, out PlayerInfo info) ? info.Class : string.Empty;
        }
    }

    /// <summary>The player's level, or 0 when it has not been seen.</summary>
    public int GetLevel(string name) => GetLevel(name, null);

    /// <summary>The player's level on <paramref name="server"/> (any server when empty), or 0 when it has not been seen.</summary>
    public int GetLevel(string name, string? server)
    {
        lock (_gate)
        {
            return FindPlayerLocked(name, server, out _, out PlayerInfo info) ? info.Level : 0;
        }
    }

    /// <summary>The server the player was seen on most recently, or empty when unknown.</summary>
    public string GetLatestServer(string name)
    {
        lock (_gate)
        {
            return _latestPlayerServer.GetValueOrDefault(name?.Trim() ?? string.Empty, string.Empty);
        }
    }

    /// <summary>What the Class column shows: "SHD" for a known class, "Pet" for a pet, otherwise empty.</summary>
    public string GetClassLabel(string name) => GetClassLabel(name, null);

    /// <summary>The Class column for a name on <paramref name="server"/> (any server when empty).</summary>
    public string GetClassLabel(string name, string? server)
    {
        lock (_gate)
        {
            if (FindPlayerLocked(name, server, out _, out PlayerInfo info))
            {
                return EqClass.Abbreviate(info.Class);
            }

            return FindPetLocked(name, server, out _) ? PetLabel : string.Empty;
        }
    }

    /// <summary>Remembers a player (and their class and level when given). Returns true when something changed.</summary>
    public bool LearnPlayer(string name, string? className = null, int level = 0, string? server = null)
    {
        bool changed;
        lock (_gate)
        {
            changed = LearnPlayerLocked(name, className, level, server);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    /// <summary>Links a pet to its owner, who becomes a verified player on the same server. Returns true when something changed.</summary>
    public bool LearnPet(string pet, string owner, string? server = null)
    {
        bool changed;
        lock (_gate)
        {
            changed = LearnPetLocked(pet, owner, server);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    /// <summary>Forgets a player and every pet linked to them, on <paramref name="server"/> or on every server when it is null.</summary>
    public bool ForgetPlayer(string name, string? server = null)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (RosterKey key in _players.Keys.Where(k => k.Name == name && Matches(k.Server, server)).ToList())
            {
                _players.Remove(key);
                changed = true;
            }

            foreach (KeyValuePair<RosterKey, PetInfo> pet in _pets.Where(p => p.Value.Owner == name && Matches(p.Key.Server, server)).ToList())
            {
                _pets.Remove(pet.Key);
                changed = true;
            }

            RebuildLatestLocked();
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    /// <summary>Forgets a pet, on <paramref name="server"/> or on every server when it is null.</summary>
    public bool ForgetPet(string pet, string? server = null)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (RosterKey key in _pets.Keys.Where(k => k.Name == pet && Matches(k.Server, server)).ToList())
            {
                _pets.Remove(key);
                changed = true;
            }

            RebuildLatestLocked();
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    /// <summary>Forgets every pet, leaving the players alone. Returns how many pets were forgotten.</summary>
    public int ForgetAllPets()
    {
        int count;
        lock (_gate)
        {
            count = _pets.Count;
            _pets.Clear();
            _latestPetServer.Clear();
        }

        if (count > 0)
        {
            Changed?.Invoke();
        }

        return count;
    }

    public void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _players.Count > 0 || _pets.Count > 0;
            _players.Clear();
            _pets.Clear();
            _latestPlayerServer.Clear();
            _latestPetServer.Clear();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Reads a log message; true when it taught the roster something new.</summary>
    public bool Observe(ReadOnlySpan<char> message) => Observe(message, null);

    /// <summary>Reads a log message from a log on <paramref name="server"/>; true when it taught the roster something new.</summary>
    public bool Observe(ReadOnlySpan<char> message, string? server)
    {
        if (!RosterLineParser.TryParse(message, out RosterObservation observation))
        {
            return false;
        }

        return observation.Kind == RosterObservationKind.Pet
            ? LearnPet(observation.Name, observation.Owner, server)
            : LearnPlayer(observation.Name, observation.Class, observation.Level, server);
    }

    /// <summary>Every trigger-enabled log's lines arrive here: /who and /pet leader lines, and the log owner's level-ups.</summary>
    void ITriggerLineSink.OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
        ((ITriggerLineSink)this).OnLine(characterName, string.Empty, message, receivedUtc);

    void ITriggerLineSink.OnLine(string characterName, string serverName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (!Observe(message, serverName) && RosterLineParser.TryParseLevelUp(message, out int level))
        {
            LearnPlayer(characterName, null, level, serverName);
        }
    }

    /// <summary>
    /// Files entries saved before servers were kept (server empty) under the server <paramref name="serverFor"/> gives for the name.
    /// An entry the server already has keeps the newer data. Returns true when something moved.
    /// </summary>
    public bool AssignLegacyServers(Func<string, string?> serverFor)
    {
        ArgumentNullException.ThrowIfNull(serverFor);
        bool changed = false;
        lock (_gate)
        {
            foreach (KeyValuePair<RosterKey, PlayerInfo> old in _players.Where(p => p.Key.Server.Length == 0).ToList())
            {
                string server = serverFor(old.Key.Name)?.Trim() ?? string.Empty;
                if (server.Length == 0)
                {
                    continue;
                }

                _players.Remove(old.Key);
                var key = new RosterKey(server, old.Key.Name);
                if (_players.TryGetValue(key, out PlayerInfo existing))
                {
                    _players[key] = new PlayerInfo(
                        existing.Class.Length > 0 ? existing.Class : old.Value.Class,
                        existing.Level > 0 ? existing.Level : old.Value.Level,
                        Math.Max(existing.Seen, old.Value.Seen));
                }
                else
                {
                    _players[key] = old.Value;
                }

                changed = true;
            }

            foreach (KeyValuePair<RosterKey, PetInfo> old in _pets.Where(p => p.Key.Server.Length == 0).ToList())
            {
                string server = serverFor(old.Value.Owner)?.Trim() ?? string.Empty;
                if (server.Length == 0)
                {
                    continue;
                }

                _pets.Remove(old.Key);
                var key = new RosterKey(server, old.Key.Name);
                if (!_pets.ContainsKey(key) && !_players.ContainsKey(key))
                {
                    _pets[key] = old.Value;
                }

                changed = true;
            }

            if (changed)
            {
                RebuildLatestLocked();
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    // ─────────────────────────── persistence ───────────────────────────

    /// <summary>Replaces the roster with saved data. Raises no event. Invalid and duplicate entries are dropped.</summary>
    public void Load(RosterSettings? saved)
    {
        lock (_gate)
        {
            _players.Clear();
            _pets.Clear();
            _latestPlayerServer.Clear();
            _latestPetServer.Clear();
            _seen = 0;
            MergePetDamage = saved?.MergePetDamage ?? true;

            foreach (RosterPlayerEntry? entry in saved?.Players ?? new List<RosterPlayerEntry>())
            {
                if (entry is not null)
                {
                    LearnPlayerLocked(entry.Name, entry.Class, entry.Level, entry.Server, entry.Seen);
                }
            }

            foreach (RosterPetEntry? entry in saved?.Pets ?? new List<RosterPetEntry>())
            {
                if (entry is not null)
                {
                    LearnPetLocked(entry.Pet, entry.Owner, entry.Server, entry.Seen);
                }
            }
        }
    }

    public RosterSettings ToSettings()
    {
        lock (_gate)
        {
            return new RosterSettings
            {
                MergePetDamage = MergePetDamage,
                Players = _players
                    .OrderBy(p => p.Key.Name, StringComparer.Ordinal)
                    .ThenBy(p => p.Key.Server, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new RosterPlayerEntry { Name = p.Key.Name, Server = p.Key.Server, Class = p.Value.Class, Level = p.Value.Level, Seen = p.Value.Seen })
                    .ToList(),
                Pets = _pets
                    .OrderBy(p => p.Key.Name, StringComparer.Ordinal)
                    .ThenBy(p => p.Key.Server, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new RosterPetEntry { Pet = p.Key.Name, Owner = p.Value.Owner, Server = p.Key.Server, Seen = p.Value.Seen })
                    .ToList(),
            };
        }
    }

    /// <summary>Cleans loaded settings (used when the settings file is read).</summary>
    public static RosterSettings Sanitize(RosterSettings? saved)
    {
        var roster = new PlayerRoster();
        roster.Load(saved);
        return roster.ToSettings();
    }

    // ─────────────────────────── internals ───────────────────────────

    private static string Clean(string? server) => server?.Trim() ?? string.Empty;

    private static bool Matches(string entryServer, string? server) =>
        server is null || entryServer.Equals(server.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// With a server: that server's entry, or an entry saved before servers were kept. Without one: the entry seen most recently.
    /// </summary>
    private bool FindPlayerLocked(string? name, string? server, out RosterKey key, out PlayerInfo info)
    {
        name = name?.Trim() ?? string.Empty;
        server = Clean(server);
        if (server.Length > 0)
        {
            key = new RosterKey(server, name);
            if (_players.TryGetValue(key, out info))
            {
                return true;
            }

            key = new RosterKey(string.Empty, name);
            return _players.TryGetValue(key, out info);
        }

        if (_latestPlayerServer.TryGetValue(name, out string? latest))
        {
            key = new RosterKey(latest, name);
            return _players.TryGetValue(key, out info);
        }

        key = default;
        info = default;
        return false;
    }

    private bool FindPetLocked(string? name, string? server, out PetInfo info)
    {
        name = name?.Trim() ?? string.Empty;
        server = Clean(server);
        if (server.Length > 0)
        {
            return _pets.TryGetValue(new RosterKey(server, name), out info)
                || _pets.TryGetValue(new RosterKey(string.Empty, name), out info);
        }

        if (_latestPetServer.TryGetValue(name, out string? latest))
        {
            return _pets.TryGetValue(new RosterKey(latest, name), out info);
        }

        info = default;
        return false;
    }

    private bool LearnPlayerLocked(string? name, string? className, int level, string? server, long seen = 0)
    {
        name = name?.Trim() ?? string.Empty;
        server = Clean(server);
        var key = new RosterKey(server, name);
        if (!PlayerNameHeuristic.CouldBePlayer(name) || _pets.ContainsKey(key))
        {
            return false;
        }

        string normalized = EqClass.TryNormalize(className, out string known) ? known : string.Empty;
        level = level is >= 1 and <= MaxLevel ? level : 0;
        long stamp = NextSeen(seen);
        if (_players.TryGetValue(key, out PlayerInfo existing))
        {
            // A sighting without a class or level never wipes what is known.
            var merged = new PlayerInfo(normalized.Length > 0 ? normalized : existing.Class, level > 0 ? level : existing.Level, existing.Seen);
            bool changed = merged != existing;
            _players[key] = merged with { Seen = stamp };
            MarkLatestPlayer(name, server);
            return changed;
        }

        if (_players.Count >= MaxPlayers)
        {
            return false;
        }

        _players[key] = new PlayerInfo(normalized, level, stamp);
        MarkLatestPlayer(name, server);
        return true;
    }

    private bool LearnPetLocked(string? pet, string? owner, string? server, long seen = 0)
    {
        pet = pet?.Trim() ?? string.Empty;
        owner = owner?.Trim() ?? string.Empty;
        server = Clean(server);
        var key = new RosterKey(server, pet);

        // Someone already proven to be a player on this server is not a pet: this stops a chat line from turning a player into one.
        if (pet.Length == 0 || pet == owner || _players.ContainsKey(key) || !PlayerNameHeuristic.CouldBePlayer(owner))
        {
            return false;
        }

        long stamp = NextSeen(seen);
        bool changed;
        if (_pets.TryGetValue(key, out PetInfo current))
        {
            changed = current.Owner != owner;
        }
        else if (_pets.Count >= MaxPets)
        {
            return false;
        }
        else
        {
            changed = true;
        }

        _pets[key] = new PetInfo(owner, stamp);
        _latestPetServer[pet] = server;
        LearnPlayerLocked(owner, null, 0, server);
        return changed;
    }

    private long NextSeen(long seen)
    {
        if (seen > 0)
        {
            _seen = Math.Max(_seen, seen);
            return seen;
        }

        return ++_seen;
    }

    /// <summary>Notes that <paramref name="name"/> was just seen on <paramref name="server"/>. An old entry without a server never hides one that has a server.</summary>
    private void MarkLatestPlayer(string name, string server)
    {
        if (!_latestPlayerServer.TryGetValue(name, out string? current))
        {
            _latestPlayerServer[name] = server;
            return;
        }

        if (server.Length == 0 && current.Length > 0)
        {
            return;
        }

        long currentSeen = _players.TryGetValue(new RosterKey(current, name), out PlayerInfo info) ? info.Seen : 0;
        if (current.Length == 0 || _players[new RosterKey(server, name)].Seen >= currentSeen)
        {
            _latestPlayerServer[name] = server;
        }
    }

    private void RebuildLatestLocked()
    {
        _latestPlayerServer.Clear();
        foreach (var group in _players.GroupBy(p => p.Key.Name, StringComparer.Ordinal))
        {
            _latestPlayerServer[group.Key] = group
                .OrderByDescending(p => p.Key.Server.Length > 0)
                .ThenByDescending(p => p.Value.Seen)
                .First().Key.Server;
        }

        _latestPetServer.Clear();
        foreach (var group in _pets.GroupBy(p => p.Key.Name, StringComparer.Ordinal))
        {
            _latestPetServer[group.Key] = group.OrderByDescending(p => p.Value.Seen).First().Key.Server;
        }
    }

    /// <summary>A name on a server: servers compare without regard to capitals, names exactly.</summary>
    private readonly record struct RosterKey(string Server, string Name)
    {
        public static readonly IEqualityComparer<RosterKey> Comparer = new KeyComparer();

        private sealed class KeyComparer : IEqualityComparer<RosterKey>
        {
            public bool Equals(RosterKey x, RosterKey y) =>
                string.Equals(x.Name, y.Name, StringComparison.Ordinal) && string.Equals(x.Server, y.Server, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(RosterKey key) =>
                HashCode.Combine(StringComparer.Ordinal.GetHashCode(key.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Server ?? string.Empty));
        }
    }
}
