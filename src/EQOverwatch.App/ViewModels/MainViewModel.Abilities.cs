using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

/// <summary>The AAs &amp; Discs tab: which abilities get an active bar and a cooldown bar, for whom, and in which overlays.</summary>
public sealed partial class MainViewModel
{
    private const int MaxAbilitySuggestions = 100;

    /// <summary>The tracked abilities, in the order they were added.</summary>
    public ObservableCollection<AbilityRowViewModel> TrackedAbilities { get; } = new();

    public IReadOnlyList<AbilityScopeOption> ScopeOptions => AbilityScopeOption.All;

    /// <summary>What is typed in the "Ability" search box. Names that are not in the spell file can simply be typed in full.</summary>
    [ObservableProperty]
    private string _abilitySearchText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<AbilitySuggestion> _abilitySuggestions = Array.Empty<AbilitySuggestion>();

    [ObservableProperty]
    private AbilitySuggestion? _selectedAbilitySuggestion;

    /// <summary>How long the next ability lasts ("3:00"); filled in from the spell file when one is picked. Blank means no active bar.</summary>
    [ObservableProperty]
    private string _abilityActiveText = string.Empty;

    /// <summary>How long until the next ability can be used again ("10:30"). Blank means no cooldown bar.</summary>
    [ObservableProperty]
    private string _abilityCooldownText = string.Empty;

    [ObservableProperty]
    private AbilityScope _abilityScope = AbilityScope.Everyone;

    [ObservableProperty]
    private TimerOverlayCategory _abilityActiveOverlay = TimerOverlayCategory.Buffs;

    [ObservableProperty]
    private TimerOverlayCategory _abilityCooldownOverlay = TimerOverlayCategory.RaidTriggers;

    [ObservableProperty]
    private string _abilityStatus = string.Empty;

    [ObservableProperty]
    private string _abilitySummary = "0 abilities tracked";

    [ObservableProperty]
    private string _abilityActivity = NoAbilityActivity;

    private const string NoAbilityActivity = "No tracked AA or discipline has been used in your logs yet.";

    private long _lastAbilityTimers;

    partial void OnAbilitySearchTextChanged(string value) => UpdateAbilitySuggestions();

    partial void OnSelectedAbilitySuggestionChanged(AbilitySuggestion? value)
    {
        if (value is null)
        {
            return;
        }

        AbilityActiveText = value.Ability.ActiveSeconds > 0 ? AbilityTime.Format(value.Ability.ActiveSeconds) : string.Empty;
        AbilityCooldownText = AbilityTime.Format(value.Ability.RecastSeconds);
    }

    private void UpdateAbilitySuggestions()
    {
        string term = AbilitySearchText?.Trim() ?? string.Empty;

        // Picking an item puts its name into the box; keep the list (and with it the selection) as it is.
        if (AbilitySuggestions.Any(s => s.Name.Equals(term, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        IReadOnlyList<AbilityInfo> all = _pipeline.Spells.Database.Abilities;
        if (term.Length < MinSearchLength || all.Count == 0)
        {
            AbilitySuggestions = Array.Empty<AbilitySuggestion>();
            AbilityStatus = all.Count == 0
                ? "No spell file with recast times is loaded. Type the ability's name exactly as the log prints it, then fill in the times."
                : $"Type at least {MinSearchLength} letters to search {all.Count:N0} abilities, or type a name that is not listed (most AAs) and fill in the times.";
            return;
        }

        bool isId = int.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out int id);
        List<AbilitySuggestion> matches = all
            .Where(a => a.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || (isId && a.SpellId == id))
            .OrderByDescending(a => a.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAbilitySuggestions)
            .Select(a => new AbilitySuggestion(a))
            .ToList();

        AbilitySuggestions = matches;
        AbilityStatus = matches.Count == 0
            ? "Not in the spell file. You can still add it: type the name exactly as the log prints it and fill in the times."
            : $"{matches.Count} match(es).";
    }

    /// <summary>Adds the picked (or typed) ability, or updates it when it is already on the list.</summary>
    [RelayCommand]
    private void AddAbility()
    {
        AbilityInfo? info = SelectedAbilitySuggestion?.Ability;
        string name = (info?.Name ?? AbilitySearchText ?? string.Empty).Trim();
        info ??= _pipeline.Spells.Database.FindAbility(name); // typed in full, but it is in the spell file
        if (info is not null && string.IsNullOrWhiteSpace(AbilityActiveText) && string.IsNullOrWhiteSpace(AbilityCooldownText))
        {
            AbilityActiveText = info.ActiveSeconds > 0 ? AbilityTime.Format(info.ActiveSeconds) : string.Empty;
            AbilityCooldownText = AbilityTime.Format(info.RecastSeconds);
        }
        if (name.Length == 0)
        {
            AbilityStatus = "Pick an ability from the search list, or type its name.";
            return;
        }

        if (name.Length > TrackedAbilitySetting.MaxNameLength)
        {
            AbilityStatus = $"The name can be at most {TrackedAbilitySetting.MaxNameLength} characters.";
            return;
        }

        if (!AbilityTime.TryParse(AbilityActiveText, out int active) || !AbilityTime.TryParse(AbilityCooldownText, out int cooldown))
        {
            AbilityStatus = "Times look like 3:00, 10:30 or 180 (seconds), up to 24 hours. Leave one blank to skip that bar.";
            return;
        }

        if (active <= 0 && cooldown <= 0)
        {
            AbilityStatus = "Enter how long it lasts, how long until it can be used again, or both.";
            return;
        }

        AbilityRowViewModel? existing = TrackedAbilities.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        string message;
        if (existing is not null)
        {
            existing.ActiveSeconds = active;
            existing.CooldownSeconds = cooldown;
            existing.Scope = AbilityScope;
            existing.ActiveOverlay = AbilityActiveOverlay;
            existing.CooldownOverlay = AbilityCooldownOverlay;
            existing.IsEnabled = true;
            message = $"{existing.Name} was already tracked; it now uses these settings.";
        }
        else
        {
            var setting = new TrackedAbilitySetting
            {
                Name = name,
                SpellId = info?.SpellId ?? 0,
                ActiveSeconds = active,
                CooldownSeconds = cooldown,
                Scope = AbilityScope,
                ActiveOverlay = AbilityActiveOverlay,
                CooldownOverlay = AbilityCooldownOverlay,
                IsEnabled = true,
            };
            TrackedAbilities.Add(CreateAbilityRow(setting));
            ApplyAbilities();
            message = $"Added {name}.";
        }

        SelectedAbilitySuggestion = null;
        AbilityActiveText = string.Empty;
        AbilityCooldownText = string.Empty;
        AbilitySearchText = string.Empty; // refreshes the suggestions and their status text...
        AbilityStatus = message;          // ...so the result goes last
    }

    [RelayCommand]
    private void RemoveAbility(AbilityRowViewModel? row)
    {
        if (row is not null && TrackedAbilities.Remove(row))
        {
            ApplyAbilities();
            AbilityStatus = $"Stopped tracking {row.Name}.";
        }
    }

    private AbilityRowViewModel CreateAbilityRow(TrackedAbilitySetting setting) => new(setting, _ => ApplyAbilities(), FadesFor);

    /// <summary>The spell file's fade text for an ability: looked up by spell id, else by name.</summary>
    private string FadesFor(AbilityRowViewModel row)
    {
        SpellDatabase database = _pipeline.Spells.Database;
        SpellInfo? spell = (row.SpellId > 0 ? database.FindById(row.SpellId) : null) ?? database.FindByName(row.Name);
        return spell?.SpellFades ?? string.Empty;
    }

    private SpellDatabase? _abilityFadesDatabase;

    /// <summary>When a different spell file has been loaded, the Fades column reads it again.</summary>
    private void RefreshAbilityFades()
    {
        SpellDatabase current = _pipeline.Spells.Database;
        if (ReferenceEquals(current, _abilityFadesDatabase))
        {
            return;
        }

        _abilityFadesDatabase = current;
        foreach (AbilityRowViewModel row in TrackedAbilities)
        {
            row.RefreshSpellText();
        }
    }

    /// <summary>Sends the list to the tracker, refreshes the summary and saves.</summary>
    private void ApplyAbilities()
    {
        _pipeline.Abilities.SetAbilities(TrackedAbilities.Select(r => r.ToSetting()));

        int enabled = TrackedAbilities.Count(r => r.IsEnabled);
        AbilitySummary = TrackedAbilities.Count == 1
            ? $"1 ability tracked ({enabled} on)"
            : $"{TrackedAbilities.Count:N0} abilities tracked ({enabled:N0} on)";

        if (!_restoring)
        {
            RequestSave();
        }
    }

    private void RestoreTrackedAbilities(AppSettings settings)
    {
        TrackedAbilities.Clear();
        foreach (TrackedAbilitySetting setting in settings.TrackedAbilities ?? new List<TrackedAbilitySetting>())
        {
            TrackedAbilities.Add(CreateAbilityRow(setting));
        }

        ApplyAbilities();
    }

    private void UpdateAbilityActivity()
    {
        RefreshAbilityFades();

        long started = _pipeline.Abilities.TimersStarted;
        if (started == _lastAbilityTimers)
        {
            return;
        }

        _lastAbilityTimers = started;
        AbilityActivity = started == 0
            ? NoAbilityActivity
            : $"{started:N0} bar(s) started. Last: {_pipeline.Abilities.LastStarted}";
    }
}
