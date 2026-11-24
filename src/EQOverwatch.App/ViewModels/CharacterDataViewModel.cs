using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Characters;

namespace EQOverwatch.App.ViewModels;

public sealed record InventoryRow(string Where, string Item, int ItemId, int Count);

public sealed record SpellRow(int Level, string Name);

/// <summary>A spell the character has not scribed, with the scroll for it when one is in the bags or banks.</summary>
public sealed record MissingSpellRow(int Level, string Name, string Scroll);

/// <summary>A key ring row; <paramref name="ItemId"/> is empty for Special keys, which come from achievements and the log.</summary>
public sealed record KeyRingRow(string Type, string Name, string ItemId);

/// <summary>An achievement on the Achievements tab.</summary>
public sealed record AchievementRowView(string Category, string Achievement, string Status, string Steps, bool IsDone);

/// <summary>A skill on the Skills tab: its value, the most it can be for the class and level, and today's skill-ups.</summary>
public sealed record SkillRowView(string Skill, int Value, string Max, string Today, string LastUp, bool IsMaxed);

/// <param name="SkillUps">Skill-ups from this recipe; <paramref name="PerSkillUp"/> is combines per skill-up ("4.5"), or a dash.</param>
public sealed record TradeskillRowView(string Recipe, int Combines, int Made, int Failed, string Success, string Last, int SkillUps = 0, string PerSkillUp = "—");

/// <summary>A character flag on the Flags tab: the log's words and when it came.</summary>
public sealed record FlagRowView(string Flag, string Received);

/// <summary>A row of the AAs tab: an alternate ability, its rank, points spent, when it was learned and where the row comes from.</summary>
public sealed record AaRowView(string Name, int Rank, string Points, string Learned, string Source, string Description)
{
    public bool IsYours => Source != EQOverwatch.Core.Progress.AaProgress.FromLog;
}

/// <summary>Reads and changes a persona's alternate abilities for the AAs tab.</summary>
public sealed record AaSource(
    Func<string, IReadOnlyList<AaRowView>> Rows,
    Action<string, string, int> Set,
    Action<string, string> Remove,
    Func<IReadOnlyList<string>> Names);

/// <summary>A row of the Challenges tab: a challenge of the character's server and how this character stands on it.</summary>
public sealed record ChallengeRowView(int Level, string Goal, string Reward, string Status, bool IsDone);

/// <param name="Standing">From the persona's faction file plus the log's changes since it, e.g. "1,250 (Ally)"; empty without a file.</param>
public sealed record FactionRowView(string Faction, string Session, string AllTime, string Hits, string UsualHit, string ComesFrom, string Status, string Standing = "");

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
    Func<string, FocusEffectsViewModel>? FocusFor = null,
    Func<IReadOnlyList<FlagRowView>>? Flags = null,
    Func<IReadOnlyList<string>>? KeyRingAdds = null,
    Func<IReadOnlyList<string>>? AchievementsDone = null,
    Func<string>? Account = null,
    Action<string>? SetAccount = null,
    Func<IReadOnlyList<string>>? AccountNames = null,
    Func<IReadOnlyList<EQOverwatch.Core.Leveling.LevelEvent>>? LevelHistory = null,
    Func<IReadOnlyList<ChallengeRowView>>? Challenges = null,
    AaSource? Aas = null);

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
    public const int FlagsTab = 8;
    public const int AchievementsTab = 9;

    /// <summary>The key ring type shown for keys that come from achievements and the log rather than the inventory file.</summary>
    public const string SpecialKeyType = "Special";

    /// <summary>The achievements file's category for keys.</summary>
    public const string KeysCategory = "General: Keys";

    public const string AllCategories = "All categories";

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

    /// <summary>True when the window can name the character's account (not in tests).</summary>
    public bool HasAccounts => _progress?.SetAccount is not null;

    /// <summary>The levels the character reached, from its log (for the level history chart).</summary>
    public IReadOnlyList<EQOverwatch.Core.Leveling.LevelEvent> LevelHistory =>
        _progress?.LevelHistory?.Invoke() ?? Array.Empty<EQOverwatch.Core.Leveling.LevelEvent>();

    public bool HasLevelHistory => LevelHistory.Count >= 2;

    /// <summary>The account names already used on this server, for the Account box.</summary>
    public IReadOnlyList<string> AccountNames => _progress?.AccountNames?.Invoke() ?? Array.Empty<string>();

    /// <summary>The account name typed for this character; empty when none. Saved when the box loses focus.</summary>
    public string AccountName
    {
        get => _progress?.Account?.Invoke() ?? string.Empty;
        set
        {
            string name = (value ?? string.Empty).Trim();
            if (_progress?.SetAccount is null || name == AccountName)
            {
                return;
            }

            _progress.SetAccount(name);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccountNames));
            Rebuild();
        }
    }

    /// <summary>"Same account: Ream, Reamii, Reamiiv", or how to link characters when the account has only this one.</summary>
    [ObservableProperty]
    private string _accountMembers = string.Empty;

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

    [ObservableProperty]
    private IReadOnlyList<FlagRowView> _flagRows = Array.Empty<FlagRowView>();

    /// <summary>The server's challenge achievements and this character's standing (Frostreaver and Vaniki characters only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChallenges))]
    private IReadOnlyList<ChallengeRowView> _challengeRows = Array.Empty<ChallengeRowView>();

    public bool HasChallenges => ChallengeRows.Count > 0;

    /// <summary>The persona's alternate abilities, A to Z.</summary>
    [ObservableProperty]
    private IReadOnlyList<AaRowView> _aaRows = Array.Empty<AaRowView>();

    [ObservableProperty]
    private string _aaSummary = string.Empty;

    [ObservableProperty]
    private AaRowView? _selectedAa;

    /// <summary>The ability being added or changed, and its rank.</summary>
    [ObservableProperty]
    private string _aaName = string.Empty;

    [ObservableProperty]
    private string _aaRank = "1";

    [ObservableProperty]
    private string _aaStatus = string.Empty;

    private IReadOnlyList<string>? _aaNames;

    /// <summary>Every ability name in the game files, for the name box.</summary>
    public IReadOnlyList<string> AaNameChoices => _aaNames ??= _progress?.Aas?.Names() ?? Array.Empty<string>();

    partial void OnSelectedAaChanged(AaRowView? value)
    {
        if (value is not null)
        {
            AaName = value.Name;
            AaRank = value.Rank.ToString(System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Adds the ability or changes its rank; the log takes over again when it shows the ability learned later.</summary>
    [RelayCommand]
    private void SaveAa()
    {
        if (_progress?.Aas is not { } aas || AaName.Trim().Length == 0)
        {
            AaStatus = "Type or pick an ability name first.";
            return;
        }

        if (!int.TryParse(AaRank, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out int rank) || rank < 1)
        {
            AaStatus = "The rank must be 1 or more.";
            return;
        }

        aas.Set(PersonaName, AaName.Trim(), rank);
        AaStatus = $"Saved {AaName.Trim()} at rank {rank}.";
        Reload();
    }

    /// <summary>Hides the selected ability until the log shows it learned again.</summary>
    [RelayCommand]
    private void RemoveAa()
    {
        if (_progress?.Aas is not { } aas || SelectedAa is not { } row)
        {
            AaStatus = "Pick an ability in the list first.";
            return;
        }

        aas.Remove(PersonaName, row.Name);
        AaStatus = $"Removed {row.Name}.";
        Reload();
    }

    [ObservableProperty]
    private IReadOnlyList<AchievementRowView> _achievementRows = Array.Empty<AchievementRowView>();

    [ObservableProperty]
    private IReadOnlyList<string> _achievementCategories = new[] { AllCategories };

    [ObservableProperty]
    private string _achievementCategory = AllCategories;

    [ObservableProperty]
    private bool _onlyCompletedAchievements;

    /// <summary>"678 of 5,231 completed", or how to get the file.</summary>
    [ObservableProperty]
    private string _achievementSummary = string.Empty;

    partial void OnAchievementCategoryChanged(string value) => Rebuild();

    partial void OnOnlyCompletedAchievementsChanged(bool value) => Rebuild();

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

    /// <summary>Shows what is stored now, without reading the game folder (a file was just read for this character).</summary>
    public void Reload()
    {
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
        SharedBankData? shared = server.Length > 0 ? _store.FindShared(CharacterName, server) : null;
        List<string> others = shared?.Characters.Where(c => !c.Equals(CharacterName, StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<string>();
        AccountMembers = others.Count > 0
            ? $"Same account: {string.Join(", ", others)}"
            : "Give characters on one account the same name to link their shared bank.";
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

        // Achievements the log has completed since the achievements file was written count as done.
        var doneInLog = new HashSet<string>(_progress?.AchievementsDone?.Invoke() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        List<AchievementEntry> achievements = (data?.Achievements ?? new List<AchievementEntry>())
            .Select(a => !a.IsDone && doneInLog.Contains(a.Name) ? a with { Status = "C", StepsDone = a.Steps } : a)
            .ToList();

        // Special keys: the achievements file's completed keys and the keys the log says were added, each once.
        var keyRing = (data?.KeyRing ?? new List<KeyRingEntry>())
            .Select(k => new KeyRingRow(k.Type, k.Name, k.ItemId.ToString(CultureInfo.InvariantCulture)))
            .ToList();
        var seen = new HashSet<string>(keyRing.Select(k => k.Name), StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> specials = achievements.Where(a => a.IsDone && a.Category.Equals(KeysCategory, StringComparison.OrdinalIgnoreCase)).Select(a => a.Name)
            .Concat(_progress?.KeyRingAdds?.Invoke() ?? Array.Empty<string>());
        foreach (string name in specials.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(name))
            {
                keyRing.Add(new KeyRingRow(SpecialKeyType, name, string.Empty));
            }
        }

        KeyRingRows = keyRing.Where(k => Match(k.Name) || Match(k.Type)).ToList();

        var categories = achievements.Select(a => a.Category).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        categories.Insert(0, AllCategories);
        if (!categories.SequenceEqual(AchievementCategories))
        {
            string keep = AchievementCategory;
            AchievementCategories = categories;
            if (!categories.Contains(keep))
            {
                AchievementCategory = AllCategories; // rebuilds again
                return;
            }
        }

        bool allCategories = AchievementCategory is null or AllCategories;
        AchievementRows = achievements
            .Where(a => (allCategories || a.Category.Equals(AchievementCategory, StringComparison.OrdinalIgnoreCase))
                        && (!OnlyCompletedAchievements || a.IsDone)
                        && (Match(a.Name) || Match(a.Category)))
            .Select(a => new AchievementRowView(
                a.Category,
                a.Name,
                a.Status switch { "C" => "Done", "L" => "Locked", _ => "Not done" },
                a.Steps == 0 ? string.Empty : $"{a.StepsDone}/{a.Steps}",
                a.IsDone))
            .ToList();
        int distinct = achievements.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int done = achievements.Where(a => a.IsDone).Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        AchievementSummary = data?.AchievementsUtc is null
            ? "No achievements read yet: type /output achievements in game."
            : $"{done:N0} of {distinct:N0} completed · file {Describe(data.AchievementsUtc)}";

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
            FlagRows = (_progress.Flags?.Invoke() ?? Array.Empty<FlagRowView>()).Where(r => Match(r.Flag)).ToList();
            ChallengeRows = (_progress.Challenges?.Invoke() ?? Array.Empty<ChallengeRowView>()).Where(r => Match(r.Goal) || Match(r.Reward)).ToList();
            IReadOnlyList<AaRowView> aas = _progress.Aas?.Rows(PersonaName) ?? Array.Empty<AaRowView>();
            AaRows = aas.Where(r => Match(r.Name)).ToList();
            int points = aas.Sum(r => int.TryParse(r.Points, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out int p) ? p : 0);
            AaSummary = aas.Count == 0
                ? "No alternate abilities yet: they come from the log's \"You have gained the ability\" and \"You have improved\" lines, or add them here."
                : $"{aas.Count:N0} abilities · {points:N0} points spent (from the logs)";
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
