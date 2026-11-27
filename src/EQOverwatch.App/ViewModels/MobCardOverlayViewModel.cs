using CommunityToolkit.Mvvm.ComponentModel;

namespace EQOverwatch.App.ViewModels;

/// <summary>The mob card overlay: the card of the mob last /con'd, shown for <see cref="ShowSeconds"/> seconds.</summary>
public sealed partial class MobCardOverlayViewModel : ObservableObject
{
    /// <summary>How long a card stays up.</summary>
    public const double ShowSeconds = 10;

    private readonly Func<DateTime> _utcNow;
    private DateTime _hideAtUtc;

    public MobCardOverlayViewModel(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    [ObservableProperty]
    private string _heading = "Mob card";

    [ObservableProperty]
    private IReadOnlyList<string> _lines = new[] { "/con a mob to see what the app knows about it." };

    /// <summary>A raid boss's spells (resist type and adjustment, shortest time between casts, casts seen); empty for other mobs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpells))]
    private IReadOnlyList<MobCardSpell> _spells = Array.Empty<MobCardSpell>();

    public bool HasSpells => Spells.Count > 0;

    /// <summary>True while a card is up; the overlay shows nothing otherwise (unless it is unlocked, to move it).</summary>
    [ObservableProperty]
    private bool _hasCard;

    /// <summary>Shows a card (call on the UI thread).</summary>
    public void Show(MobCardInfo card)
    {
        ArgumentNullException.ThrowIfNull(card);
        Heading = card.Heading;
        Lines = card.Lines;
        Spells = card.SpellRows;
        HasCard = true;
        _hideAtUtc = _utcNow().AddSeconds(ShowSeconds);
    }

    /// <summary>Call every second: hides the card once its time is up.</summary>
    public void Tick()
    {
        if (HasCard && _utcNow() >= _hideAtUtc)
        {
            HasCard = false;
        }
    }
}
