using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

/// <summary>One entry in a focus drop-down. A null <see cref="SpellId"/> is "None".</summary>
public sealed record FocusChoice(int? SpellId, string Label);

/// <summary>
/// The Focus Effects tab: which spell-duration focus a character uses for buffs and for debuffs/DoTs, and the AA duration percentage.
/// Every change is saved at once through <c>save</c>.
/// </summary>
public sealed partial class FocusEffectsViewModel : ObservableObject
{
    public const string Instructions =
        "Open your AA window (Alt+V), find the AAs that increase spell duration, add up the percentages they show, " +
        "and type the total here. Leave it at 0 if you have none.";

    private readonly Action<DurationProfile> _save;
    private readonly bool _loading;

    public FocusEffectsViewModel(IReadOnlyList<DurationFocus> foci, DurationProfile profile, Action<DurationProfile> save)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _loading = true;

        var none = new FocusChoice(null, "None");
        BeneficialChoices = new[] { none }
            .Concat(foci.Where(f => f.AppliesTo != FocusAppliesTo.Detrimental).Select(f => new FocusChoice(f.SpellId, f.Label)))
            .ToList();
        DetrimentalChoices = new[] { none }
            .Concat(foci.Where(f => f.AppliesTo != FocusAppliesTo.Beneficial).Select(f => new FocusChoice(f.SpellId, f.Label)))
            .ToList();

        _selectedBeneficial = BeneficialChoices.FirstOrDefault(c => c.SpellId == profile.BeneficialFocusId) ?? none;
        _selectedDetrimental = DetrimentalChoices.FirstOrDefault(c => c.SpellId == profile.DetrimentalFocusId) ?? none;
        _aaPercent = Math.Clamp(profile.AaPercent, 0, 100);
        Message = foci.Count == 0
            ? "No duration focus spells were found. Load your spell file (Settings tab) first; the newer spells_us.txt with its spells_us_str.txt is needed for focus effects."
            : string.Empty;
        _loading = false;
    }

    public IReadOnlyList<FocusChoice> BeneficialChoices { get; }

    public IReadOnlyList<FocusChoice> DetrimentalChoices { get; }

    public string Message { get; }

    public bool HasMessage => Message.Length > 0;

    public string AaInstructions => Instructions;

    [ObservableProperty]
    private FocusChoice _selectedBeneficial;

    [ObservableProperty]
    private FocusChoice _selectedDetrimental;

    [ObservableProperty]
    private int _aaPercent;

    public DurationProfile Profile => new(SelectedBeneficial?.SpellId, SelectedDetrimental?.SpellId, Math.Clamp(AaPercent, 0, 100));

    partial void OnSelectedBeneficialChanged(FocusChoice value) => Save();

    partial void OnSelectedDetrimentalChanged(FocusChoice value) => Save();

    partial void OnAaPercentChanged(int value)
    {
        if (value is < 0 or > 100)
        {
            AaPercent = Math.Clamp(value, 0, 100);
            return;
        }

        Save();
    }

    private void Save()
    {
        if (!_loading)
        {
            _save(Profile);
        }
    }
}
