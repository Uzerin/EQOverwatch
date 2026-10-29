using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Characters;

namespace EQOverwatch.App.ViewModels;

public sealed record InventoryRow(string Where, string Item, int ItemId, int Count);

public sealed record SpellRow(int Level, string Name);

/// <summary>A spell the character has not scribed, with the scroll for it when one is in the bags or banks.</summary>
public sealed record MissingSpellRow(int Level, string Name, string Scroll);

public sealed record KeyRingRow(string Type, string Name, int ItemId);

/// <summary>
/// One character's data window: inventory, spellbook, missing spells and key ring, read from the character's <c>/outputfile</c> files.
/// The filter box narrows whichever table is showing. <see cref="Refresh"/> reads the game folder again.
/// </summary>
public sealed partial class CharacterDataViewModel : ObservableObject
{
    public const int InventoryTab = 0;
    public const int SpellbookTab = 1;
    public const int MissingSpellsTab = 2;
    public const int KeyRingTab = 3;

    private const string ScrollPrefix = "Spell: ";
    private const string None = "—";

    private readonly CharacterDataStore _store;
    private readonly Func<string?> _eqFolder;
    private readonly Func<string> _classLabel;

    public CharacterDataViewModel(
        CharacterDataStore store, Func<string?> eqFolder, string characterName, string serverName, Func<string> classLabel, int tab)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _eqFolder = eqFolder ?? throw new ArgumentNullException(nameof(eqFolder));
        _classLabel = classLabel ?? throw new ArgumentNullException(nameof(classLabel));
        CharacterName = characterName;
        ServerName = serverName ?? string.Empty;
        _selectedTabIndex = tab;
        Rebuild();
    }

    public string CharacterName { get; }

    public string ServerName { get; }

    /// <summary>Identifies the window so a second click on the same character reuses it.</summary>
    public string Key => $"{CharacterName}_{ServerName}";

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _onlyWithScroll;

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>When each file was written, e.g. "Inventory Oct 28 18:12 · Spellbook Oct 28 18:12 · Missing spells not read yet".</summary>
    [ObservableProperty]
    private string _sourceText = string.Empty;

    /// <summary>"Scribed: 168 · Missing: 44 · Scrolls on hand for missing spells: 3".</summary>
    [ObservableProperty]
    private string _missingSummary = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<InventoryRow> _inventoryRows = Array.Empty<InventoryRow>();

    [ObservableProperty]
    private IReadOnlyList<SpellRow> _spellbookRows = Array.Empty<SpellRow>();

    [ObservableProperty]
    private IReadOnlyList<MissingSpellRow> _missingRows = Array.Empty<MissingSpellRow>();

    [ObservableProperty]
    private IReadOnlyList<KeyRingRow> _keyRingRows = Array.Empty<KeyRingRow>();

    partial void OnFilterTextChanged(string value) => Rebuild();

    partial void OnOnlyWithScrollChanged(bool value) => Rebuild();

    /// <summary>Reads this character's files from the game folder again and refreshes the tables.</summary>
    [RelayCommand]
    public void Refresh()
    {
        _store.ScanAll(_eqFolder()); // this character's files, and the other characters' (their scrolls are listed too)
        Rebuild();
    }

    /// <summary>"Oct 28 18:12" in the PC's local time, or "not read yet".</summary>
    public static string Describe(DateTime? utc) =>
        utc is { } value ? value.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture) : "not read yet";

    private void Rebuild()
    {
        CharacterData? data = _store.Find(CharacterName, ServerName);
        string server = data?.Server ?? ServerName;
        SharedBankData? shared = server.Length > 0 ? _store.FindShared(server) : null;
        string filter = FilterText?.Trim() ?? string.Empty;

        bool Match(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        string className = _classLabel();
        if (string.IsNullOrEmpty(className) && data is not null)
        {
            className = data.ClassCode;
        }

        var details = new List<string>();
        if (!string.IsNullOrEmpty(className))
        {
            details.Add(className);
        }

        if (server.Length > 0)
        {
            details.Add(server);
        }

        Title = details.Count == 0
            ? $"{CharacterName} · Character data"
            : $"{CharacterName} ({string.Join(", ", details)}) · Character data";

        SourceText = data is null
            ? "No /outputfile data has been read yet."
            : $"Inventory {Describe(data.InventoryUtc)} · Spellbook {Describe(data.SpellbookUtc)} · Missing spells {Describe(data.MissingUtc)}";

        var items = new List<InventoryItem>();
        if (data is not null)
        {
            items.AddRange(data.Inventory);
        }

        if (shared is not null)
        {
            items.AddRange(shared.Items);
        }

        InventoryRows = items
            .Where(i => Match(i.Name) || Match(i.Where))
            .Select(i => new InventoryRow(i.Where, i.Name, i.ItemId, i.Count))
            .ToList();

        KeyRingRows = (data?.KeyRing ?? new List<KeyRingEntry>())
            .Where(k => Match(k.Name) || Match(k.Type))
            .Select(k => new KeyRingRow(k.Type, k.Name, k.ItemId))
            .ToList();

        SpellbookRows = (data?.Spellbook ?? new List<SpellListEntry>())
            .Where(s => Match(s.Name))
            .OrderByDescending(s => s.Level) // highest level first, then by name
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SpellRow(s.Level, s.Name))
            .ToList();

        // A scroll in a bag or bank is named "Spell: <spell>". This character's own (and the shared bank's) come first, then the
        // other characters' on the same server with their name in front, e.g. "Ream: Spell: Sanctuary · General 2 · slot 3".
        // Scrolls on another server cannot be traded over, so they are not listed.
        var holders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        void AddScroll(InventoryItem item, string owner)
        {
            if (!item.Name.StartsWith(ScrollPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string spell = item.Name[ScrollPrefix.Length..].Trim();
            if (spell.Length == 0)
            {
                return;
            }

            if (!holders.TryGetValue(spell, out List<string>? list))
            {
                holders[spell] = list = new List<string>();
            }

            list.Add($"{owner}{item.Name} · {item.Where}");
        }

        foreach (InventoryItem item in items)
        {
            AddScroll(item, string.Empty);
        }

        foreach (CharacterData other in _store.All()
                     .Where(c => !c.Name.Equals(CharacterName, StringComparison.OrdinalIgnoreCase)
                                 && (server.Length == 0 || c.Server.Equals(server, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (InventoryItem item in other.Inventory)
            {
                AddScroll(item, $"{other.Name}: ");
            }
        }

        var scrolls = holders.ToDictionary(p => p.Key, p => string.Join("; ", p.Value), StringComparer.OrdinalIgnoreCase);

        List<SpellListEntry> missing = data?.Missing ?? new List<SpellListEntry>();
        int withScroll = missing.Count(m => scrolls.ContainsKey(m.Name));
        MissingRows = missing
            .Where(m => Match(m.Name))
            .OrderByDescending(m => m.Level)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new MissingSpellRow(m.Level, m.Name, scrolls.GetValueOrDefault(m.Name) ?? None))
            .Where(m => !OnlyWithScroll || m.Scroll != None)
            .ToList();

        MissingSummary = data is null || !data.Has(OutputFileKind.MissingSpells)
            ? "Missing spells have not been read yet."
            : $"Scribed: {data.Spellbook.Count:N0} · Missing: {missing.Count:N0} · Scrolls on hand for missing spells: {withScroll:N0}";
    }
}
