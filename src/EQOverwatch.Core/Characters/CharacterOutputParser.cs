using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Characters;

/// <summary>Which <c>/outputfile</c> command wrote a file.</summary>
public enum OutputFileKind
{
    Inventory,
    Spellbook,
    MissingSpells,
}

/// <summary>What an output file's name says: <c>Reamiiv_frostreaver-CLR-Spellbook</c> is Reamiiv on frostreaver, a cleric, the spellbook.</summary>
/// <param name="ClassCode">"CLR", or empty for inventory files, whose names have no class part.</param>
public sealed record OutputFileInfo(string Name, string Server, string ClassCode, OutputFileKind Kind);

/// <summary>One item row of an inventory file. Empty slots are not kept.</summary>
public sealed record InventoryItem(string Location, string Name, int ItemId, int Count, int Slots)
{
    /// <summary>True for the account-wide shared bank, whose rows are the same in every character's file.</summary>
    [JsonIgnore]
    public bool IsShared => Location.StartsWith("SharedBank", StringComparison.OrdinalIgnoreCase);

    /// <summary>"General 1 · slot 5", "Shared bank 4 · slot 2", "Head", "Head · augment 1".</summary>
    [JsonIgnore]
    public string Where => InventoryLocation.Describe(Location);
}

/// <summary>One row of the key ring section of an inventory file.</summary>
public sealed record KeyRingEntry(string Type, string Name, int ItemId);

/// <summary>One line of a spellbook or missing-spells file.</summary>
public sealed record SpellListEntry(int Level, string Name);

/// <summary>An inventory file split into its two tables.</summary>
public sealed record InventoryFile(IReadOnlyList<InventoryItem> Items, IReadOnlyList<KeyRingEntry> KeyRing);

/// <summary>Turns the location text of an inventory row into something readable.</summary>
public static partial class InventoryLocation
{
    [GeneratedRegex(@"^(?<kind>General|Bank|SharedBank)\s*(?<n>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerRegex();

    [GeneratedRegex(@"^Slot(?<n>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SlotRegex();

    /// <summary>
    /// "General 1-Slot5" is slot 5 of the bag in General 1, and "General 1-Slot12-Slot1" is augment socket 1 of the item in it.
    /// Worn gear works the other way round: "Head-Slot1" is augment socket 1 of the item worn on the head.
    /// </summary>
    public static string Describe(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return string.Empty;
        }

        string[] parts = location.Trim().Split('-');
        var slots = new List<string>();
        for (int i = 1; i < parts.Length; i++)
        {
            Match slot = SlotRegex().Match(parts[i]);
            slots.Add(slot.Success ? slot.Groups["n"].Value : parts[i]);
        }

        Match container = ContainerRegex().Match(parts[0].Trim());
        if (!container.Success)
        {
            // Worn gear (and anything unknown): the first "-SlotN" is an augment socket.
            return slots.Count == 0 ? parts[0].Trim() : $"{parts[0].Trim()} · augment {slots[0]}";
        }

        string kind = container.Groups["kind"].Value.ToLowerInvariant() switch
        {
            "sharedbank" => "Shared bank",
            "bank" => "Bank",
            _ => "General",
        };
        string name = $"{kind} {container.Groups["n"].Value}";

        return slots.Count switch
        {
            0 => name,
            1 => $"{name} · slot {slots[0]}",
            _ => $"{name} · slot {slots[0]} · augment {slots[1]}",
        };
    }
}

/// <summary>
/// Reads the files EverQuest's <c>/outputfile</c> command writes into the game folder: <c>inventory</c>, <c>spellbook</c> and
/// <c>missingspells</c>. Spell lists are "level, tab, spell name" per line. An inventory file is a tab-separated table
/// (<c>Location, Name, ID, Count, Slots</c>) followed by a second table for the key ring (<c>KeyRing, Name, ID</c>); a new header
/// row switches tables. Lines that do not fit are skipped, never fatal.
/// </summary>
public static class CharacterOutputParser
{
    private const string EmptySlot = "Empty";

    /// <summary>
    /// Reads <c>Name_server-CLASS-Kind</c> or <c>Name_server-Kind</c>, with or without an extension. Names are letters only and
    /// servers letters and digits, so the result is always safe to use in a file name.
    /// </summary>
    public static bool TryParseFileName(string? path, out OutputFileInfo info)
    {
        info = new OutputFileInfo(string.Empty, string.Empty, string.Empty, OutputFileKind.Inventory);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(path.Trim());
        int underscore = stem.IndexOf('_');
        if (underscore <= 0)
        {
            return false;
        }

        string name = stem[..underscore];
        string[] parts = stem[(underscore + 1)..].Split('-');
        if (parts.Length < 2 || parts.Length > 3 || !TryParseKind(parts[^1], out OutputFileKind kind))
        {
            return false;
        }

        string server = parts[0];
        string classCode = parts.Length == 3 ? parts[1] : string.Empty;
        if (!name.All(char.IsLetter) || server.Length == 0 || !server.All(char.IsLetterOrDigit) || !classCode.All(char.IsLetterOrDigit))
        {
            return false;
        }

        info = new OutputFileInfo(name, server, classCode.ToUpperInvariant(), kind);
        return true;
    }

    private static bool TryParseKind(string text, out OutputFileKind kind)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "inventory":
                kind = OutputFileKind.Inventory;
                return true;
            case "spellbook":
                kind = OutputFileKind.Spellbook;
                return true;
            case "missingspells":
                kind = OutputFileKind.MissingSpells;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>Parses a spellbook or missing-spells file: "6&lt;tab&gt;Sanctuary" per line.</summary>
    public static List<SpellListEntry> ParseSpellList(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var result = new List<SpellListEntry>();
        foreach (string line in lines)
        {
            string[] columns = line.Split('\t');
            if (columns.Length < 2
                || !int.TryParse(columns[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)
                || level < 0)
            {
                continue;
            }

            string name = columns[1].Trim();
            if (name.Length > 0)
            {
                result.Add(new SpellListEntry(level, name));
            }
        }

        return result;
    }

    /// <summary>Parses an inventory file. Empty slots (name "Empty") are dropped.</summary>
    public static InventoryFile ParseInventory(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var items = new List<InventoryItem>();
        var keyRing = new List<KeyRingEntry>();
        bool inKeyRing = false;

        foreach (string line in lines)
        {
            string[] columns = line.Split('\t');
            string first = columns[0].Trim();

            if (first.Equals("Location", StringComparison.OrdinalIgnoreCase))
            {
                inKeyRing = false;
                continue;
            }

            if (first.Equals("KeyRing", StringComparison.OrdinalIgnoreCase))
            {
                inKeyRing = true;
                continue;
            }

            if (first.Length == 0 || columns.Length < 3)
            {
                continue;
            }

            string name = columns[1].Trim();
            if (name.Length == 0 || name.Equals(EmptySlot, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(columns[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                continue;
            }

            if (inKeyRing)
            {
                keyRing.Add(new KeyRingEntry(first, name, id));
                continue;
            }

            int count = columns.Length > 3 && int.TryParse(columns[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : 1;
            int slots = columns.Length > 4 && int.TryParse(columns[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : 0;
            items.Add(new InventoryItem(first, name, id, count, slots));
        }

        return new InventoryFile(items, keyRing);
    }
}
