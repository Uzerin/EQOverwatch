using System.Globalization;
using EQOverwatch.Core.Roster;

namespace EQOverwatch.Core.Progress;

/// <summary>
/// The most a skill can be for a class at a level, from the game's <c>Resources\skillcaps.txt</c> (lines <c>class^skill^level^cap^...</c>,
/// with EverQuest's class and skill numbers).
/// </summary>
public sealed class SkillCaps
{
    /// <summary>EverQuest's skill numbers, as skillcaps.txt uses them, with the names the "You have become better at" lines print.</summary>
    public static readonly IReadOnlyList<string> SkillNames = new[]
    {
        "1H Blunt", "1H Slashing", "2H Blunt", "2H Slashing", "Abjuration", "Alteration", "Apply Poison", "Archery", "Backstab",
        "Bind Wound", "Bash", "Block", "Brass Instruments", "Channeling", "Conjuration", "Defense", "Disarm", "Disarm Traps",
        "Divination", "Dodge", "Double Attack", "Dragon Punch", "Dual Wield", "Eagle Strike", "Evocation", "Feign Death", "Flying Kick",
        "Forage", "Hand to Hand", "Hide", "Kick", "Meditate", "Mend", "Offense", "Parry", "Pick Lock", "1H Piercing", "Riposte",
        "Round Kick", "Safe Fall", "Sense Heading", "Singing", "Sneak", "Specialize Abjure", "Specialize Alteration",
        "Specialize Conjuration", "Specialize Divination", "Specialize Evocation", "Pick Pockets", "Stringed Instruments", "Swimming",
        "Throwing", "Tiger Claw", "Tracking", "Wind Instruments", "Fishing", "Make Poison", "Tinkering", "Research", "Alchemy",
        "Baking", "Tailoring", "Sense Traps", "Blacksmithing", "Fletching", "Brewing", "Alcohol Tolerance", "Begging",
        "Jewelry Making", "Pottery", "Percussion Instruments", "Intimidation", "Berserking", "Taunt", "Frenzy", "Remove Traps",
        "Triple Attack", "2H Piercing",
    };

    private static readonly Dictionary<string, int> SkillIds = SkillNames
        .Select((name, id) => (name, id))
        .ToDictionary(p => p.name, p => p.id, StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(int Class, int Skill, int Level), int> _caps = new();

    public static SkillCaps Empty { get; } = new();

    public int Count => _caps.Count;

    /// <summary>Reads skillcaps.txt. A missing or unreadable file gives an empty table.</summary>
    public static SkillCaps Load(string? path)
    {
        var caps = new SkillCaps();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return caps;
        }

        try
        {
            foreach (string line in File.ReadLines(path))
            {
                caps.AddLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return caps;
    }

    /// <summary>Reads one <c>class^skill^level^cap^...</c> line; others are ignored.</summary>
    public void AddLine(string line)
    {
        string[] parts = line.Split('^');
        if (parts.Length >= 4
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int classId)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int skill)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
            && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cap))
        {
            _caps[(classId, skill, level)] = cap;
        }
    }

    /// <summary>The cap of a skill for a class ("Cleric" or "CLR") at a level, or 0 when it is not known.</summary>
    public int Cap(string className, string skill, int level)
    {
        if (level <= 0 || !EqClass.TryNormalize(className, out string name) || !SkillIds.TryGetValue(skill.Trim(), out int skillId))
        {
            return 0;
        }

        int classId = EqClass.Names.ToList().IndexOf(name) + 1; // EverQuest numbers the classes Warrior = 1 to Berserker = 16
        return _caps.GetValueOrDefault((classId, skillId, level));
    }
}
