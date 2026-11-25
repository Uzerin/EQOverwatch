using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

/// <summary>One entry in a focus drop-down. A null <see cref="SpellId"/> is "None".</summary>
public sealed record FocusChoice(int? SpellId, string Label);

/// <summary>One focus kind on the Focus Effects tab: the focus picked for it, what it gives at the character's level, and the item it came from.</summary>
public sealed partial class FocusTypeRow : ObservableObject
{
    private readonly Func<int?, string> _benefit;
    private readonly Action _changed;

    public FocusTypeRow(FocusKind kind, IReadOnlyList<FocusChoice> choices, FocusChoice selected, string fromItem, Func<int?, string> benefit, Action changed)
    {
        Kind = kind;
        Choices = choices;
        _fromItem = fromItem;
        _benefit = benefit;
        _changed = changed;
        _selected = selected;
        _benefitText = benefit(selected.SpellId);
    }

    public FocusKind Kind { get; }

    public string Title => Kind.Title();

    public IReadOnlyList<FocusChoice> Choices { get; }

    /// <summary>"Meditative Blanket": the item the focus was set from; empty for a focus picked by hand.</summary>
    [ObservableProperty]
    private string _fromItem;

    [ObservableProperty]
    private FocusChoice _selected;

    [ObservableProperty]
    private string _benefitText;

    partial void OnSelectedChanged(FocusChoice value)
    {
        if (value is null)
        {
            Selected = Choices[0]; // never empty: "None"
            return;
        }

        BenefitText = _benefit(value.SpellId);
        FromItem = string.Empty; // picked by hand now
        _changed();
    }
}

/// <summary>
/// The Focus Effects tab: a focus per kind (buff and detrimental duration, healing, damage, spell haste, mana cost, range, hate and reagent
/// cost), each with what it gives at the character's level and the item it was set from, and the learned alternate abilities that
/// lengthen spells. The two duration
/// kinds change timer bars; the rest are for reference. Every change is saved at once through <c>save</c>.
/// </summary>
public sealed partial class FocusEffectsViewModel : ObservableObject
{
    private readonly Action<DurationProfile> _save;
    private readonly bool _loading;

    /// <param name="level">The character's level (0 when not known), for what each focus gives.</param>
    public FocusEffectsViewModel(
        SpellDatabase database, DurationProfile profile, Action<DurationProfile> save, IReadOnlyList<string>? durationAas = null, int level = 0)
    {
        _applied = profile.AppliedItems;
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _loading = true;
        DurationAas = durationAas ?? Array.Empty<string>();
        Level = level;

        var none = new FocusChoice(null, "None");
        var rows = new List<FocusTypeRow>();
        foreach (FocusKind kind in FocusKinds.All)
        {
            IReadOnlyList<ItemFocus> foci = database.ItemFoci.TryGetValue(kind, out IReadOnlyList<ItemFocus>? list) ? list : Array.Empty<ItemFocus>();
            var choices = new List<FocusChoice> { none };
            choices.AddRange(foci.Select(f => new FocusChoice(f.SpellId, f.Label)));
            int? picked = kind switch
            {
                FocusKind.BuffDuration => profile.BeneficialFocusId,
                FocusKind.DetrimentalDuration => profile.DetrimentalFocusId,
                _ => profile.Foci?.GetValueOrDefault(kind),
            };
            FocusChoice selected = choices.FirstOrDefault(c => c.SpellId == picked && picked is not null) ?? none;
            string fromItem = selected.SpellId is not null ? profile.FromItems?.GetValueOrDefault(kind) ?? string.Empty : string.Empty;
            FocusKind k = kind;
            rows.Add(new FocusTypeRow(kind, choices, selected, fromItem,
                id => id is int spellId && database.FindItemFocus(k, spellId) is { } focus ? focus.BenefitAt(level) : string.Empty,
                Save));
        }

        Rows = rows;
        Message = database.ItemFoci.Count == 0
            ? "No focus effects were found. Load your spell file (Settings tab) first; the newer spells_us.txt with its spells_us_str.txt is needed for focus effects."
            : string.Empty;
        _loading = false;
    }

    public IReadOnlyList<FocusTypeRow> Rows { get; }

    public int Level { get; }

    /// <summary>"At level 60", the heading of the benefit column.</summary>
    public string LevelHeading => Level > 0 ? $"At level {Level}" : "Benefit";

    public string Message { get; }

    public bool HasMessage => Message.Length > 0;

    /// <summary>The learned abilities that lengthen this persona's spells, one line each.</summary>
    public IReadOnlyList<string> DurationAas { get; }

    public string DurationAasText => DurationAas.Count == 0
        ? "None learned yet. Abilities come from the AAs tab (the logs, or ones you add there)."
        : string.Join(Environment.NewLine, DurationAas);

    /// <summary>The buff duration focus (kept for callers and tests).</summary>
    public FocusChoice SelectedBeneficial
    {
        get => Row(FocusKind.BuffDuration).Selected;
        set => Row(FocusKind.BuffDuration).Selected = value;
    }

    public FocusChoice SelectedDetrimental
    {
        get => Row(FocusKind.DetrimentalDuration).Selected;
        set => Row(FocusKind.DetrimentalDuration).Selected = value;
    }

    public DurationProfile Profile => new(
        SelectedBeneficial?.SpellId, SelectedDetrimental?.SpellId, 0,
        Foci: Rows.Where(r => r.Kind is not (FocusKind.BuffDuration or FocusKind.DetrimentalDuration) && r.Selected.SpellId is not null)
            .ToDictionary(r => r.Kind, r => r.Selected.SpellId!.Value),
        FromItems: Rows.Where(r => r.FromItem.Length > 0).ToDictionary(r => r.Kind, r => r.FromItem) is { Count: > 0 } items ? items : null,
        AppliedItems: _applied);

    private readonly IReadOnlyCollection<string>? _applied;

    private FocusTypeRow Row(FocusKind kind) => Rows.First(r => r.Kind == kind);

    private void Save()
    {
        if (!_loading)
        {
            _save(Profile);
        }
    }
}
