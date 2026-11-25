using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Roster;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

// Foci set from items: an item whose focus fired in a character's log ("Your Meditative Blanket feels alive with power.") or that the
// character wears (its /outputfile inventory) is looked up once, and the best focus it carries for each kind becomes that character's
// focus on the Focus Effects tab, replacing a hand pick. A focus picked by hand afterwards stays until another item is identified.
public sealed partial class MainViewModel
{
    private const int MaxItemLookupsPerScan = 20;
    private static readonly TimeSpan ItemScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ItemRetryAfterFailure = TimeSpan.FromMinutes(10);

    private IItemFocusLookup? _itemLookup;
    private readonly ItemFocusCache _itemCache = new();
    private readonly HashSet<string> _itemsAsked = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextItemScan;
    private bool _itemScanRunning;

    /// <summary>Turns on item lookups; <paramref name="cachePath"/> keeps the items already looked up.</summary>
    public void ConfigureItemFocusLookup(IItemFocusLookup lookup, string? cachePath)
    {
        _itemLookup = lookup;
        if (!string.IsNullOrEmpty(cachePath))
        {
            _itemCache.Load(cachePath);
        }
    }

    /// <summary>Called by the refresh tick: looks for newly fired or worn items now and then.</summary>
    private void TickItemFoci(DateTime now)
    {
        if (_itemLookup is null || _itemScanRunning || now < _nextItemScan || _pipeline.Spells.Database.ItemFoci.Count == 0)
        {
            return;
        }

        _nextItemScan = now + ItemScanInterval;
        _ = ScanItemFociAsync();
    }

    /// <summary>Looks up the items not known yet, then sets each character's foci from its identified items. Returns when done.</summary>
    public async Task ScanItemFociAsync()
    {
        if (_itemLookup is null || _itemScanRunning)
        {
            return;
        }

        _itemScanRunning = true;
        try
        {
            int asked = 0;
            bool cacheChanged = false;
            foreach (WatchedLogConfig log in Logs.ToList())
            {
                var identified = new List<LookedUpItem>();
                foreach ((string name, int id) in ItemsToIdentify(log.CharacterName, log.ServerName))
                {
                    if (!_itemCache.TryGet(name, id, out IReadOnlyList<LookedUpItem> items))
                    {
                        if (asked >= MaxItemLookupsPerScan || !_itemsAsked.Add(ItemFocusCache.KeyFor(name, id)))
                        {
                            continue;
                        }

                        asked++;
                        items = await _itemLookup.LookupAsync(name, id);
                        _itemCache.Set(name, id, items);
                        cacheChanged = true;
                    }

                    identified.AddRange(items.Where(i => i.Foci.Count > 0));
                }

                ApplyItemFoci(log.CharacterName, log.ServerName, identified);
            }

            if (cacheChanged)
            {
                _itemCache.Save();
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _itemsAsked.Clear(); // asked again once the server can be reached
            _nextItemScan = _clock().ToUniversalTime() + ItemRetryAfterFailure;
        }
        finally
        {
            _itemScanRunning = false;
        }
    }

    /// <summary>The items whose focus fired in the character's log (by name) and the gear it wears in its inventory file (by id).</summary>
    private IEnumerable<(string Name, int Id)> ItemsToIdentify(string character, string server)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string item, _, _) in _pipeline.Progress.FocusFires(character, server))
        {
            if (seen.Add(item))
            {
                yield return (item, 0);
            }
        }

        CharacterData? data = _characterData.All()
            .FirstOrDefault(c => c.Name.Equals(character, StringComparison.OrdinalIgnoreCase) && c.Server.Equals(server, StringComparison.OrdinalIgnoreCase));
        if (data is null)
        {
            yield break;
        }

        string code = EqClass.TryNormalize(_pipeline.Roster.GetClass(character, server), out string className) ? EqClass.Abbreviate(className) : string.Empty;
        IEnumerable<InventoryItem> worn = data.WornFor(code)?.Rows
                                          ?? data.InventoryLayout.Where(r => InventoryLayout.IsWornRow(r.Location));
        foreach (InventoryItem item in worn)
        {
            if (item.ItemId > 0 && seen.Add(item.Name))
            {
                yield return (item.Name, item.ItemId);
            }
        }
    }

    /// <summary>Sets each focus kind from the best identified item, unless that item was applied for that kind before.</summary>
    private void ApplyItemFoci(string character, string server, IReadOnlyList<LookedUpItem> identified)
    {
        if (identified.Count == 0)
        {
            return;
        }

        string persona = EqClass.TryNormalize(_pipeline.Roster.GetClass(character, server), out string className) ? className : string.Empty;
        IReadOnlyDictionary<FocusKind, (int SpellId, string Item)> best =
            ItemFocusAssigner.Best(_pipeline.Spells.Database, identified, FocusLevelOf(character, server, persona));

        DurationProfile profile = GetDurationProfile(character, server, persona);
        var applied = new HashSet<string>(profile.AppliedItems ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var foci = new Dictionary<FocusKind, int>(profile.Foci ?? new Dictionary<FocusKind, int>());
        var fromItems = new Dictionary<FocusKind, string>(profile.FromItems ?? new Dictionary<FocusKind, string>());
        int? beneficial = profile.BeneficialFocusId;
        int? detrimental = profile.DetrimentalFocusId;
        bool changed = false;
        foreach ((FocusKind kind, (int spellId, string item)) in best)
        {
            if (!applied.Add(item + "|" + kind))
            {
                continue; // set from this item once already: a later hand pick stays
            }

            switch (kind)
            {
                case FocusKind.BuffDuration:
                    beneficial = spellId;
                    break;
                case FocusKind.DetrimentalDuration:
                    detrimental = spellId;
                    break;
                default:
                    foci[kind] = spellId;
                    break;
            }

            fromItems[kind] = item;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        SetDurationProfile(character, server, profile with
        {
            BeneficialFocusId = beneficial,
            DetrimentalFocusId = detrimental,
            Foci = foci.Count > 0 ? foci : null,
            FromItems = fromItems,
            AppliedItems = applied.ToList(),
        }, persona);

        foreach (WeakReference<CharacterDataViewModel> reference in _openProfiles)
        {
            if (reference.TryGetTarget(out CharacterDataViewModel? open)
                && open.CharacterName.Equals(character, StringComparison.OrdinalIgnoreCase) && open.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase))
            {
                open.RefreshFocus();
            }
        }
    }
}
