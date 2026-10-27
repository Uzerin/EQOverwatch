using CommunityToolkit.Mvvm.ComponentModel;
using EQCompanion.Core.Models;
using EQCompanion.Core.Settings;

namespace EQCompanion.App.ViewModels;

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
    }

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
    };
}
