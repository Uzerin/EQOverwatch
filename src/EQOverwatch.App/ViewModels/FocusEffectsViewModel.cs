using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

/// <summary>One entry in a focus drop-down. A null <see cref="SpellId"/> is "None".</summary>
public sealed record FocusChoice(int? SpellId, string Label);

/// <summary>
/// The Focus Effects tab: which spell-duration focus a character uses for buffs and for debuffs/DoTs, and the learned alternate
/// abilities that lengthen spells (those come from the AAs tab on their own). Every change is saved at once through <c>save</c>.
/// </summary>
public sealed partial class FocusEffectsViewModel : ObservableObject
{
    private readonly Action<DurationProfile> _save;
    private readonly bool _loading;

    /// <param name="durationAas">The learned abilities that lengthen spells, as lines ("Spell Casting Reinforcement 3: +30%").</param>
    public FocusEffectsViewModel(IReadOnlyList<DurationFocus> foci, DurationProfile profile, Action<DurationProfile> save, IReadOnlyList<string>? durationAas = null)
    {
        DurationAas = durationAas ?? Array.Empty<string>();
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
        Message = foci.Count == 0
            ? "No duration focus spells were found. Load your spell file (Settings tab) first; the newer spells_us.txt with its spells_us_str.txt is needed for focus effects."
            : string.Empty;
        _loading = false;
    }

    public IReadOnlyList<FocusChoice> BeneficialChoices { get; }

    public IReadOnlyList<FocusChoice> DetrimentalChoices { get; }

    public string Message { get; }

    public bool HasMessage => Message.Length > 0;

    /// <summary>The learned abilities that lengthen this persona's spells, one line each.</summary>
    public IReadOnlyList<string> DurationAas { get; }

    public string DurationAasText => DurationAas.Count == 0
        ? "None learned yet. Abilities come from the AAs tab (the logs, or ones you add there)."
        : string.Join(Environment.NewLine, DurationAas);

    [ObservableProperty]
    private FocusChoice _selectedBeneficial;

    [ObservableProperty]
    private FocusChoice _selectedDetrimental;

    public DurationProfile Profile => new(SelectedBeneficial?.SpellId, SelectedDetrimental?.SpellId, 0);

    partial void OnSelectedBeneficialChanged(FocusChoice value) => Save();

    partial void OnSelectedDetrimentalChanged(FocusChoice value) => Save();


    private void Save()
    {
        if (!_loading)
        {
            _save(Profile);
        }
    }
}
