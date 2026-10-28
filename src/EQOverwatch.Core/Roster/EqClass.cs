namespace EQOverwatch.Core.Roster;

/// <summary>The sixteen EverQuest classes and their three-letter labels.</summary>
public static class EqClass
{
    private static readonly (string Name, string Abbreviation)[] Classes =
    {
        ("Warrior", "WAR"), ("Cleric", "CLR"), ("Paladin", "PAL"), ("Ranger", "RNG"),
        ("Shadow Knight", "SHD"), ("Druid", "DRU"), ("Monk", "MNK"), ("Bard", "BRD"),
        ("Rogue", "ROG"), ("Shaman", "SHM"), ("Necromancer", "NEC"), ("Wizard", "WIZ"),
        ("Magician", "MAG"), ("Enchanter", "ENC"), ("Beastlord", "BST"), ("Berserker", "BER"),
    };

    /// <summary>Every class name, in the order above.</summary>
    public static IReadOnlyList<string> Names { get; } = Classes.Select(c => c.Name).ToArray();

    /// <summary>
    /// Maps "monk", "Shadow Knight", "shadowknight" or "SHD" to the class name. Titles such as "Grandmaster" are not classes
    /// and return false.
    /// </summary>
    public static bool TryNormalize(string? text, out string className)
    {
        className = string.Empty;
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return false;
        }

        string squashed = trimmed.Replace(" ", string.Empty);
        foreach ((string name, string abbreviation) in Classes)
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name.Replace(" ", string.Empty), squashed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(abbreviation, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                className = name;
                return true;
            }
        }

        return false;
    }

    /// <summary>"Shadow Knight" becomes "SHD"; an unknown or empty name gives an empty string.</summary>
    public static string Abbreviate(string? className)
    {
        if (TryNormalize(className, out string name))
        {
            foreach ((string n, string abbreviation) in Classes)
            {
                if (n == name)
                {
                    return abbreviation;
                }
            }
        }

        return string.Empty;
    }
}
