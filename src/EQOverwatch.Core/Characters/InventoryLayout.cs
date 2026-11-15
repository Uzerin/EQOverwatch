namespace EQOverwatch.Core.Characters;

/// <summary>
/// One slot of the inventory picture: what is in it (null when empty), its augment sockets (true = an augment is in it), and, for a
/// bag, its capacity and contents.
/// </summary>
public sealed record InventorySlot(
    string Location,
    string Label,
    string? Item,
    int ItemId,
    int Count,
    IReadOnlyList<bool> Augments,
    int Capacity,
    IReadOnlyList<InventorySlot> Contents)
{
    public bool IsEmpty => Item is null;

    public bool IsBag => Capacity > 0;
}

/// <summary>
/// Turns the rows of an inventory file into the slots the game shows: the worn gear in the game's order, and the containers
/// (General, Bank, Shared bank) with what is inside each bag. "Head-Slot1" is an augment socket of the worn helm; "General 1-Slot5" is
/// slot 5 of the bag in General 1 when General 1 holds a bag, otherwise an augment socket; "General 1-Slot5-Slot1" is an augment
/// socket of the item in that bag slot.
/// </summary>
public static class InventoryLayout
{
    /// <summary>The worn slots in the order the inventory window shows them (the file lists them the same way).</summary>
    public static readonly IReadOnlyList<string> WornSlots = new[]
    {
        "Charm", "Ear", "Head", "Face", "Ear", "Neck", "Shoulders", "Arms", "Back", "Wrist", "Wrist", "Range", "Hands", "Primary",
        "Secondary", "Fingers", "Fingers", "Chest", "Legs", "Feet", "Waist", "Power Source", "Ammo",
    };

    private sealed class Node
    {
        public required InventoryItem Row { get; init; }

        public List<Node> Children { get; } = new();
    }

    /// <summary>The worn gear, one entry per slot of <see cref="WornSlots"/> (empty when the file does not list it).</summary>
    public static IReadOnlyList<InventorySlot> Worn(IEnumerable<InventoryItem> rows)
    {
        List<Node> tops = Tree(rows);
        var used = new HashSet<Node>();
        var result = new List<InventorySlot>();
        foreach (string slot in WornSlots)
        {
            Node? node = tops.FirstOrDefault(n => !used.Contains(n) && n.Row.Location.Equals(slot, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                result.Add(new InventorySlot(slot, slot, null, 0, 0, Array.Empty<bool>(), 0, Array.Empty<InventorySlot>()));
                continue;
            }

            used.Add(node);
            result.Add(Slot(node, slot, worn: true));
        }

        return result;
    }

    /// <summary>
    /// The numbered containers of one kind ("General", "Bank" or "SharedBank"), in number order, from 1 to the highest the file lists.
    /// </summary>
    public static IReadOnlyList<InventorySlot> Containers(IEnumerable<InventoryItem> rows, string kind, string label)
    {
        var numbered = new SortedDictionary<int, Node>();
        foreach (Node node in Tree(rows))
        {
            string location = node.Row.Location.Replace(" ", string.Empty);
            if (location.StartsWith(kind, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(location[kind.Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n)
                && !(kind.Equals("Bank", StringComparison.OrdinalIgnoreCase) && location.StartsWith("SharedBank", StringComparison.OrdinalIgnoreCase)))
            {
                numbered.TryAdd(n, node);
            }
        }

        if (numbered.Count == 0)
        {
            return Array.Empty<InventorySlot>();
        }

        var result = new List<InventorySlot>();
        for (int n = 1; n <= numbered.Keys.Max(); n++)
        {
            string name = $"{label} {n}";
            result.Add(numbered.TryGetValue(n, out Node? node)
                ? Slot(node, name, worn: false)
                : new InventorySlot(name, name, null, 0, 0, Array.Empty<bool>(), 0, Array.Empty<InventorySlot>()));
        }

        return result;
    }

    private static InventorySlot Slot(Node node, string label, bool worn)
    {
        InventoryItem row = node.Row;
        bool empty = IsEmptyRow(row);
        // A bag lists a row for each of its slots (its Slots column is how many), and its slots can hold items with sockets of their
        // own; an item's augment sockets never have rows under them.
        bool bag = !worn && !empty && node.Children.Count > 0
                   && (node.Children.Count == row.Slots || node.Children.Any(c => c.Children.Count > 0) || node.Children.Count > 6);
        if (bag)
        {
            var contents = new List<InventorySlot>();
            for (int i = 0; i < Math.Max(row.Slots, node.Children.Count); i++)
            {
                contents.Add(i < node.Children.Count
                    ? Slot(node.Children[i], $"{label} · slot {i + 1}", worn: true) // a bag slot's children are sockets
                    : new InventorySlot(string.Empty, $"{label} · slot {i + 1}", null, 0, 0, Array.Empty<bool>(), 0, Array.Empty<InventorySlot>()));
            }

            return new InventorySlot(row.Location, label, row.Name, row.ItemId, row.Count, Array.Empty<bool>(), Math.Max(row.Slots, contents.Count), contents);
        }

        return new InventorySlot(
            row.Location,
            label,
            empty ? null : row.Name,
            row.ItemId,
            row.Count,
            empty ? Array.Empty<bool>() : node.Children.Select(c => !IsEmptyRow(c.Row)).ToList(),
            0,
            Array.Empty<InventorySlot>());
    }

    private static bool IsEmptyRow(InventoryItem row) => row.Name.Length == 0 || row.Name.Equals("Empty", StringComparison.OrdinalIgnoreCase);

    /// <summary>Hangs each "X-SlotN" row under the latest row named X (and "X-SlotN-SlotK" under that row's slot N).</summary>
    private static List<Node> Tree(IEnumerable<InventoryItem> rows)
    {
        var tops = new List<Node>();
        var latest = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (InventoryItem row in rows)
        {
            string location = row.Location.Trim();
            var node = new Node { Row = row };
            int dash = location.LastIndexOf("-Slot", StringComparison.OrdinalIgnoreCase);
            if (dash < 0)
            {
                tops.Add(node);
            }
            else if (latest.TryGetValue(location[..dash], out Node? parent))
            {
                parent.Children.Add(node);
            }

            latest[location] = node;
        }

        return tops;
    }
}
