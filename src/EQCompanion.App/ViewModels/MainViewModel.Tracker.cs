using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQCompanion.App.Presentation;
using EQCompanion.Core.Models;
using EQCompanion.Core.Settings;
using EQCompanion.Core.Spells;

namespace EQCompanion.App.ViewModels;

/// <summary>
/// The Buff &amp; Song Tracker tab (a list of chosen spells, each with its own overlay) and the Raid Triggers boss drop-down
/// (mobs from ended fights).
/// </summary>
public sealed partial class MainViewModel
{
    private const int MaxSuggestions = 100;
    private const int MinSearchLength = 2;

    // ─────────────────────────── tracked spells ───────────────────────────

    /// <summary>The tracked spells, in the order they were added.</summary>
    public ObservableCollection<TrackedSpellRowViewModel> TrackedSpells { get; } = new();

    /// <summary>The rows the grid shows (the list after the filter box). Replaced as a whole.</summary>
    [ObservableProperty]
    private IReadOnlyList<TrackedSpellRowViewModel> _visibleTrackedSpells = Array.Empty<TrackedSpellRowViewModel>();

    [ObservableProperty]
    private string _trackedFilterText = string.Empty;

    /// <summary>What is typed in the "Spell" search box.</summary>
    [ObservableProperty]
    private string _trackerSearchText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<SpellSuggestion> _trackerSuggestions = Array.Empty<SpellSuggestion>();

    [ObservableProperty]
    private SpellSuggestion? _selectedSuggestion;

    /// <summary>The overlay the next spell will be added to. It is kept between additions, so a run of songs can be added quickly.</summary>
    [ObservableProperty]
    private TimerOverlayCategory _trackerOverlay = TimerOverlayCategory.Buffs;

    [ObservableProperty]
    private string _trackerStatus = string.Empty;

    /// <summary>The picked spell's landing / fade text, so it can be checked against the log.</summary>
    [ObservableProperty]
    private string _trackerPreview = string.Empty;

    [ObservableProperty]
    private string _trackedSummary = "0 spells tracked";

    /// <summary>"3 bars started. Last: Oodles: Spirit of Wolf" (the quick answer to "is it seeing my buffs?").</summary>
    [ObservableProperty]
    private string _trackerActivity = NoTrackerActivity;

    private const string NoTrackerActivity = "No tracked buff or song has landed in your logs yet.";

    private long _lastTimersStarted;

    partial void OnTrackedFilterTextChanged(string value) => ApplyTrackedFilter();

    partial void OnTrackerSearchTextChanged(string value) => UpdateTrackerSuggestions();

    partial void OnSelectedSuggestionChanged(SpellSuggestion? value)
    {
        if (value is null)
        {
            TrackerPreview = string.Empty;
            return;
        }

        SpellInfo spell = value.Spell;
        string you = string.IsNullOrWhiteSpace(spell.CastOnYou) ? "(none)" : spell.CastOnYou;
        string other = string.IsNullOrWhiteSpace(spell.CastOnOther) ? "(none)" : $"<name>{spell.CastOnOther}";
        string fades = string.IsNullOrWhiteSpace(spell.SpellFades) ? "(none)" : spell.SpellFades;
        TrackerPreview = $"On you: {you}  ·  On others: {other}  ·  Fades: {fades}";
    }

    private void UpdateTrackerSuggestions()
    {
        string term = TrackerSearchText?.Trim() ?? string.Empty;

        // Picking an item puts its name into the box; keep the list (and with it the selection) as it is.
        if (TrackerSuggestions.Any(s => s.Name.Equals(term, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        IReadOnlyList<SpellInfo> all = _pipeline.Spells.Database.Spells;
        if (term.Length < MinSearchLength || all.Count == 0)
        {
            TrackerSuggestions = Array.Empty<SpellSuggestion>();
            TrackerStatus = all.Count == 0
                ? "Load spells_us.txt on the Settings tab to search spells."
                : $"Type at least {MinSearchLength} letters of a spell name, or a spell id.";
            return;
        }

        bool isId = int.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out int id);
        List<SpellSuggestion> matches = all
            .Where(s => s.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || (isId && s.SpellId == id))
            .OrderByDescending(s => s.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .Select(s => new SpellSuggestion(s))
            .ToList();

        TrackerSuggestions = matches;
        TrackerStatus = matches.Count == 0
            ? "No spell matches that."
            : matches.Count == MaxSuggestions ? $"Showing the first {MaxSuggestions} matches; type more to narrow it down." : $"{matches.Count} match(es).";
    }

    /// <summary>Adds the picked spell with the chosen overlay (or moves it to that overlay when it is already on the list).</summary>
    [RelayCommand]
    private void AddTrackedSpell()
    {
        SpellSuggestion? pick = SelectedSuggestion;
        if (pick is null)
        {
            string typed = TrackerSearchText?.Trim() ?? string.Empty;
            pick = TrackerSuggestions.FirstOrDefault(s => s.Name.Equals(typed, StringComparison.OrdinalIgnoreCase))
                   ?? (TrackerSuggestions.Count == 1 ? TrackerSuggestions[0] : null);
        }

        if (pick is null)
        {
            TrackerStatus = "Pick a spell from the search list first.";
            return;
        }

        TrackedSpellRowViewModel? existing = TrackedSpells.FirstOrDefault(r => r.SpellId == pick.SpellId);
        string overlayName = TrackerOverlay.DisplayName();
        string message;
        if (existing is not null)
        {
            existing.SetInfo(pick.Spell);
            existing.IsEnabled = true;
            existing.Overlay = TrackerOverlay;
            message = $"{pick.Name} was already tracked; it now uses the {overlayName} overlay.";
        }
        else
        {
            var setting = new TrackedSpellSetting
            {
                SpellId = pick.SpellId, SpellName = pick.Name, Overlay = TrackerOverlay, IsEnabled = true,
            };
            TrackedSpells.Add(CreateTrackedRow(setting, pick.Spell));
            ApplyTracked();
            message = $"Added {pick.Name} to the {overlayName} overlay.";
        }

        SelectedSuggestion = null;
        TrackerSearchText = string.Empty; // refreshes the suggestions and their status text...
        TrackerStatus = message;          // ...so the result goes last
    }

    [RelayCommand]
    private void RemoveTrackedSpell(TrackedSpellRowViewModel? row)
    {
        if (row is not null && TrackedSpells.Remove(row))
        {
            ApplyTracked();
            TrackerStatus = $"Stopped tracking {row.Name}.";
        }
    }

    private TrackedSpellRowViewModel CreateTrackedRow(TrackedSpellSetting setting, SpellInfo? info, bool isMigrated = false) =>
        new(setting, info, _ => ApplyTracked(), isMigrated);

    /// <summary>Sends the enabled rows (with their overlays) to the tracker, refreshes the list and saves.</summary>
    private void ApplyTracked()
    {
        _pipeline.Buffs.SetTrackedSpellOverlays(
            TrackedSpells.Where(r => r.IsEnabled).Select(r => new KeyValuePair<int, TimerOverlayCategory>(r.SpellId, r.Overlay)));

        int enabled = TrackedSpells.Count(r => r.IsEnabled);
        TrackedSummary = TrackedSpells.Count == 1
            ? $"1 spell tracked ({enabled} on)"
            : $"{TrackedSpells.Count:N0} spells tracked ({enabled:N0} on)";

        ApplyTrackedFilter();

        if (!_restoring)
        {
            RequestSave();
        }
    }

    private void ApplyTrackedFilter()
    {
        string term = TrackedFilterText?.Trim() ?? string.Empty;
        IEnumerable<TrackedSpellRowViewModel> rows = TrackedSpells;
        if (term.Length > 0)
        {
            rows = rows.Where(r =>
                r.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.CastOnYou.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.CastOnOther.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.Overlay.DisplayName().Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        VisibleTrackedSpells = rows.ToList();
    }

    /// <summary>Loads the saved list. Spell ids from an older settings file become rows too, flagged as migrated.</summary>
    private void RestoreTrackedSpells(AppSettings settings)
    {
        TrackedSpells.Clear();
        var seen = new HashSet<int>();

        foreach (TrackedSpellSetting setting in settings.TrackedSpells)
        {
            if (seen.Add(setting.SpellId))
            {
                TrackedSpells.Add(CreateTrackedRow(setting, info: null));
            }
        }

        foreach (int id in settings.TrackedSpellIds.Where(i => i > 0).OrderBy(i => i))
        {
            if (seen.Add(id))
            {
                TrackedSpells.Add(CreateTrackedRow(new TrackedSpellSetting { SpellId = id }, info: null, isMigrated: true));
            }
        }

        ApplyTracked();
    }

    /// <summary>
    /// Called whenever the spell file has (re)loaded or was cleared: fills in every row's name, duration and texts, gives migrated
    /// rows the file's suggested overlay once, and refreshes the search results.
    /// </summary>
    private void BuildSpellRows()
    {
        SpellDatabase database = _pipeline.Spells.Database;
        bool migratedAny = false;

        foreach (TrackedSpellRowViewModel row in TrackedSpells)
        {
            SpellInfo? info = database.FindById(row.SpellId);
            row.SetInfo(info);

            if (row.IsMigrated && info is not null)
            {
                row.SetOverlayQuietly(info.Category);
                row.MarkMigrationDone();
                migratedAny = true;
            }
        }

        if (migratedAny)
        {
            ApplyTracked();
        }
        else
        {
            ApplyTrackedFilter();
        }

        TrackerSuggestions = Array.Empty<SpellSuggestion>();
        UpdateTrackerSuggestions();
        UpdateRaidSpellResults();
        AbilitySuggestions = Array.Empty<AbilitySuggestion>();
        UpdateAbilitySuggestions();
    }

    private void UpdateTrackerActivity()
    {
        long started = _pipeline.Buffs.TimersStarted;
        if (started == _lastTimersStarted)
        {
            return;
        }

        _lastTimersStarted = started;
        TrackerActivity = started == 0
            ? NoTrackerActivity
            : $"{started:N0} bar(s) started. Last: {_pipeline.Buffs.LastStarted}";
    }

    // ─────────────────────── raid triggers: boss drop-down ───────────────────────

    private List<KnownMobOption> _knownMobs = new();
    private long _mobLiveVersion = -1;
    private long _mobHistoricalVersion = -1;

    /// <summary>Mobs matching what is typed in the boss box, most recently fought first.</summary>
    [ObservableProperty]
    private IReadOnlyList<KnownMobOption> _mobSuggestions = Array.Empty<KnownMobOption>();

    [ObservableProperty]
    private string _raidMobHint = "Leave blank to match any boss.";

    partial void OnRaidMobNameChanged(string value) => UpdateMobSuggestions();

    /// <summary>Rebuilds the list of fought mobs when the live or the imported encounters changed.</summary>
    private void UpdateKnownMobs()
    {
        DpsSnapshot live = _pipeline.LiveDps.Snapshot;
        DpsSnapshot historical = _pipeline.HistoricalDps.Snapshot;
        if (live.Version == _mobLiveVersion && historical.Version == _mobHistoricalVersion)
        {
            return;
        }

        _mobLiveVersion = live.Version;
        _mobHistoricalVersion = historical.Version;
        DateTime today = _clock().Date;

        _knownMobs = live.All.Concat(historical.All)
            .Where(e => !e.IsActive && !string.IsNullOrWhiteSpace(e.TargetName))
            .GroupBy(e => e.TargetName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Fights: g.Count(), Last: g.Max(e => e.EndTime)))
            .OrderByDescending(m => m.Last)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new KnownMobOption(
                m.Name, $"{m.Fights} fight{(m.Fights == 1 ? string.Empty : "s")} · {DescribeAge(today, m.Last)}", m.Fights))
            .ToList();

        MobSuggestions = Array.Empty<KnownMobOption>(); // so the filter below is not skipped as "unchanged"
        UpdateMobSuggestions();
    }

    private static string DescribeAge(DateTime today, DateTime last)
    {
        int days = (today - last.Date).Days;
        return days <= 0 ? "today" : days == 1 ? "yesterday" : $"{days} days ago";
    }

    private void UpdateMobSuggestions()
    {
        string term = RaidMobName?.Trim() ?? string.Empty;
        KnownMobOption? exact = _knownMobs.FirstOrDefault(m => m.Name.Equals(term, StringComparison.OrdinalIgnoreCase));

        RaidMobHint = term.Length == 0
            ? (_knownMobs.Count == 0
                ? "Leave blank to match any boss. Mobs you fight will be listed here; or type a name."
                : "Leave blank to match any boss, pick a mob you have fought, or type a new name.")
            : exact is not null
                ? $"{exact.Name}: {exact.Detail}."
                : "New name: used as typed. The timer runs when a matching log line names this mob or you are fighting it.";

        // Picking an entry puts its name into the box; keep the list as it is then.
        if (exact is not null && MobSuggestions.Any(m => m.Name.Equals(term, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        MobSuggestions = _knownMobs
            .Where(m => term.Length == 0 || m.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(MaxSuggestions)
            .ToList();
    }
}
