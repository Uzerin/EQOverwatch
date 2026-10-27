using EQCompanion.Core.Pipeline;
using EQCompanion.Core.Services;
using EQCompanion.Core.Settings;

namespace EQCompanion.Core.Roster;

/// <summary>
/// The players and pets the app is sure about. A name enters the roster when it shows up in a /who list (with its class) or when
/// a pet answers <c>/pet leader</c> ("My leader is Oodles.") - that proves the owner is a player and links the pet to them.
/// The DPS tracker uses it to tell players from NPCs, to put pet damage on the owner's row, and the Class column reads it.
/// <para>
/// Thread-safe. <see cref="Changed"/> fires on whichever thread learned something, so handlers must marshal to the UI themselves.
/// </para>
/// </summary>
public sealed class PlayerRoster : ITriggerLineSink
{
    public const int MaxPlayers = 5000;
    public const int MaxPets = 2000;

    /// <summary>What the Class column shows for a pet.</summary>
    public const string PetLabel = "Pet";

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _players = new(StringComparer.Ordinal); // name -> class ("" when unknown)
    private readonly Dictionary<string, string> _pets = new(StringComparer.Ordinal);    // pet -> owner

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

    public bool IsVerifiedPlayer(string name)
    {
        lock (_gate)
        {
            return _players.ContainsKey(name);
        }
    }

    public bool IsPet(string name)
    {
        lock (_gate)
        {
            return _pets.ContainsKey(name);
        }
    }

    public bool TryGetOwner(string pet, out string owner)
    {
        lock (_gate)
        {
            return _pets.TryGetValue(pet, out owner!);
        }
    }

    /// <summary>The player's class name ("Shadow Knight"), or empty when unknown.</summary>
    public string GetClass(string name)
    {
        lock (_gate)
        {
            return _players.TryGetValue(name, out string? className) ? className : string.Empty;
        }
    }

    /// <summary>What the Class column shows: "SHD" for a known class, "Pet" for a pet, otherwise empty.</summary>
    public string GetClassLabel(string name)
    {
        lock (_gate)
        {
            if (_players.TryGetValue(name, out string? className))
            {
                return EqClass.Abbreviate(className);
            }

            return _pets.ContainsKey(name) ? PetLabel : string.Empty;
        }
    }

    /// <summary>Remembers a player (and their class when given). Returns true when something changed.</summary>
    public bool LearnPlayer(string name, string? className = null)
    {
        bool changed;
        lock (_gate)
        {
            changed = LearnPlayerLocked(name, className);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    /// <summary>Links a pet to its owner, who becomes a verified player. Returns true when something changed.</summary>
    public bool LearnPet(string pet, string owner)
    {
        bool changed;
        lock (_gate)
        {
            changed = LearnPetLocked(pet, owner);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    /// <summary>Forgets a player and every pet linked to them.</summary>
    public bool ForgetPlayer(string name)
    {
        bool changed;
        lock (_gate)
        {
            changed = _players.Remove(name);
            foreach (string pet in _pets.Where(p => p.Value == name).Select(p => p.Key).ToList())
            {
                _pets.Remove(pet);
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    public bool ForgetPet(string pet)
    {
        bool changed;
        lock (_gate)
        {
            changed = _pets.Remove(pet);
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return changed;
    }

    public void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = _players.Count > 0 || _pets.Count > 0;
            _players.Clear();
            _pets.Clear();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Reads a log message; true when it taught the roster something new.</summary>
    public bool Observe(ReadOnlySpan<char> message)
    {
        if (!RosterLineParser.TryParse(message, out RosterObservation observation))
        {
            return false;
        }

        return observation.Kind == RosterObservationKind.Pet
            ? LearnPet(observation.Name, observation.Owner)
            : LearnPlayer(observation.Name, observation.Class);
    }

    void ITriggerLineSink.OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) => Observe(message);

    // ─────────────────────────── persistence ───────────────────────────

    /// <summary>Replaces the roster with saved data. Raises no event. Invalid and duplicate entries are dropped.</summary>
    public void Load(RosterSettings? saved)
    {
        lock (_gate)
        {
            _players.Clear();
            _pets.Clear();
            MergePetDamage = saved?.MergePetDamage ?? true;

            foreach (RosterPlayerEntry? entry in saved?.Players ?? new List<RosterPlayerEntry>())
            {
                if (entry is not null)
                {
                    LearnPlayerLocked(entry.Name, entry.Class);
                }
            }

            foreach (RosterPetEntry? entry in saved?.Pets ?? new List<RosterPetEntry>())
            {
                if (entry is not null)
                {
                    LearnPetLocked(entry.Pet, entry.Owner);
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
                Players = _players.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new RosterPlayerEntry { Name = p.Key, Class = p.Value }).ToList(),
                Pets = _pets.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => new RosterPetEntry { Pet = p.Key, Owner = p.Value }).ToList(),
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

    private bool LearnPlayerLocked(string? name, string? className)
    {
        name = name?.Trim() ?? string.Empty;
        if (!PlayerNameHeuristic.CouldBePlayer(name) || _pets.ContainsKey(name))
        {
            return false;
        }

        string normalized = EqClass.TryNormalize(className, out string known) ? known : string.Empty;
        if (_players.TryGetValue(name, out string? existing))
        {
            if (normalized.Length == 0 || existing == normalized)
            {
                return false;
            }

            _players[name] = normalized;
            return true;
        }

        if (_players.Count >= MaxPlayers)
        {
            return false;
        }

        _players[name] = normalized;
        return true;
    }

    private bool LearnPetLocked(string? pet, string? owner)
    {
        pet = pet?.Trim() ?? string.Empty;
        owner = owner?.Trim() ?? string.Empty;

        // Someone already proven to be a player is not a pet: this stops a chat line from turning a player into one.
        if (pet.Length == 0 || pet == owner || _players.ContainsKey(pet) || !PlayerNameHeuristic.CouldBePlayer(owner))
        {
            return false;
        }

        if (_pets.TryGetValue(pet, out string? current))
        {
            if (current == owner)
            {
                return false;
            }
        }
        else if (_pets.Count >= MaxPets)
        {
            return false;
        }

        _pets[pet] = owner;
        LearnPlayerLocked(owner, null);
        return true;
    }
}
