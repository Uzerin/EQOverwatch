using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

// Several fights picked in the Encounter DPS list (Ctrl- or Shift-click) show as one parse, "Combined (5)".
public sealed partial class MainViewModel
{
    private IReadOnlyList<EncounterRowViewModel> _pickedEncounters = Array.Empty<EncounterRowViewModel>();

    /// <summary>The fights picked in the list; two or more show as one combined parse.</summary>
    public void SetPickedEncounters(IReadOnlyList<EncounterRowViewModel> rows)
    {
        IReadOnlyList<EncounterRowViewModel> picked = rows ?? Array.Empty<EncounterRowViewModel>();
        if (picked.Count == _pickedEncounters.Count && picked.All(r => _pickedEncounters.Contains(r)))
        {
            return;
        }

        _pickedEncounters = picked;
        RefreshParticipants();
    }

    /// <summary>True while two or more fights are picked.</summary>
    public bool IsCombinedPick => _pickedEncounters.Count(r => r.Snapshot is not null && Encounters.Contains(r)) > 1;

    /// <summary>The parse the Encounter DPS tab shows: the picked fights combined, or the selected fight.</summary>
    public EncounterSnapshot? CurrentEncounter()
    {
        List<EncounterSnapshot> fights = _pickedEncounters
            .Where(r => r.Snapshot is not null && Encounters.Contains(r))
            .Select(r => r.Snapshot!)
            .ToList();
        return fights.Count > 1 ? CombinedFights.Combine(fights) : SelectedEncounter?.Snapshot;
    }
}
