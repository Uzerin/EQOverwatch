namespace EQOverwatch.Core.Settings;

/// <summary>A player the app has seen for certain on one server (in a /who list, or as the owner of a pet), with their class when known.</summary>
public sealed class RosterPlayerEntry
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Full class name ("Shadow Knight"), or empty when only the name is known.</summary>
    public string Class { get; set; } = string.Empty;

    /// <summary>The player's level, or 0 when it has not been seen.</summary>
    public int Level { get; set; }

    /// <summary>The server the player was seen on (empty in settings saved before servers were kept).</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Increases every time the player is seen, so the most recent sighting across servers is known.</summary>
    public long Seen { get; set; }
}

/// <summary>A pet and the player it belongs to.</summary>
public sealed class RosterPetEntry
{
    public string Pet { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;

    /// <summary>The server the pet and its owner are on (empty in settings saved before servers were kept).</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Increases every time the pet is seen.</summary>
    public long Seen { get; set; }
}

/// <summary>What is saved of the player roster.</summary>
public sealed class RosterSettings
{
    public List<RosterPlayerEntry> Players { get; set; } = new();
    public List<RosterPetEntry> Pets { get; set; } = new();

    /// <summary>Count a pet's damage under its owner (one row per player) instead of listing the pet separately.</summary>
    public bool MergePetDamage { get; set; } = true;
}
