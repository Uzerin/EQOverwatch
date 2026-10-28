using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

/// <summary>One match in the AAs &amp; Discs search drop-down.</summary>
public sealed record AbilitySuggestion(AbilityInfo Ability)
{
    public string Name => Ability.Name;

    /// <summary>"ID 4499 · lasts 3:00 · recast 10:30".</summary>
    public string Detail =>
        $"ID {Ability.SpellId} · lasts {(Ability.ActiveSeconds > 0 ? AbilityTime.Format(Ability.ActiveSeconds) : "?")} · recast {AbilityTime.Format(Ability.RecastSeconds)}";
}

/// <summary>One of the two scope choices in the drop-downs.</summary>
public sealed record AbilityScopeOption(AbilityScope Value, string Display)
{
    public static IReadOnlyList<AbilityScopeOption> All { get; } = new[]
    {
        new AbilityScopeOption(AbilityScope.Everyone, "Everyone in the raid"),
        new AbilityScopeOption(AbilityScope.MyCharacters, "My characters only"),
    };
}

/// <summary>Reads and writes the "m:ss" / "h:mm:ss" / plain-seconds times used on the AAs &amp; Discs tab.</summary>
public static class AbilityTime
{
    public static string Format(int seconds)
    {
        if (seconds <= 0)
        {
            return "0:00";
        }

        int h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    /// <summary>"180", "3:00", "1:07:30" and blank (zero) are accepted; anything else is not.</summary>
    public static bool TryParse(string? text, out int seconds)
    {
        seconds = 0;
        string trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return true;
        }

        string[] parts = trimmed.Split(':');
        if (parts.Length > 3)
        {
            return false;
        }

        long total = 0;
        foreach (string part in parts)
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                return false;
            }

            total = total * 60 + value;
        }

        if (total > TrackedAbilitySetting.MaxSeconds)
        {
            return false;
        }

        seconds = (int)total;
        return true;
    }
}

/// <summary>
/// One row of the AAs &amp; Discs list. Everything but the name can be changed at any time; every change is reported to the owner,
/// which re-applies the list and saves.
/// </summary>
public sealed partial class AbilityRowViewModel : ObservableObject
{
    private readonly Action<AbilityRowViewModel> _changed;
    private readonly Func<AbilityRowViewModel, string>? _fadesLookup;

    /// <param name="fadesLookup">Finds the spell file's fade text for the row; null (or an empty result) shows a dash.</param>
    public AbilityRowViewModel(TrackedAbilitySetting setting, Action<AbilityRowViewModel> changed, Func<AbilityRowViewModel, string>? fadesLookup = null)
    {
        _fadesLookup = fadesLookup;
        ArgumentNullException.ThrowIfNull(setting);
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        Id = setting.Id;
        Name = setting.Name;
        SpellId = setting.SpellId;
        _activeSeconds = setting.ActiveSeconds;
        _cooldownSeconds = setting.CooldownSeconds;
        _scope = setting.Scope;
        _activeOverlay = setting.ActiveOverlay;
        _cooldownOverlay = setting.CooldownOverlay;
        _isEnabled = setting.IsEnabled;
    }

    public Guid Id { get; }

    public string Name { get; }

    public int SpellId { get; }

    /// <summary>The log lines that start this ability's bars.</summary>
    public string StartsWhen => $"You activate {Name}. · <Name> activates {Name}.";

    /// <summary>The spell file's "wears off" text for the ability, or a dash when it has none (most AAs).</summary>
    public string FadesText
    {
        get
        {
            string text = _fadesLookup?.Invoke(this)?.Trim() ?? string.Empty;
            return text.Length > 0 ? text : "—";
        }
    }

    /// <summary>The spell file was (re)loaded: look the fade text up again.</summary>
    public void RefreshSpellText() => OnPropertyChanged(nameof(FadesText));

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private AbilityScope _scope;

    [ObservableProperty]
    private TimerOverlayCategory _activeOverlay;

    [ObservableProperty]
    private TimerOverlayCategory _cooldownOverlay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveText))]
    private int _activeSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CooldownText))]
    private int _cooldownSeconds;

    /// <summary>How long it lasts, as typed in the grid ("3:00"). Text that is not a time is ignored and the old value shown again.</summary>
    public string ActiveText
    {
        get => AbilityTime.Format(ActiveSeconds);
        set => SetTime(value, v => ActiveSeconds = v, nameof(ActiveText));
    }

    /// <summary>How long until it can be used again, as typed in the grid ("10:30").</summary>
    public string CooldownText
    {
        get => AbilityTime.Format(CooldownSeconds);
        set => SetTime(value, v => CooldownSeconds = v, nameof(CooldownText));
    }

    /// <summary>What the row's bars look like, e.g. for the "Shows" column.</summary>
    public string Summary => $"{Name}: active {(ActiveSeconds > 0 ? ActiveText : "off")}, cooldown {(CooldownSeconds > 0 ? CooldownText : "off")}";

    private void SetTime(string? text, Action<int> apply, string property)
    {
        if (AbilityTime.TryParse(text, out int seconds))
        {
            apply(seconds);
        }

        OnPropertyChanged(property); // shows the cleaned-up value, or puts the old one back
    }

    partial void OnIsEnabledChanged(bool value) => Report();

    partial void OnScopeChanged(AbilityScope value) => Report();

    partial void OnActiveOverlayChanged(TimerOverlayCategory value) => Report();

    partial void OnCooldownOverlayChanged(TimerOverlayCategory value) => Report();

    partial void OnActiveSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(Summary));
        Report();
    }

    partial void OnCooldownSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(Summary));
        Report();
    }

    private void Report()
    {
        _changed(this);
    }

    public TrackedAbilitySetting ToSetting() => new()
    {
        Id = Id,
        Name = Name,
        SpellId = SpellId,
        ActiveSeconds = ActiveSeconds,
        CooldownSeconds = CooldownSeconds,
        Scope = Scope,
        ActiveOverlay = ActiveOverlay,
        CooldownOverlay = CooldownOverlay,
        IsEnabled = IsEnabled,
    };
}
