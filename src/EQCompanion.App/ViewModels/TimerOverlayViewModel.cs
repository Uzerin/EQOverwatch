using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EQCompanion.App.Presentation;
using EQCompanion.Core.Models;
using EQCompanion.Core.Services;

namespace EQCompanion.App.ViewModels;

/// <summary>One text alert in the flash banner.</summary>
public sealed partial class AlertViewModel : ObservableObject
{
    public AlertViewModel(long id, string text)
    {
        Id = id;
        _text = text;
    }

    public long Id { get; }

    [ObservableProperty]
    private string _text;
}

/// <summary>One countdown bar. <see cref="Fraction"/> is 1 when it starts and drains to 0.</summary>
public sealed partial class TimerBarViewModel : ObservableObject
{
    public const double UrgentThresholdSeconds = 3.0;

    public TimerBarViewModel(long id)
    {
        Id = id;
    }

    public long Id { get; }

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    [ObservableProperty]
    private double _fraction = 1.0;

    [ObservableProperty]
    private bool _isUrgent;

    /// <summary>Picks the bar color (the theme's Buffs / Debuffs / Songs / Raid Triggers color).</summary>
    [ObservableProperty]
    private TimerOverlayCategory _category;

    public void Update(ActiveTimerSnapshot snapshot, DateTime nowUtc)
    {
        Category = snapshot.Category;
        double remaining = snapshot.RemainingSeconds(nowUtc);
        Label = snapshot.Text;
        RemainingText = OverlayFormat.Remaining(remaining);
        Fraction = snapshot.FractionRemaining(nowUtc);
        IsUrgent = remaining <= UrgentThresholdSeconds;
    }
}

/// <summary>
/// Drives one timer overlay. <see cref="Tick"/> reads <c>TriggerSnapshot.GetActive(utcNow)</c>, keeps only the overlay's own
/// <see cref="TimerOverlayCategory"/> (all of them when no category is given), and updates two stacked regions:
/// a flash banner of recent text alerts and a list of draining countdown bars. View models are reused between ticks.
/// </summary>
public sealed class TimerOverlayViewModel : ObservableObject
{
    /// <summary>A text alert is shown for this long (or its own duration, if that is shorter).</summary>
    public const double AlertDisplaySeconds = 4.0;

    private readonly Func<TriggerSnapshot> _source;
    private readonly Func<DateTime> _utcNow;
    private readonly TimerOverlayCategory? _category;

    public TimerOverlayViewModel(TriggerEngineService engine, TimerOverlayCategory? category = null, Func<DateTime>? utcNow = null)
        : this(() => engine.Snapshot, utcNow, category)
    {
    }

    public TimerOverlayViewModel(Func<TriggerSnapshot> source, Func<DateTime>? utcNow = null, TimerOverlayCategory? category = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _category = category;
    }

    /// <summary>The overlay this view model feeds, or null when it shows every category.</summary>
    public TimerOverlayCategory? Category => _category;

    /// <summary>Newest alert first.</summary>
    public ObservableCollection<AlertViewModel> Alerts { get; } = new();

    /// <summary>Soonest-expiring bar first.</summary>
    public ObservableCollection<TimerBarViewModel> Bars { get; } = new();

    /// <summary>Call from the overlay's 50ms dispatcher timer.</summary>
    public void Tick()
    {
        DateTime now = _utcNow();
        IReadOnlyList<ActiveTimerSnapshot> active = _category is { } category
            ? _source().GetActive(now, category)
            : _source().GetActive(now);

        ActiveTimerSnapshot[] alerts = active
            .Where(t => t.Kind == TimerKind.TextAlert
                        && (now - t.StartedUtc).TotalSeconds < Math.Min(AlertDisplaySeconds, t.DurationSeconds))
            .OrderByDescending(t => t.StartedUtc)
            .ThenByDescending(t => t.Id)
            .ToArray();

        ActiveTimerSnapshot[] bars = active
            .Where(t => t.Kind == TimerKind.Countdown)
            .OrderBy(t => t.ExpiresUtc)
            .ThenBy(t => t.Id)
            .ToArray();

        ObservableSync.Sync(
            Alerts, alerts,
            snapshotKey: s => s.Id,
            viewModelKey: vm => vm.Id,
            create: s => new AlertViewModel(s.Id, s.Text),
            update: (vm, s) => vm.Text = s.Text);

        ObservableSync.Sync(
            Bars, bars,
            snapshotKey: s => s.Id,
            viewModelKey: vm => vm.Id,
            create: s => new TimerBarViewModel(s.Id),
            update: (vm, s) => vm.Update(s, now));
    }
}
