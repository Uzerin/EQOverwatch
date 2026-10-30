namespace EQOverwatch.Core.Characters;

/// <summary>An item found in someone's bags or banks, or in the shared bank.</summary>
public sealed record ItemHit(string Character, string Where, string Item, int Count);

/// <summary>A scroll that someone carries and another character still needs.</summary>
public sealed record ScrollPlanRow(string Spell, string HeldBy, string NeededBy);

/// <summary>A spell that a character is missing and no scroll for is carried by anyone on the server.</summary>
public sealed record ShoppingRow(int Level, string Spell, string NeededBy);

/// <summary>Looks across all of the characters whose <c>/outputfile</c> files have been read.</summary>
public static class CharacterPlanner
{
    private const string ScrollPrefix = "Spell: ";

    /// <summary>
    /// Every item whose name contains <paramref name="text"/> (case-insensitive) on any character, plus the shared banks.
    /// Items are listed once per character, shared bank items once per server (as character "Shared bank (server)").
    /// </summary>
    public static IReadOnlyList<ItemHit> FindItems(
        IEnumerable<CharacterData> characters, IEnumerable<SharedBankData> sharedBanks, string text)
    {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(sharedBanks);

        string needle = (text ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            return Array.Empty<ItemHit>();
        }

        var hits = new List<ItemHit>();
        foreach (CharacterData character in characters.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (InventoryItem item in character.Inventory.Where(i => i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(new ItemHit(character.Name, item.Where, item.Name, item.Count));
            }
        }

        foreach (SharedBankData bank in sharedBanks.OrderBy(b => b.Server, StringComparer.OrdinalIgnoreCase))
        {
            foreach (InventoryItem item in bank.Items.Where(i => i.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                hits.Add(new ItemHit($"Shared bank ({bank.Server})", item.Where, item.Name, item.Count));
            }
        }

        return hits;
    }

    /// <summary>
    /// For one server: which scrolls are carried by a character other than the one that needs them, and which missing spells have no
    /// scroll anywhere on the server (the shopping list). A character's own scroll counts as carried by that character.
    /// </summary>
    public static (IReadOnlyList<ScrollPlanRow> Scrolls, IReadOnlyList<ShoppingRow> Shopping) Plan(
        IEnumerable<CharacterData> characters, IEnumerable<SharedBankData> sharedBanks, string server)
    {
        ArgumentNullException.ThrowIfNull(characters);
        ArgumentNullException.ThrowIfNull(sharedBanks);

        List<CharacterData> onServer = characters
            .Where(c => c.Server.Equals(server, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // spell -> who carries a scroll of it, e.g. "Ream: General 2 · slot 3"
        var holders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        void Add(string spell, string owner, InventoryItem item)
        {
            if (!holders.TryGetValue(spell, out List<string>? list))
            {
                holders[spell] = list = new List<string>();
            }

            list.Add($"{owner}: {item.Where}");
        }

        foreach (CharacterData character in onServer)
        {
            foreach (InventoryItem item in character.Inventory.Where(IsScroll))
            {
                Add(SpellOf(item), character.Name, item);
            }
        }

        SharedBankData? shared = sharedBanks.FirstOrDefault(b => b.Server.Equals(server, StringComparison.OrdinalIgnoreCase));
        if (shared is not null)
        {
            foreach (InventoryItem item in shared.Items.Where(IsScroll))
            {
                Add(SpellOf(item), "Shared bank", item);
            }
        }

        // spell -> (level, who is missing it)
        var needs = new Dictionary<string, (int Level, List<string> Who)>(StringComparer.OrdinalIgnoreCase);
        foreach (CharacterData character in onServer)
        {
            foreach (SpellListEntry missing in character.Missing)
            {
                if (!needs.TryGetValue(missing.Name, out (int Level, List<string> Who) entry))
                {
                    needs[missing.Name] = entry = (missing.Level, new List<string>());
                }

                entry.Who.Add(character.Name);
            }
        }

        var scrolls = new List<ScrollPlanRow>();
        var shopping = new List<ShoppingRow>();
        foreach (KeyValuePair<string, (int Level, List<string> Who)> need in needs
                     .OrderByDescending(p => p.Value.Level).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            string neededBy = string.Join(", ", need.Value.Who);
            if (holders.TryGetValue(need.Key, out List<string>? held))
            {
                scrolls.Add(new ScrollPlanRow(need.Key, string.Join("; ", held), neededBy));
            }
            else
            {
                shopping.Add(new ShoppingRow(need.Value.Level, need.Key, neededBy));
            }
        }

        return (scrolls, shopping);
    }

    private static bool IsScroll(InventoryItem item) =>
        item.Name.StartsWith(ScrollPrefix, StringComparison.OrdinalIgnoreCase) && item.Name.Length > ScrollPrefix.Length;

    private static string SpellOf(InventoryItem item) => item.Name[ScrollPrefix.Length..].Trim();
}
