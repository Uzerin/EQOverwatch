using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.ViewModels;

// Floating combat text: the three overlays' switches, text sizes and colors.
public sealed partial class MainViewModel
{
    public const double MinFctTextSize = 10;
    public const double MaxFctTextSize = 48;

    /// <summary>The floating combat text colors and their defaults.</summary>
    public static readonly IReadOnlyList<(FloatingStyle Style, string Label, string Default)> FloatingColorDefaults = new[]
    {
        (FloatingStyle.Melee, "Melee", "#FFFFFF"),
        (FloatingStyle.Ranged, "Ranged", "#FFD84D"),
        (FloatingStyle.Spell, "Spells", "#7FB2FF"),
        (FloatingStyle.DamageShield, "Damage shields", "#F0A050"),
        (FloatingStyle.Heal, "Heals", "#6FDC8C"),
        (FloatingStyle.Taken, "Damage taken", "#FF6B6B"),
    };

    [ObservableProperty]
    private bool _isFctDamageOverlayVisible = true;

    [ObservableProperty]
    private bool _isFctHealsOverlayVisible = true;


    [ObservableProperty]
    private double _fctDamageTextSize = 20;

    [ObservableProperty]
    private double _fctHealsTextSize = 20;


    /// <summary>The floating combat text colors, edited in the Colors window.</summary>
    public ObservableCollection<AttackColorEditorViewModel> FloatingColors { get; } = new();

    partial void OnIsFctDamageOverlayVisibleChanged(bool value) => OnFloatingTextVisibilityChanged(OverlayKind.FctDamage, value);

    partial void OnIsFctHealsOverlayVisibleChanged(bool value) => OnFloatingTextVisibilityChanged(OverlayKind.FctHeals, value);


    private void OnFloatingTextVisibilityChanged(OverlayKind kind, bool visible)
    {
        _pipeline.FloatingCombat.Enabled = IsFctDamageOverlayVisible || IsFctHealsOverlayVisible;
        OnOverlayVisibilityChanged(kind, visible);
    }

    partial void OnFctDamageTextSizeChanged(double value) => ClampFctSize(value, v => FctDamageTextSize = v);

    partial void OnFctHealsTextSizeChanged(double value) => ClampFctSize(value, v => FctHealsTextSize = v);


    private void ClampFctSize(double value, Action<double> set)
    {
        double clamped = Math.Round(Math.Clamp(double.IsFinite(value) ? value : 20, MinFctTextSize, MaxFctTextSize));
        if (clamped != value)
        {
            set(clamped);
            return;
        }

        SaveUnlessRestoring();
    }

    private void RestoreFloatingColors(IReadOnlyDictionary<string, string>? saved)
    {
        FloatingColors.Clear();
        foreach ((FloatingStyle style, string label, string def) in FloatingColorDefaults)
        {
            string hex = saved is not null && saved.TryGetValue(style.ToString(), out string? s) && EQOverwatch.Core.Theming.ThemeColors.TryNormalize(s, out string n) ? n : def;
            FloatingColors.Add(new AttackColorEditorViewModel(style.ToString(), label, def, hex, (_, _) => SaveUnlessRestoring()));
        }
    }

    /// <summary>The overlay a number goes to, its color, and its text size.</summary>
    public (OverlayKind Kind, string Hex, double Size, bool Shown) FloatingLook(FloatingNumber number)
    {
        OverlayKind kind = number.IsHeal ? OverlayKind.FctHeals : OverlayKind.FctDamage;
        string key = number.Style.ToString();
        string hex = FloatingColors.FirstOrDefault(c => c.Key == key)?.Hex
                     ?? FloatingColorDefaults.First(d => d.Style == number.Style).Default;
        (double size, bool shown) = kind switch
        {
            OverlayKind.FctHeals => (FctHealsTextSize, IsFctHealsOverlayVisible),
            _ => (FctDamageTextSize, IsFctDamageOverlayVisible),
        };
        return (kind, hex, size, shown);
    }

    /// <summary>Opens the app minimized when you sign in to Windows (the app sets your Run entry when this changes).</summary>
    [ObservableProperty]
    private bool _startWithWindows;

    /// <summary>Raised when Start with Windows is switched by you (not when settings are read).</summary>
    public event Action<bool>? StartWithWindowsChanged;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_restoring)
        {
            StartWithWindowsChanged?.Invoke(value);
        }

        SaveUnlessRestoring();
    }

    /// <summary>"100%": the character's legacy server XP bonus, from its log; a dash when not seen.</summary>
    private string LegacyBonusText(string character)
    {
        string server = Logs.FirstOrDefault(l => l.CharacterName.Equals(character, StringComparison.OrdinalIgnoreCase))?.ServerName ?? string.Empty;
        return _pipeline.Progress.Legacy(character, server).LegacyBonus is { } bonus ? bonus.ToString(CultureInfo.CurrentCulture) + "%" : "—";
    }

    /// <summary>"842", "+2,104" or "-907".</summary>
    public static string FloatingText(FloatingNumber number) =>
        number.Word ?? (number.IsHeal ? "+" : number.Panel == FloatingPanel.Taken ? "-" : string.Empty)
        + number.Amount.ToString("N0", CultureInfo.CurrentCulture);
}

// Mob History: a kill opens on the Encounter DPS tab.
public sealed partial class MainViewModel
{
    /// <summary>The main window's Encounter DPS tab.</summary>
    public const int EncounterDpsTab = 2;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenBossKill(BossKillRow? row)
    {
        if (row?.Fight is not { } fight)
        {
            return;
        }

        EncounterRowViewModel? match = Encounters
            .Where(e => e.Snapshot is { } s && s.TargetName.Trim().Equals(fight.Boss.Trim(), StringComparison.OrdinalIgnoreCase)
                        && Math.Abs((s.StartTime - fight.Start).TotalSeconds) <= 60)
            .OrderBy(e => Math.Abs((e.Snapshot!.StartTime - fight.Start).TotalSeconds))
            .FirstOrDefault();
        if (match is null)
        {
            BossKillsHint = $"The {fight.Start.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)} fight isn't in the Encounter DPS list any more. Read that night's log with Read a log file… to see it again.";
            return;
        }

        SelectedEncounter = match;
        SelectedMainTab = EncounterDpsTab;
    }
}
