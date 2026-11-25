using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

/// <summary>Editable wrapper around a <see cref="TriggerRule"/> for the trigger editor form.</summary>
public sealed partial class TriggerRuleViewModel : ObservableObject
{
    public TriggerRuleViewModel(TriggerRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        Id = rule.Id;
        _name = rule.Name;
        _pattern = rule.Pattern;
        _useRegex = rule.UseRegex;
        _isEnabled = rule.IsEnabled;
        _timerDurationSeconds = rule.TimerDurationSeconds;
        _timerLabelTemplate = rule.TimerLabelTemplate;
        _alertTextTemplate = rule.AlertTextTemplate;
        _audioText = rule.AudioText;
        _audioFilePath = rule.AudioFilePath;
        _overlayCategory = rule.OverlayCategory;
        _customOverlayId = rule.CustomOverlayId;
        _folder = rule.Folder ?? string.Empty;
        _zone = rule.Zone ?? string.Empty;
    }

    /// <summary>Your own folder ("" shows as General).</summary>
    [ObservableProperty]
    private string _folder;

    /// <summary>The zone the trigger is for ("" for any zone).</summary>
    [ObservableProperty]
    private string _zone;

    /// <summary>The list's first grouping level: the expansion or your folder. Set by the main view model.</summary>
    [ObservableProperty]
    private string _groupTop = string.Empty;

    /// <summary>The second level: the zone (empty when grouped by folder).</summary>
    [ObservableProperty]
    private string _groupSub = string.Empty;

    /// <summary>Sorts the first-level groups (expansion release order; "Any zone" first).</summary>
    [ObservableProperty]
    private int _groupOrder;

    public Guid Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _pattern;

    [ObservableProperty]
    private bool _useRegex;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private double _timerDurationSeconds;

    [ObservableProperty]
    private string _timerLabelTemplate;

    [ObservableProperty]
    private string _alertTextTemplate;

    [ObservableProperty]
    private string _audioText;

    [ObservableProperty]
    private string _audioFilePath;

    /// <summary>Which timer overlay shows this trigger's alert and countdown.</summary>
    [ObservableProperty]
    private TimerOverlayCategory _overlayCategory;

    /// <summary>One of your own overlays that shows this trigger instead, or null.</summary>
    [ObservableProperty]
    private string? _customOverlayId;

    /// <summary>Compile error from the engine (bad regex, empty pattern), or null. Not persisted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public TriggerRule ToRule() => new()
    {
        Id = Id,
        Name = Name ?? string.Empty,
        Pattern = Pattern ?? string.Empty,
        UseRegex = UseRegex,
        IsEnabled = IsEnabled,
        TimerDurationSeconds = double.IsFinite(TimerDurationSeconds) ? Math.Max(0, TimerDurationSeconds) : 0,
        TimerLabelTemplate = TimerLabelTemplate ?? string.Empty,
        AlertTextTemplate = AlertTextTemplate ?? string.Empty,
        AudioText = AudioText ?? string.Empty,
        AudioFilePath = AudioFilePath ?? string.Empty,
        OverlayCategory = OverlayCategory,
        CustomOverlayId = string.IsNullOrEmpty(CustomOverlayId) ? null : CustomOverlayId,
        Folder = (Folder ?? string.Empty).Trim(),
        Zone = (Zone ?? string.Empty).Trim(),
    };
}
