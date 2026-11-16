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

/// <summary>A skill on the Skills tab: its value, the most it can be for the class and level, and today's skill-ups.</summary>
public sealed record SkillRowView(string Skill, int Value, string Max, string Today, string LastUp, bool IsMaxed);

public sealed record TradeskillRowView(string Recipe, int Combines, int Made, int Failed, string Success, string Last);

public sealed record FactionRowView(string Faction, string Session, string AllTime, string Hits, string UsualHit, string ComesFrom, string Status);

/// <summary>One entry of the Persona drop-down: the class name ("Cleric", empty when not known) and what the drop-down shows.</summary>
public sealed record PersonaChoice(string Persona, string Label, bool IsCurrent);

/// <summary>
/// Where the per-persona tabs get their rows (read again each time the window refreshes): the personas, skills and faction of a
/// persona, the character's tradeskills, and a persona's Focus Effects choices.
/// </summary>
public sealed record CharacterProgressSource(
    Func<string, IReadOnlyList<SkillRowView>> Skills,
    Func<IReadOnlyList<TradeskillRowView>> Tradeskills,
    Func<string, IReadOnlyList<FactionRowView>> Factions,
    Func<IReadOnlyList<PersonaChoice>>? Personas = null,
    Func<string, FocusEffectsViewModel>? FocusFor = null);

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
    public const int FocusTab = 4;
    public const int SkillsTab = 5;
    public const int TradeskillsTab = 6;
    public const int FactionTab = 7;

    private const string ScrollPrefix = "Spell: ";
    private const string None = "—";

    private readonly CharacterDataStore _store;
    private readonly Func<string?> _eqFolder;
    private readonly Func<string> _classLabel;
    private readonly CharacterProgressSource? _progress;
    private readonly Action? _forget;

    public CharacterDataViewModel(
        CharacterDataStore store, Func<string?> eqFolder, string characterName, string serverName, Func<string> classLabel, int tab,
        FocusEffectsViewModel? focus = null, CharacterProgressSource? progress = null, Action? forget = null)
    {
        _focus = focus;
        _progress = progress;
        _forget = forget;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _eqFolder = eqFolder ?? throw new ArgumentNullException(nameof(eqFolder));
        _classLabel = classLabel ?? throw new ArgumentNullException(nameof(classLabel));
        CharacterName = characterName;
        ServerName = serverName ?? string.Empty;
        _selectedTabIndex = tab;
        LoadPersonas();
        Rebuild();
    }

    public string CharacterName { get; }

    /// <summary>The Focus Effects tab's data for the chosen persona; null only in tests.</summary>
    [ObservableProperty]
    private FocusEffectsViewModel? _focus;

    /// <summary>The character's class personas, current one marked. Empty when the logs have not named one.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PersonaChoice> PersonaChoices { get; } = new();

    public bool HasPersonas => PersonaChoices.Count > 0;

    /// <summary>The persona the Spellbook, Missing Spells, Focus Effects, Skills and Faction tabs show.</summary>
    [ObservableProperty]
    private PersonaChoice? _selectedPersona;

    private string PersonaName => SelectedPersona?.Persona ?? string.Empty;

    partial void OnSelectedPersonaChanged(PersonaChoice? value)
    {
        if (_progress?.FocusFor is { } focusFor && value is not null)
        {
            Focus = focusFor(value.Persona);
        }

        Rebuild();
    }

    /// <summary>Fills the Persona drop-down, keeping the choice (or picking the current persona).</summary>
    private void LoadPersonas()
    {
        if (_progress?.Personas is null)
        {
            return;
        }

        string? keep = SelectedPersona?.Persona;
        IReadOnlyList<PersonaChoice> choices = _progress.Personas();
        PersonaChoices.Clear();
        foreach (PersonaChoice choice in choices)
        {
            PersonaChoices.Add(choice);
        }

        OnPropertyChanged(nameof(HasPersonas));
        PersonaChoice? pick = choices.FirstOrDefault(c => c.Persona == keep) ?? choices.FirstOrDefault(c => c.IsCurrent) ?? choices.FirstOrDefault();
        if (!Equals(pick, SelectedPersona))
        {
            SelectedPersona = pick;
        }
    }

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

    /// <summary>The Inventory tab's picture of the worn gear, bags, bank and shared bank.</summary>
    public InventoryPanelViewModel InventoryPanel { get; } = new();

    /// <summary>Why the chosen persona has no worn gear, or empty.</summary>
    [ObservableProperty]
    private string _inventoryNote = string.Empty;

    /// <summary>Why the Spellbook tab is empty for the chosen persona, or empty.</summary>
    [ObservableProperty]
    private string _spellbookNote = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<SkillRowView> _skillRows = Array.Empty<SkillRowView>();

    [ObservableProperty]
    private IReadOnlyList<TradeskillRowView> _tradeskillRows = Array.Empty<TradeskillRowView>();

    [ObservableProperty]
    private IReadOnlyList<FactionRowView> _factionRows = Array.Empty<FactionRowView>();

    /// <summary>Hides skills already at their most for the character's class and level.</summary>
    [ObservableProperty]
    private bool _onlyUnmaxedSkills;

    public bool CanForget => _forget is not null;

    partial void OnFilterTextChanged(string value) => Rebuild();

    partial void OnOnlyUnmaxedSkillsChanged(bool value) => Rebuild();

    // The log-based tabs change as the character plays, so they are read again whenever a tab is picked.
    partial void OnSelectedTabIndexChanged(int value) => Rebuild();

    /// <summary>Forgets the character's saved /outputfile data (after asking), then refreshes the window.</summary>
    [RelayCommand]
    private void Forget()
    {
        _forget?.Invoke();
        Rebuild();
    }

    partial void OnOnlyWithScrollChanged(bool value) => Rebuild();

    /// <summary>Reads this character's files from the game folder again and refreshes the tables.</summary>
    [RelayCommand]
    public void Refresh()
    {
        _store.ScanAll(_eqFolder()); // this character's files, and the other characters' (their scrolls are listed too)
        LoadPersonas();
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
            ? $"{CharacterName} · Inventory / Profile"
            : $"{CharacterName} ({string.Join(", ", details)}) · Inventory / Profile";

        SourceText = data is null
            ? "No /outputfile data has been read yet."
            : $"Inventory {Describe(data.InventoryUtc)} · Spellbook {Describe(data.SpellbookUtc)} · Missing spells {Describe(data.MissingUtc)}";

        // Worn gear is each persona's own; bags, bank and shared bank are the same for every persona.
        string code = EQOverwatch.Core.Roster.EqClass.Abbreviate(PersonaName);
        bool byPersona = data is not null && code.Length > 0 && data.Worn.Count > 0;
        PersonaWorn? worn = byPersona ? data!.WornFor(code) : null;
        List<InventoryItem> layout = data is null ? new List<InventoryItem>()
            : byPersona ? (worn?.Rows ?? new List<InventoryItem>()).Concat(data.UnwornLayout).ToList()
            : data.InventoryLayout;
        InventoryNote = byPersona && worn is null
            ? $"No worn gear for the {PersonaName} persona yet: type /outputfile inventory in game while it is on. Bags and bank are the same for every persona."
            : string.Empty;

        var items = new List<InventoryItem>();
        if (data is not null)
        {
            items.AddRange(byPersona
                ? (worn?.Rows ?? new List<InventoryItem>()).Where(i => !i.Name.Equals("Empty", StringComparison.OrdinalIgnoreCase))
                    .Concat(data.Inventory.Where(i => !InventoryLayout.IsWornRow(i.Location)))
                : data.Inventory);
        }

        if (shared is not null)
        {
            items.AddRange(shared.Items);
        }

        InventoryPanel.Load(layout, byPersona ? $"Worn ({PersonaName})" : "Worn");
        InventoryRows = items
            .Where(i => Match(i.Name) || Match(i.Where))
            .Select(i => new InventoryRow(i.Where, i.Name, i.ItemId, i.Count))
            .ToList();

        KeyRingRows = (data?.KeyRing ?? new List<KeyRingEntry>())
            .Where(k => Match(k.Name) || Match(k.Type))
            .Select(k => new KeyRingRow(k.Type, k.Name, k.ItemId))
            .ToList();

        // A persona's own lists come from the files with its class code; with no personas known, the newest lists are shown.
        PersonaBooks? books = data is null ? null
            : code.Length > 0 ? data.Books(code)
            : new PersonaBooks { SpellbookUtc = data.SpellbookUtc, Spellbook = data.Spellbook, MissingUtc = data.MissingUtc, Missing = data.Missing };
        SpellbookRows = (books?.Spellbook ?? new List<SpellListEntry>())
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

        List<SpellListEntry> missing = books?.Missing ?? new List<SpellListEntry>();
        int withScroll = missing.Count(m => scrolls.ContainsKey(m.Name));
        MissingRows = missing
            .Where(m => Match(m.Name))
            .OrderByDescending(m => m.Level)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new MissingSpellRow(m.Level, m.Name, scrolls.GetValueOrDefault(m.Name) ?? None))
            .Where(m => !OnlyWithScroll || m.Scroll != None)
            .ToList();

        if (_progress is not null)
        {
            SkillRows = _progress.Skills(PersonaName).Where(r => Match(r.Skill) && (!OnlyUnmaxedSkills || !r.IsMaxed)).ToList();
            TradeskillRows = _progress.Tradeskills().Where(r => Match(r.Recipe)).ToList();
            FactionRows = _progress.Factions(PersonaName).Where(r => Match(r.Faction) || Match(r.ComesFrom)).ToList();
        }

        MissingSummary = books?.MissingUtc is null
            ? (code.Length > 0
                ? $"No missing spells for the {PersonaName} persona yet: type /outputfile missingspells in game while it is on."
                : "Missing spells have not been read yet.")
            : $"Scribed: {books.Spellbook.Count:N0} · Missing: {missing.Count:N0} · Scrolls on hand for missing spells: {withScroll:N0}";
        SpellbookNote = books?.SpellbookUtc is null && code.Length > 0
            ? $"No spellbook for the {PersonaName} persona yet: type /outputfile spellbook in game while it is on."
            : string.Empty;
    }
}
