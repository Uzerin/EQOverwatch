using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQOverwatch.Core.Spells;

/// <summary>A focus effect an item carries: the focus spell's id and name.</summary>
public sealed record ItemFocusRef(int SpellId, string SpellName);

/// <summary>An item and the focus effects it carries, as looked up by name or id.</summary>
public sealed record LookedUpItem(int Id, string Name, IReadOnlyList<ItemFocusRef> Foci);

/// <summary>Finds which focus effects an item carries.</summary>
public interface IItemFocusLookup
{
    /// <summary>The items with that id (when above 0) or exact name; empty when there is none. Throws when the lookup cannot be made.</summary>
    Task<IReadOnlyList<LookedUpItem>> LookupAsync(string name, int id, CancellationToken cancellation = default);
}

/// <summary>Asks the EQ Overwatch server, which looks each item up once for every player and remembers it.</summary>
public sealed class HttpItemFocusLookup : IItemFocusLookup
{
    private readonly HttpClient _http;
    private readonly Uri _base;

    public HttpItemFocusLookup(HttpClient http, string address = Drops.HttpSharedDropsService.DefaultAddress)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _base = new Uri(address.EndsWith('/') ? address : address + "/");
    }

    public async Task<IReadOnlyList<LookedUpItem>> LookupAsync(string name, int id, CancellationToken cancellation = default)
    {
        string query = id > 0 ? "id=" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "name=" + Uri.EscapeDataString(name.Trim());
        using HttpResponseMessage response = await _http.GetAsync(new Uri(_base, "v1/itemfocus?" + query), cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            return Array.Empty<LookedUpItem>(); // a name the server does not accept is never an item
        }

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>Reads <c>{ items: [{ id, name, foci: [{ s, n }] }] }</c>.</summary>
    public static IReadOnlyList<LookedUpItem> Parse(string json)
    {
        var result = new List<LookedUpItem>();
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            var foci = new List<ItemFocusRef>();
            if (item.TryGetProperty("foci", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement focus in list.EnumerateArray())
                {
                    foci.Add(new ItemFocusRef(focus.GetProperty("s").GetInt32(), focus.TryGetProperty("n", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty));
                }
            }

            result.Add(new LookedUpItem(item.GetProperty("id").GetInt32(), item.GetProperty("name").GetString() ?? string.Empty, foci));
        }

        return result;
    }
}

/// <summary>Items already looked up (found or not), kept in a file so each item is asked for once.</summary>
public sealed class ItemFocusCache
{
    private readonly object _gate = new();
    private Dictionary<string, List<LookedUpItem>> _items = new(StringComparer.OrdinalIgnoreCase);
    private string? _path;

    public static string KeyFor(string name, int id) => id > 0 ? "id:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "name:" + name.Trim();

    public bool TryGet(string name, int id, out IReadOnlyList<LookedUpItem> items)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(KeyFor(name, id), out List<LookedUpItem>? found))
            {
                items = found;
                return true;
            }
        }

        items = Array.Empty<LookedUpItem>();
        return false;
    }

    public void Set(string name, int id, IReadOnlyList<LookedUpItem> items)
    {
        lock (_gate)
        {
            _items[KeyFor(name, id)] = items.ToList();
        }
    }

    public void Load(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                Dictionary<string, List<StoredItem>>? stored = JsonSerializer.Deserialize<Dictionary<string, List<StoredItem>>>(File.ReadAllText(path));
                lock (_gate)
                {
                    _items = (stored ?? new())
                        .ToDictionary(p => p.Key, p => p.Value.Select(s => new LookedUpItem(s.Id, s.Name, s.Foci.Select(f => new ItemFocusRef(f.S, f.N)).ToList())).ToList(),
                            StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable file only means the items are looked up again.
        }
    }

    public void Save()
    {
        if (_path is null)
        {
            return;
        }

        Dictionary<string, List<StoredItem>> stored;
        lock (_gate)
        {
            stored = _items.ToDictionary(p => p.Key, p => p.Value.Select(i => new StoredItem(i.Id, i.Name, i.Foci.Select(f => new StoredFocus(f.SpellId, f.SpellName)).ToList())).ToList());
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(stored));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Saved again next time.
        }
    }

    private sealed record StoredFocus([property: JsonPropertyName("s")] int S, [property: JsonPropertyName("n")] string N);

    private sealed record StoredItem(int Id, string Name, List<StoredFocus> Foci);
}

/// <summary>Turns identified items into a focus per kind.</summary>
public static class ItemFocusAssigner
{
    /// <summary>
    /// For each focus kind, the best focus the items carry (the biggest at <paramref name="level"/>) and the item it is on. Focus spells
    /// the spell file does not know as a focus are left out.
    /// </summary>
    public static IReadOnlyDictionary<FocusKind, (int SpellId, string Item)> Best(SpellDatabase database, IEnumerable<LookedUpItem> items, int level)
    {
        var best = new Dictionary<FocusKind, (int SpellId, string Item, double Size)>();
        foreach (LookedUpItem item in items)
        {
            foreach (ItemFocusRef focus in item.Foci)
            {
                foreach (FocusKind kind in FocusKinds.All)
                {
                    if (database.FindItemFocus(kind, focus.SpellId) is not { } known)
                    {
                        continue;
                    }

                    double size = known.PercentAt(level);
                    if (!best.TryGetValue(kind, out var current) || size > current.Size)
                    {
                        best[kind] = (known.SpellId, item.Name, size);
                    }
                }
            }
        }

        return best.ToDictionary(p => p.Key, p => (p.Value.SpellId, p.Value.Item));
    }
}
