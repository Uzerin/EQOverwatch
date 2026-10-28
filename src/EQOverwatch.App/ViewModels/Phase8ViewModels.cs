using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.App.Presentation;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

/// <summary>One match in the tracker's spell search drop-down.</summary>
public sealed record SpellSuggestion(SpellInfo Spell)
{
    public int SpellId => Spell.SpellId;

    /// <summary>Also the text put into the search box when the suggestion is picked.</summary>
    public string Name => Spell.Name;

    /// <summary>"ID 2603 · 03:00"</summary>
    public string Detail => $"ID {Spell.SpellId} · {OverlayFormat.Duration(Spell.BaseDurationSeconds)}";
}

/// <summary>One mob in the Raid Triggers boss drop-down, built from the fights that have ended.</summary>
public sealed record KnownMobOption(string Name, string Detail, int Fights);

/// <summary>
/// One row of the Buff &amp; Song Tracker list. The overlay is the user's choice and can be changed at any time; so can the On
/// switch. Both report to the owner, which re-applies the list and saves.
/// </summary>
public sealed partial class TrackedSpellRowViewModel : ObservableObject
{
    private readonly Action<TrackedSpellRowViewModel> _changed;
    private readonly string _savedName;
    private SpellInfo? _info;
    private bool _quiet;

    public TrackedSpellRowViewModel(TrackedSpellSetting setting, SpellInfo? info, Action<TrackedSpellRowViewModel> changed, bool isMigrated = false)
    {
        ArgumentNullException.ThrowIfNull(setting);
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        SpellId = setting.SpellId;
        _savedName = setting.SpellName ?? string.Empty;
        _info = info;
        _overlay = setting.Overlay;
        _isEnabled = setting.IsEnabled;
        IsMigrated = isMigrated;
    }

    public int SpellId { get; }

    /// <summary>
    /// True for a row created from a bare spell id of an older settings file. Its overlay is only a guess until the spell file is
    /// loaded, when it is set once from the file's suggestion.
    /// </summary>
    public bool IsMigrated { get; private set; }

    public string Name => _info?.Name ?? (_savedName.Length > 0 ? _savedName : $"Spell {SpellId}");

    public string DurationText => _info is null ? "?" : OverlayFormat.Duration(_info.BaseDurationSeconds);

    public string CastOnYou => _info?.CastOnYou ?? string.Empty;

    public string CastOnOther => _info?.CastOnOther ?? string.Empty;

    public string SpellFades => _info?.SpellFades ?? string.Empty;

    public SpellInfo? Info => _info;

    /// <summary>
    /// What starts the bar: songs begin at "You begin singing X." (nothing else is needed); buffs begin when the cast line
    /// "You begin casting X." is followed by the landing text.
    /// </summary>
    public string StartsWhen => Overlay == TimerOverlayCategory.Songs
        ? $"You begin singing {Name}."
        : $"You begin casting {Name}. + landing text";

    [ObservableProperty]
    private TimerOverlayCategory _overlay;

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnOverlayChanged(TimerOverlayCategory value)
    {
        OnPropertyChanged(nameof(StartsWhen));
        if (!_quiet)
        {
            IsMigrated = false; // the user has chosen
            _changed(this);
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_quiet)
        {
            _changed(this);
        }
    }

    /// <summary>Called when the spell file (re)loads: fills in names, durations and texts.</summary>
    public void SetInfo(SpellInfo? info)
    {
        _info = info;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Sets the overlay without telling the owner (used for the one-time guess of migrated rows).</summary>
    public void SetOverlayQuietly(TimerOverlayCategory overlay)
    {
        _quiet = true;
        try
        {
            Overlay = overlay;
        }
        finally
        {
            _quiet = false;
        }
    }

    public void MarkMigrationDone() => IsMigrated = false;

    public TrackedSpellSetting ToSetting() => new()
    {
        SpellId = SpellId,
        SpellName = _info?.Name ?? _savedName,
        Overlay = Overlay,
        IsEnabled = IsEnabled,
    };
}
