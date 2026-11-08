using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.ViewModels;

/// <summary>One attack type's color on the Settings tab: a swatch, the lighter crit swatch and a hex box.</summary>
public sealed partial class AttackColorEditorViewModel : ObservableObject
{
    private readonly Action<string, string> _onEdited;
    private bool _updating;

    public AttackColorEditorViewModel(string key, string label, string defaultHex, string hex, Action<string, string> onEdited)
    {
        Key = key;
        Label = label;
        DefaultHex = defaultHex;
        _onEdited = onEdited ?? throw new ArgumentNullException(nameof(onEdited));
        _hex = ThemeColors.TryNormalize(hex, out string normalized) ? normalized : defaultHex;
        _hexText = _hex;
    }

    /// <summary>The attack type's name, or "Healing".</summary>
    public string Key { get; }

    public string Label { get; }

    public string DefaultHex { get; }

    [ObservableProperty]
    private string _hex;

    [ObservableProperty]
    private string _hexText;

    [ObservableProperty]
    private bool _isValid = true;

    /// <summary>The lighter shade used for crits.</summary>
    public string CritHex => ThemeColors.TryParse(Hex, out Rgba c) ? ThemeColors.ToHex(ThemeColors.Lighten(c, AttackColorScheme.CritLightening)) : Hex;

    partial void OnHexChanged(string value) => OnPropertyChanged(nameof(CritHex));

    partial void OnHexTextChanged(string value)
    {
        if (_updating)
        {
            return;
        }

        if (!ThemeColors.TryNormalize(value, out string normalized))
        {
            IsValid = false;
            return;
        }

        IsValid = true;
        if (!string.Equals(normalized, Hex, StringComparison.Ordinal))
        {
            Hex = normalized;
            _onEdited(Key, normalized);
        }
    }

    public void Set(string hex)
    {
        _updating = true;
        try
        {
            Hex = ThemeColors.TryNormalize(hex, out string normalized) ? normalized : Hex;
            HexText = Hex;
            IsValid = true;
        }
        finally
        {
            _updating = false;
        }
    }
}

// Overlay fading when out of combat, the DPS overlay's hide timeout, and the attack colors of the encounter pictures.
public sealed partial class MainViewModel
{
    /// <summary>How visible every overlay is while out of combat, in percent (10 to 100). 100 turns fading off.</summary>
    [ObservableProperty]
    private int _overlayIdlePercent = 100;

    /// <summary>Seconds without a combat line before the DPS overlay goes away. 0 keeps it up.</summary>
    [ObservableProperty]
    private int _dpsHideSeconds = OverlayFade.DefaultDpsHideSeconds;

    public ObservableCollection<AttackColorEditorViewModel> AttackColors { get; } = new();

    partial void OnOverlayIdlePercentChanged(int value)
    {
        int clamped = Math.Clamp(value, OverlayFade.MinIdlePercent, 100);
        if (clamped != value)
        {
            OverlayIdlePercent = clamped;
            return;
        }

        ApplyOverlayFade();
        if (!_restoring)
        {
            RequestSave();
        }
    }

    partial void OnDpsHideSecondsChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, 3600);
        if (clamped != value)
        {
            DpsHideSeconds = clamped;
            return;
        }

        ApplyOverlayFade();
        if (!_restoring)
        {
            RequestSave();
        }
    }

    /// <summary>Sets every overlay's opacity from how long ago combat was last seen. Called about four times a second.</summary>
    private void ApplyOverlayFade()
    {
        DateTime now = DateTime.UtcNow;
        CharacterActivityService activity = _pipeline.Activity;

        // Only characters that are logged in count: a log that has had no line for a minute belongs to a character who is gone, so its
        // combat does not keep the DPS overlay up, and with nobody logged in every overlay is hidden.
        DateTime? last = activity.LastCombatUtc(now);
        bool anyActive = activity.AnyActive(now);
        IsInCombat = OverlayFade.IsInCombat(last, now);
        foreach (OverlayKind kind in OverlayKindExtensions.All)
        {
            double opacity = OverlayFade.Opacity(last, now, OverlayIdlePercent, DpsHideSeconds, kind == OverlayKind.Dps, !AreOverlaysLocked);
            if (!anyActive && AreOverlaysLocked)
            {
                opacity = 0.0;
            }

            _overlays.SetOpacity(kind, opacity);
        }
    }

    /// <summary>Raised when the Colors... button is clicked; the main window opens (or brings forward) the Colors window.</summary>
    public event Action? ShowColorsRequested;

    /// <summary>Raised whenever a color changes, so an open Colors window can redraw its preview.</summary>
    public event Action? ColorsChanged;

    /// <summary>The colors as they are now, for the preview.</summary>
    public AttackColorScheme CurrentColors => AttackColorScheme.Current;

    [RelayCommand]
    private void OpenColors() => ShowColorsRequested?.Invoke();

    private void RestoreAttackColors(IReadOnlyDictionary<string, string>? saved)
    {
        var scheme = new AttackColorScheme(saved);
        AttackColors.Clear();
        foreach (AttackType type in AttackTypes.All)
        {
            AttackColors.Add(new AttackColorEditorViewModel(
                type.ToString(), AttackTypes.Label(type), AttackTypes.DefaultColor(type), scheme.Normal(type), OnAttackColorEdited));
        }

        AttackColors.Add(new AttackColorEditorViewModel(
            AttackColorScheme.HealingKey, "Healing", AttackColorScheme.DefaultHealingColor, scheme.Healing, OnAttackColorEdited));

        AttackColorScheme.Current = scheme;
        ColorsChanged?.Invoke();
    }

    private void OnAttackColorEdited(string key, string hex)
    {
        AttackColorScheme.Current = new AttackColorScheme(AttackColors.ToDictionary(c => c.Key, c => c.Hex));
        ColorsChanged?.Invoke();
        if (!_restoring)
        {
            RequestSave();
        }
    }

    private Dictionary<string, string> AttackColorSettings() =>
        AttackColors.ToDictionary(c => c.Key, c => c.Hex);

    [RelayCommand]
    private void ResetAttackColors()
    {
        foreach (AttackColorEditorViewModel editor in AttackColors)
        {
            editor.Set(editor.DefaultHex);
        }

        OnAttackColorEdited(string.Empty, string.Empty);
    }
}
