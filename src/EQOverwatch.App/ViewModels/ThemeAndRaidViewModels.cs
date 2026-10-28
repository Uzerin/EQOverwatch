using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.ViewModels;

/// <summary>An entry for an overlay-category drop-down.</summary>
public sealed record OverlayCategoryOption(TimerOverlayCategory Value, string Display)
{
    public static IReadOnlyList<OverlayCategoryOption> All { get; } =
        TimerOverlayCategoryExtensions.All.Select(c => new OverlayCategoryOption(c, c.DisplayName())).ToArray();
}

/// <summary>
/// One color in the Theme bar: a swatch plus a hex box. <see cref="HexText"/> is what the user types;
/// <see cref="Hex"/> only ever holds a valid color, so the swatch never shows garbage while typing.
/// </summary>
public sealed partial class ThemeColorEditorViewModel : ObservableObject
{
    private readonly Action<ThemeColorKey, string> _onEdited;
    private bool _updating;

    public ThemeColorEditorViewModel(ThemeColorKey key, string hex, Action<ThemeColorKey, string> onEdited)
    {
        Key = key;
        _onEdited = onEdited ?? throw new ArgumentNullException(nameof(onEdited));
        _hex = ThemeColors.TryNormalize(hex, out string normalized) ? normalized : ThemePresets.ObsidianSlate.Get(key);
        _hexText = _hex;
    }

    public ThemeColorKey Key { get; }

    public string Label => Key.Label();

    /// <summary>The valid color currently in effect ("#RRGGBB" or "#AARRGGBB").</summary>
    [ObservableProperty]
    private string _hex;

    /// <summary>The text box content.</summary>
    [ObservableProperty]
    private string _hexText;

    /// <summary>False while <see cref="HexText"/> is not a color yet.</summary>
    [ObservableProperty]
    private bool _isValid = true;

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

    /// <summary>Shows a color that came from the theme itself (a preset was chosen); does not count as an edit.</summary>
    public void SetFromTheme(string hex)
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

/// <summary>A row in the raid boss timer list.</summary>
public sealed partial class RaidTimerRuleViewModel : ObservableObject
{
    private readonly RaidBossTimerRule _rule;
    private readonly Action<RaidTimerRuleViewModel> _onChanged;

    public RaidTimerRuleViewModel(RaidBossTimerRule rule, Action<RaidTimerRuleViewModel> onChanged)
    {
        _rule = rule ?? throw new ArgumentNullException(nameof(rule));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _isEnabled = rule.IsEnabled; // the field, so building the list does not fire the callback
    }

    public Guid Id => _rule.Id;
    public string MobText => string.IsNullOrWhiteSpace(_rule.MobName) ? "Any boss" : _rule.MobName.Trim();
    public string SpellName => _rule.SpellName;
    public string MatchText => _rule.MatchText;
    public int RecastSeconds => _rule.RecastSeconds;
    public string CategoryText => _rule.OverlayCategory.DisplayName();
    public string StartText => string.IsNullOrWhiteSpace(_rule.StartTtsText) ? string.Empty : _rule.StartTtsText;
    public string WarningText => string.IsNullOrWhiteSpace(_rule.WarningTtsText) ? string.Empty : _rule.WarningTtsText;

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        _rule.IsEnabled = value;
        _onChanged(this);
    }

    /// <summary>A copy of the rule as it is now (safe to hand to other threads).</summary>
    public RaidBossTimerRule ToRule() => _rule.Clone();
}
