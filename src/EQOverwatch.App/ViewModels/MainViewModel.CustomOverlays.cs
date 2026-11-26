using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

/// <summary>One of your own overlays on the Settings tab: its name (editable) and whether it is shown.</summary>
public sealed partial class CustomOverlayViewModel : ObservableObject
{
    private readonly Action<CustomOverlayViewModel> _changed;

    public CustomOverlayViewModel(string id, string name, bool isVisible, Action<CustomOverlayViewModel> changed)
    {
        Id = id;
        _name = name;
        _isVisible = isVisible;
        _changed = changed;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isVisible;

    partial void OnNameChanged(string value) => _changed(this);

    partial void OnIsVisibleChanged(bool value) => _changed(this);
}

/// <summary>A choice of the trigger editor's "Your overlay" box: an overlay's id (empty for none) and name.</summary>
public sealed record CustomOverlayChoice(string Id, string Name);

// Overlays you make yourself (Settings, Overlays): timer and text overlays that triggers can be routed to. Every overlay's title strip
// also has — (minimize to the strip) and ✕ (switch the overlay off) while overlays are unlocked.
public sealed partial class MainViewModel
{
    public ObservableCollection<CustomOverlayViewModel> CustomOverlays { get; } = new();

    /// <summary>True when you have made an overlay (the trigger editor's box and the list show then).</summary>
    public bool HasCustomOverlays => CustomOverlays.Count > 0;

    /// <summary>"Built-in overlay" plus your own, for the trigger editor.</summary>
    public ObservableCollection<CustomOverlayChoice> CustomOverlayChoices { get; } = new() { new CustomOverlayChoice(string.Empty, "None (use the overlay above)") };

    [ObservableProperty]
    private CustomOverlayViewModel? _selectedCustomOverlay;

    [RelayCommand]
    private void NewOverlay()
    {
        string id = Guid.NewGuid().ToString("N")[..12];
        int number = CustomOverlays.Count + 1;
        string name = $"My Overlay {number}";
        while (CustomOverlays.Any(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"My Overlay {++number}";
        }

        var overlay = new CustomOverlayViewModel(id, name, true, OnCustomOverlayChanged);
        CustomOverlays.Add(overlay);
        // A new overlay starts in the middle of the screen area the built-in ones use, staggered so several do not stack.
        _overlays.ShowCustom(id, name, OverlaySettings.DefaultTimer with { Left = 120 + 30 * (number % 8), Top = 120 + 30 * (number % 8), IsVisible = true });
        SelectedCustomOverlay = overlay;
        SyncCustomOverlayChoices();
        RequestSave();
    }

    /// <summary>Deletes the selected overlay; its triggers go back to their category's overlay.</summary>
    [RelayCommand]
    private void DeleteOverlay()
    {
        if (SelectedCustomOverlay is not { } overlay)
        {
            return;
        }

        CustomOverlays.Remove(overlay);
        _overlays.RemoveCustom(overlay.Id);
        foreach (TriggerRuleViewModel rule in Rules.Where(r => r.CustomOverlayId == overlay.Id))
        {
            rule.CustomOverlayId = null;
        }

        SyncCustomOverlayChoices();
        ApplyTriggers();
        RequestSave();
    }

    private void OnCustomOverlayChanged(CustomOverlayViewModel overlay)
    {
        if (_restoring)
        {
            return;
        }

        string name = overlay.Name.Trim().Length > 0 ? overlay.Name.Trim() : "My Overlay";
        OverlaySettings placement = _overlays.CaptureCustom(overlay.Id) ?? OverlaySettings.DefaultTimer;
        _overlays.ShowCustom(overlay.Id, name, placement with { IsVisible = overlay.IsVisible });
        SyncCustomOverlayChoices();
        RequestSave();
    }

    private void CloseCustomOverlay(string id)
    {
        if (CustomOverlays.FirstOrDefault(o => o.Id == id) is { } overlay)
        {
            overlay.IsVisible = false;
        }
    }

    /// <summary>The ✕ of a built-in overlay switches it off, the same as unticking it in Settings.</summary>
    private void CloseOverlay(OverlayKind kind) => SetOverlayVisible(kind, false);

    /// <summary>Switches a built-in overlay on or off, the same as its tick box in Settings.</summary>
    public void SetOverlayVisible(OverlayKind kind, bool visible)
    {
        switch (kind)
        {
            case OverlayKind.Buffs: IsBuffsOverlayVisible = visible; break;
            case OverlayKind.Debuffs: IsDebuffsOverlayVisible = visible; break;
            case OverlayKind.Songs: IsSongsOverlayVisible = visible; break;
            case OverlayKind.RaidTriggers: IsRaidTriggersOverlayVisible = visible; break;
            case OverlayKind.ChChain: IsChChainOverlayVisible = visible; break;
            case OverlayKind.Dps: IsDpsOverlayVisible = visible; break;
            case OverlayKind.Tells: IsTellsOverlayVisible = visible; break;
            case OverlayKind.MobCard: IsMobCardOverlayVisible = visible; break;
            case OverlayKind.FctDamage: IsFctDamageOverlayVisible = visible; break;
            case OverlayKind.FctHeals: IsFctHealsOverlayVisible = visible; break;
        }
    }

    private void RestoreCustomOverlays(IEnumerable<CustomOverlaySetting>? saved)
    {
        foreach (CustomOverlayViewModel old in CustomOverlays)
        {
            _overlays.RemoveCustom(old.Id);
        }

        CustomOverlays.Clear();
        foreach (CustomOverlaySetting s in saved ?? Enumerable.Empty<CustomOverlaySetting>())
        {
            if (string.IsNullOrWhiteSpace(s.Id) || CustomOverlays.Any(o => o.Id == s.Id))
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(s.Name) ? "My Overlay" : s.Name.Trim();
            OverlaySettings placement = s.Placement ?? OverlaySettings.DefaultTimer;
            CustomOverlays.Add(new CustomOverlayViewModel(s.Id, name, placement.IsVisible, OnCustomOverlayChanged));
            _overlays.ShowCustom(s.Id, name, placement);
        }

        SyncCustomOverlayChoices();
        if (Rules.Count > 0)
        {
            ApplyTriggers(); // routes triggers to the overlays just restored
        }
    }

    private List<CustomOverlaySetting> CustomOverlaySettings() =>
        CustomOverlays.Select(o => new CustomOverlaySetting
        {
            Id = o.Id,
            Name = o.Name.Trim().Length > 0 ? o.Name.Trim() : "My Overlay",
            Placement = (_overlays.CaptureCustom(o.Id) ?? OverlaySettings.DefaultTimer) with { IsVisible = o.IsVisible },
        }).ToList();

    private void SyncCustomOverlayChoices()
    {
        OnPropertyChanged(nameof(HasCustomOverlays));
        var wanted = new List<CustomOverlayChoice> { new(string.Empty, "None (use the overlay above)") };
        wanted.AddRange(CustomOverlays.Select(o => new CustomOverlayChoice(o.Id, o.Name)));
        if (!wanted.SequenceEqual(CustomOverlayChoices))
        {
            CustomOverlayChoices.Clear();
            foreach (CustomOverlayChoice choice in wanted)
            {
                CustomOverlayChoices.Add(choice);
            }
        }
    }
}
