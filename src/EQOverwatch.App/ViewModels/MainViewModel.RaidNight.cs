using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Loot;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

// Fight details (boss abilities, healing), recommended raid triggers, loot, attendance, and the cross-character item and scroll tools.

/// <summary>One boss spell of the selected fight.</summary>
public sealed record BossAbilityDisplayRow(string Spell, int Casts, string Every, string Times);

/// <summary>One healer of the selected fight.</summary>
public sealed record HealingDisplayRow(int Rank, string Name, long Effective, double Hps, double OverhealPercent, int Heals, int MaxHeal, string TopSpell);

public sealed record LootLineRow(string Time, string Item, string Looter, string Mob, string Roll);

public sealed record LootMobRow(string Mob, string Item, int Count, string Looters);

public sealed record LootSummaryRow(string Item, int Count, string Looters, string Mobs);

public sealed record AttendanceRaidRow(RaidAttendance Record, string When, string Server, int Members);

public sealed record RosterDisplayRow(int Group, string Name, int Level, string Class, string Role);

public sealed record AttendanceTotalRow(string Name, string Class, int Present, int Raids, double Percent, string LastSeen);

public sealed record ScrollDisplayRow(string Server, string Spell, string HeldBy, string NeededBy);

public sealed record ShoppingDisplayRow(string Server, int Level, string Spell, string NeededBy);

/// <summary>A raid trigger offered by "Recommended triggers", with the numbers behind the suggestion.</summary>
public sealed partial class RecommendedTriggerViewModel : ObservableObject
{
    public RecommendedTriggerViewModel(RaidTriggerSuggestion suggestion)
    {
        Suggestion = suggestion;
    }

    public RaidTriggerSuggestion Suggestion { get; }
    public string SpellName => Suggestion.SpellName;
    public string Description =>
        $"{Suggestion.SpellName}: every {Suggestion.RecastSeconds}s · {Suggestion.Casts} casts in {Suggestion.Fights} fight{(Suggestion.Fights == 1 ? string.Empty : "s")}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool _isAdded;

    public string ButtonText => IsAdded ? "Added" : "Add";
}

public sealed partial class MainViewModel
{
    private static readonly TimeSpan AttendanceScanEvery = TimeSpan.FromSeconds(15);

    private readonly RaidRosterStore _raidRoster;
    private volatile bool _lootDirty = true;
    private bool _toolsDirty = true;
    private DateTime _lastAttendanceScan;

    /// <summary>Called once from the constructor, after the stores exist.</summary>
    private void InitializeRaidNight()
    {
        _pipeline.Loot.Changed += () => _lootDirty = true;
        _characterData.Changed += () => _toolsDirty = true;
        _raidRoster.Changed += RebuildAttendance;
    }

    /// <summary>Called from <see cref="Refresh"/> (about four times a second): rebuilds only what changed.</summary>
    private void RefreshRaidNight()
    {
        if (_lootDirty)
        {
            _lootDirty = false;
            RebuildLoot();
        }

        if (_toolsDirty)
        {
            _toolsDirty = false;
            RebuildCharacterTools();
        }

        DateTime now = _clock();
        if (now - _lastAttendanceScan >= AttendanceScanEvery)
        {
            _lastAttendanceScan = now;
            _raidRoster.ScanAll(EqFolderPath);
        }
    }

    // ───────────────────────── selected fight: boss abilities and healing ─────────────────────────

    [ObservableProperty]
    private IReadOnlyList<BossAbilityDisplayRow> _bossAbilityRows = Array.Empty<BossAbilityDisplayRow>();

    [ObservableProperty]
    private IReadOnlyList<HealingDisplayRow> _healingRows = Array.Empty<HealingDisplayRow>();

    [ObservableProperty]
    private string _bossAbilitySummary = string.Empty;

    [ObservableProperty]
    private string _healingSummary = string.Empty;

    private void UpdateEncounterDetails(EncounterSnapshot? encounter)
    {
        if (encounter is null)
        {
            BossAbilityRows = Array.Empty<BossAbilityDisplayRow>();
            HealingRows = Array.Empty<HealingDisplayRow>();
            BossAbilitySummary = string.Empty;
            HealingSummary = string.Empty;
            return;
        }

        BossAbilityDisplayRow[] abilities = BossAbilityTimeline.Build(encounter)
            .Select(a => new BossAbilityDisplayRow(a.Spell, a.Casts, a.EveryText, a.TimesText))
            .ToArray();
        if (!abilities.SequenceEqual(BossAbilityRows))
        {
            BossAbilityRows = abilities;
        }

        BossAbilitySummary = abilities.Length == 0
            ? $"No spells cast by {encounter.TargetName} were seen in this fight."
            : $"Spells {encounter.TargetName} cast, with the time since the fight began.";

        IReadOnlyList<HealingSnapshot> healers = encounter.HealingRows;
        HealingDisplayRow[] healing = healers
            .Select((h, i) => new HealingDisplayRow(i + 1, h.Name, h.Effective, h.Hps, h.OverhealPercent, h.Heals, h.MaxHeal, h.TopSpell))
            .ToArray();
        if (!healing.SequenceEqual(HealingRows))
        {
            HealingRows = healing;
        }

        long total = healers.Sum(h => h.Effective);
        HealingSummary = healers.Count == 0
            ? "No heals were seen during this fight."
            : $"Healing during this fight: {total:N0} effective.";
    }

    // ───────────────────────── recommended raid triggers ─────────────────────────

    public ObservableCollection<RecommendedTriggerViewModel> RecommendedTriggers { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecommendationPanel))]
    private string _recommendedStatus = string.Empty;

    public bool HasRecommendationPanel => !string.IsNullOrEmpty(RecommendedStatus);

    /// <summary>Looks through the fights against the chosen boss for spells cast at a steady interval.</summary>
    [RelayCommand]
    private void SuggestRaidTriggers()
    {
        RecommendedTriggers.Clear();
        string mob = RaidMobName?.Trim() ?? string.Empty;
        if (mob.Length == 0)
        {
            RecommendedStatus = "Pick a boss in the Boss (mob) box first, then ask for recommendations.";
            return;
        }

        List<EncounterSnapshot> fights = _pipeline.LiveDps.Snapshot.All.Concat(_pipeline.HistoricalDps.Snapshot.All).ToList();
        int fightCount = fights.Count(f => f.TargetName.Equals(mob, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<RaidTriggerSuggestion> found = RaidTriggerSuggester.Suggest(mob, fights);
        foreach (RaidTriggerSuggestion suggestion in found)
        {
            RecommendedTriggers.Add(new RecommendedTriggerViewModel(suggestion));
        }

        RecommendedStatus = found.Count > 0
            ? $"{found.Count} spell{(found.Count == 1 ? string.Empty : "s")} of {mob} repeat at a steady interval across {fightCount} fight{(fightCount == 1 ? string.Empty : "s")}."
            : fightCount == 0
                ? $"No fights against {mob} are loaded. Raise Historical Days to Load in Settings, or fight it first."
                : $"No spell of {mob} repeats at a steady interval in the {fightCount} loaded fight{(fightCount == 1 ? string.Empty : "s")} yet (it needs at least {RaidTriggerSuggester.MinimumGaps + 1} casts at about the same spacing).";
    }

    [RelayCommand]
    private void AddRecommendedTrigger(RecommendedTriggerViewModel? item)
    {
        if (item is null || item.IsAdded)
        {
            return;
        }

        if (AddRecommended(item.Suggestion))
        {
            item.IsAdded = true;
            RaidBuilderStatus = $"Added {item.SpellName} ({item.Suggestion.RecastSeconds}s) for {item.Suggestion.MobName}.";
        }
        else
        {
            item.IsAdded = true;
            RaidBuilderStatus = $"{item.SpellName} for {item.Suggestion.MobName} is already in the list.";
        }
    }

    [RelayCommand]
    private void AddAllRecommendedTriggers()
    {
        int added = 0;
        foreach (RecommendedTriggerViewModel item in RecommendedTriggers.Where(r => !r.IsAdded))
        {
            if (AddRecommended(item.Suggestion))
            {
                added++;
            }

            item.IsAdded = true;
        }

        RaidBuilderStatus = added == 0 ? "Nothing new to add." : $"Added {added} recommended raid trigger{(added == 1 ? string.Empty : "s")}.";
    }

    /// <summary>Adds the rule unless the same boss and spell are already listed. Returns whether it was added.</summary>
    private bool AddRecommended(RaidTriggerSuggestion suggestion)
    {
        bool exists = RaidRules.Any(r =>
            r.SpellName.Equals(suggestion.SpellName, StringComparison.OrdinalIgnoreCase)
            && r.MobText.Equals(suggestion.MobName, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
            return false;
        }

        var rule = new RaidBossTimerRule
        {
            MobName = suggestion.MobName,
            SpellName = suggestion.SpellName,
            MatchText = suggestion.MatchText,
            RecastSeconds = suggestion.RecastSeconds,
            OverlayCategory = RaidCategory,
        };
        RaidRules.Add(CreateRaidRule(rule));
        OnRaidRulesChanged();
        return true;
    }

    // ───────────────────────── loot ─────────────────────────

    public IReadOnlyList<string> LootViews { get; } = new[] { "Line by line", "Grouped by mob", "Summary" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLootLinesView))]
    [NotifyPropertyChangedFor(nameof(IsLootMobView))]
    [NotifyPropertyChangedFor(nameof(IsLootSummaryView))]
    private int _lootViewIndex;

    public bool IsLootLinesView => LootViewIndex == 0;
    public bool IsLootMobView => LootViewIndex == 1;
    public bool IsLootSummaryView => LootViewIndex == 2;

    [ObservableProperty]
    private string _lootFilter = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<LootLineRow> _lootLineRows = Array.Empty<LootLineRow>();

    [ObservableProperty]
    private IReadOnlyList<LootMobRow> _lootMobRows = Array.Empty<LootMobRow>();

    [ObservableProperty]
    private IReadOnlyList<LootSummaryRow> _lootSummaryRows = Array.Empty<LootSummaryRow>();

    [ObservableProperty]
    private string _lootStatus = "No loot seen yet. It is collected from every watched log that has triggers turned on.";

    partial void OnLootFilterChanged(string value) => RebuildLoot();

    private void RebuildLoot()
    {
        IReadOnlyList<LootEntry> all = _pipeline.Loot.Entries;
        string filter = LootFilter?.Trim() ?? string.Empty;
        List<LootEntry> entries = filter.Length == 0
            ? all.ToList()
            : all.Where(e => e.Item.Contains(filter, StringComparison.OrdinalIgnoreCase)
                             || e.Looter.Contains(filter, StringComparison.OrdinalIgnoreCase)
                             || e.Mob.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        LootLineRows = entries
            .OrderByDescending(e => e.Time)
            .Select(e => new LootLineRow(
                e.Time.ToString("MMM d HH:mm:ss", CultureInfo.CurrentCulture), e.Item, e.Looter, e.Mob,
                e.Roll > 0 ? $"{e.RollType} {e.Roll}" : string.Empty))
            .ToList();

        LootMobRows = entries
            .GroupBy(e => e.Mob, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(e => e.Time))
            .SelectMany(mob => mob
                .GroupBy(e => e.Item, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => new LootMobRow(mob.Key, item.Key, item.Count(), DescribeLooters(item))))
            .ToList();

        LootSummaryRows = entries
            .GroupBy(e => e.Item, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LootSummaryRow(
                g.Key, g.Count(), DescribeLooters(g),
                string.Join(", ", g.Select(e => e.Mob).Distinct(StringComparer.OrdinalIgnoreCase))))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Item, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LootStatus = all.Count == 0
            ? "No loot seen yet. It is collected from every watched log that has triggers turned on."
            : $"{entries.Count:N0} of {all.Count:N0} item{(all.Count == 1 ? string.Empty : "s")} looted.";
    }

    /// <summary>"Nasti, Warshield (2)": who looted the items, with a count when someone got more than one.</summary>
    private static string DescribeLooters(IEnumerable<LootEntry> entries) =>
        string.Join(", ", entries
            .GroupBy(e => e.Looter, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key));

    [RelayCommand]
    private async Task ScanLootLogAsync()
    {
        string? path = _dialogs.PickImportLogFile();
        if (path is null)
        {
            return;
        }

        await ScanLootAsync(path, from: null);
    }

    [RelayCommand]
    private async Task ScanLootFromPrimaryLogAsync()
    {
        WatchedLogConfig? primary = Logs.PrimaryDpsLog;
        if (primary is null || !File.Exists(primary.FilePath))
        {
            LootStatus = "Choose a Primary DPS character on the Characters tab first.";
            return;
        }

        await ScanLootAsync(primary.FilePath, AppSettings.GetHistoricalStart(HistoricalDaysLimit, _clock()));
    }

    private async Task ScanLootAsync(string path, DateTime? from)
    {
        LootStatus = $"Reading loot from {Path.GetFileName(path)}…";
        try
        {
            int added = await Task.Run(() => _pipeline.Loot.ScanLog(path, from));
            RebuildLoot();
            LootStatus = $"Added {added:N0} looted item{(added == 1 ? string.Empty : "s")} from {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LootStatus = $"Could not read {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearLoot()
    {
        _pipeline.Loot.Clear();
        RebuildLoot();
    }

    /// <summary>Copies the loot in the view that is showing, as text for Discord.</summary>
    [RelayCommand]
    private void CopyLoot()
    {
        var sb = new StringBuilder();
        switch (LootViewIndex)
        {
            case 1:
                foreach (IGrouping<string, LootMobRow> mob in LootMobRows.GroupBy(r => r.Mob))
                {
                    sb.AppendLine(mob.Key);
                    foreach (LootMobRow row in mob)
                    {
                        sb.AppendLine($"  {(row.Count > 1 ? row.Count + "x " : string.Empty)}{row.Item} - {row.Looters}");
                    }
                }

                break;
            case 2:
                foreach (LootSummaryRow row in LootSummaryRows)
                {
                    sb.AppendLine($"{(row.Count > 1 ? row.Count + "x " : string.Empty)}{row.Item} - {row.Looters}");
                }

                break;
            default:
                foreach (LootLineRow row in LootLineRows.Reverse())
                {
                    sb.AppendLine($"{row.Time}  {row.Looter} - {row.Item} ({row.Mob}){(row.Roll.Length > 0 ? ", " + row.Roll : string.Empty)}");
                }

                break;
        }

        CopyText(sb.ToString().TrimEnd(), "loot", LootViews[Math.Clamp(LootViewIndex, 0, 2)]);
    }

    private void CopyText(string text, string what, string detail)
    {
        if (text.Length == 0)
        {
            StatusMessage = $"There is no {what} to copy.";
            return;
        }

        try
        {
            _clipboard.SetText(text);
            StatusMessage = $"Copied the {what} ({detail.ToLowerInvariant()}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The clipboard could not be used: {ex.Message}";
        }
    }

    // ───────────────────────── attendance ─────────────────────────

    [ObservableProperty]
    private IReadOnlyList<AttendanceRaidRow> _attendanceRaids = Array.Empty<AttendanceRaidRow>();

    [ObservableProperty]
    private AttendanceRaidRow? _selectedAttendanceRaid;

    [ObservableProperty]
    private IReadOnlyList<RosterDisplayRow> _rosterRows = Array.Empty<RosterDisplayRow>();

    [ObservableProperty]
    private IReadOnlyList<AttendanceTotalRow> _attendanceTotals = Array.Empty<AttendanceTotalRow>();

    [ObservableProperty]
    private string _attendanceStatus = "No raid roster dumps read yet. In the game, open the Raid window and click Dump.";

    partial void OnSelectedAttendanceRaidChanged(AttendanceRaidRow? value) => RebuildRoster();

    private void RebuildAttendance()
    {
        IReadOnlyList<RaidAttendance> records = _raidRoster.Records();
        AttendanceRaidRow? selected = SelectedAttendanceRaid;
        AttendanceRaids = records
            .Select(r => new AttendanceRaidRow(r, r.Taken.ToString("ddd MMM d yyyy, HH:mm", CultureInfo.CurrentCulture), r.Server, r.Members.Count))
            .ToList();
        SelectedAttendanceRaid = selected is null
            ? AttendanceRaids.FirstOrDefault()
            : AttendanceRaids.FirstOrDefault(r => r.Record.FileName == selected.Record.FileName) ?? AttendanceRaids.FirstOrDefault();

        AttendanceTotals = RaidRosterStore.Totals(records)
            .Select(t => new AttendanceTotalRow(t.Name, t.Class, t.Present, t.Raids, t.Percent, t.LastSeen.ToString("MMM d", CultureInfo.CurrentCulture)))
            .ToList();

        AttendanceStatus = records.Count == 0
            ? "No raid roster dumps read yet. In the game, open the Raid window and click Dump."
            : $"{records.Count} raid{(records.Count == 1 ? string.Empty : "s")} recorded.";
        RebuildRoster();
    }

    private void RebuildRoster()
    {
        RosterRows = SelectedAttendanceRaid is null
            ? Array.Empty<RosterDisplayRow>()
            : SelectedAttendanceRaid.Record.Members
                .OrderBy(m => m.Group == 0 ? int.MaxValue : m.Group)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => new RosterDisplayRow(m.Group, m.Name, m.Level, m.Class, m.Role))
                .ToList();
    }

    [RelayCommand]
    private void RefreshAttendance()
    {
        int added = _raidRoster.ScanAll(EqFolderPath);
        RebuildAttendance();
        if (string.IsNullOrWhiteSpace(EqFolderPath))
        {
            AttendanceStatus = "Set your EverQuest folder first (Settings tab). The raid dump files are saved there.";
        }
        else if (added > 0)
        {
            AttendanceStatus = $"Read {added} new raid roster dump{(added == 1 ? string.Empty : "s")}.";
        }
    }

    /// <summary>Copies the selected raid as text for Discord: the date, the head count and each group's members.</summary>
    [RelayCommand]
    private void CopyAttendance()
    {
        if (SelectedAttendanceRaid is not { } raid)
        {
            StatusMessage = "Select a raid first.";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Raid {raid.When} ({raid.Server}): {raid.Members} present");
        foreach (IGrouping<int, RosterDisplayRow> group in RosterRows.GroupBy(r => r.Group))
        {
            sb.Append(group.Key == 0 ? "No group" : $"Group {group.Key}").Append(": ")
              .AppendLine(string.Join(", ", group.Select(r => r.Class.Length > 0 ? $"{r.Name} ({r.Class})" : r.Name)));
        }

        CopyText(sb.ToString().TrimEnd(), "raid roster", raid.When);
    }

    [RelayCommand]
    private void DeleteAttendance()
    {
        if (SelectedAttendanceRaid is not { } raid)
        {
            return;
        }

        _raidRoster.Remove(raid.Record);
        RebuildAttendance();
    }

    // ───────────────────────── items and scrolls across characters ─────────────────────────

    [ObservableProperty]
    private string _itemSearchText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ItemHit> _itemHits = Array.Empty<ItemHit>();

    [ObservableProperty]
    private string _itemSearchStatus = "Type part of an item name to find it on every character and in the shared bank.";

    [ObservableProperty]
    private IReadOnlyList<ScrollDisplayRow> _scrollRows = Array.Empty<ScrollDisplayRow>();

    [ObservableProperty]
    private IReadOnlyList<ShoppingDisplayRow> _shoppingRows = Array.Empty<ShoppingDisplayRow>();

    [ObservableProperty]
    private string _scrollPlanStatus = string.Empty;

    partial void OnItemSearchTextChanged(string value) => RebuildItemSearch();

    private void RebuildItemSearch()
    {
        string text = ItemSearchText?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            ItemHits = Array.Empty<ItemHit>();
            ItemSearchStatus = "Type part of an item name to find it on every character and in the shared bank.";
            return;
        }

        ItemHits = CharacterPlanner.FindItems(_characterData.All(), _characterData.SharedBanks(), text);
        ItemSearchStatus = ItemHits.Count == 0
            ? $"No item matching \"{text}\" in the inventory files read so far."
            : $"{ItemHits.Count:N0} match{(ItemHits.Count == 1 ? string.Empty : "es")}.";
    }

    private void RebuildCharacterTools()
    {
        RebuildItemSearch();

        IReadOnlyList<CharacterData> characters = _characterData.All();
        IReadOnlyList<SharedBankData> banks = _characterData.SharedBanks();
        var scrolls = new List<ScrollDisplayRow>();
        var shopping = new List<ShoppingDisplayRow>();
        foreach (string server in characters.Select(c => c.Server).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            (IReadOnlyList<ScrollPlanRow> carried, IReadOnlyList<ShoppingRow> missing) = CharacterPlanner.Plan(characters, banks, server);
            scrolls.AddRange(carried.Select(s => new ScrollDisplayRow(server, s.Spell, s.HeldBy, s.NeededBy)));
            shopping.AddRange(missing.Select(s => new ShoppingDisplayRow(server, s.Level, s.Spell, s.NeededBy)));
        }

        ScrollRows = scrolls;
        ShoppingRows = shopping;
        ScrollPlanStatus = characters.Count(c => c.Has(OutputFileKind.MissingSpells)) == 0
            ? "No missing-spells files read yet. In the game, type /outputfile missingspells on each character."
            : $"{scrolls.Count} scroll{(scrolls.Count == 1 ? string.Empty : "s")} you carry that another character needs; " +
              $"{shopping.Count} missing spell{(shopping.Count == 1 ? string.Empty : "s")} nobody has a scroll for.";
    }

    [RelayCommand]
    private void RefreshCharacterTools()
    {
        _characterData.ScanAll(EqFolderPath);
        RefreshCharacterInfo();
        RebuildCharacterTools();
    }
}
