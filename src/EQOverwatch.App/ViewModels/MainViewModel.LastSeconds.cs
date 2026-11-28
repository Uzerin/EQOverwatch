using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>One hit, heal or heal cast in the Damage Taken tab's "Last 20 Seconds" list.</summary>
public sealed record LastSecondsRowView(string Time, string Seconds, string From, string What, string Damage, string Healed, bool IsHeal, bool IsCast = false);

/// <summary>One row of the Damage Taken tab's Lost Control list: when, who, the spell and caster (when seen) and how long it lasted.</summary>
public sealed record LostControlRowView(string Time, string Character, string Spell, string Caster, string Lasted);

/// <summary>One turn on the Tank Timeline list.</summary>
public sealed record TankRowView(string Name, string From, string To, string Length, int Swings);

public partial class MainViewModel
{
    private IReadOnlyList<LastSecondsSnapshot> _lastSecondsWindows = Array.Empty<LastSecondsSnapshot>();
    private string? _lastSecondsName;

    [ObservableProperty]
    private IReadOnlyList<TankSpanSnapshot> _tankSpans = Array.Empty<TankSpanSnapshot>();

    [ObservableProperty]
    private double _tankDuration;

    [ObservableProperty]
    private double _tankTimelineHeight = 60;

    [ObservableProperty]
    private IReadOnlyList<TankRowView> _tankRows = Array.Empty<TankRowView>();

    /// <summary>The row picked in the Damage Taken summary; its last seconds show in the "Last 20 Seconds" sub-tab.</summary>
    [ObservableProperty]
    private DamageTakenRowViewModel? _selectedTakenRow;

    [ObservableProperty]
    private IReadOnlyList<LastSecondsRowView> _lastSecondsRows = Array.Empty<LastSecondsRowView>();

    [ObservableProperty]
    private string _lastSecondsTitle = string.Empty;

    partial void OnSelectedTakenRowChanged(DamageTakenRowViewModel? value)
    {
        if (value is not null)
        {
            _lastSecondsName = value.Name;
            RebuildLastSeconds();
        }
    }

    /// <summary>Each time one of your characters lost control of itself in the fight (a charm, fear or mind control).</summary>
    [ObservableProperty]
    private IReadOnlyList<LostControlRowView> _lostControlRows = Array.Empty<LostControlRowView>();

    [ObservableProperty]
    private string _lostControlSummary = string.Empty;

    private ValueList<LostControlSnapshot>? _lostControlShown;

    /// <summary>The Lost Control rows and the line above them for <paramref name="encounter"/>.</summary>
    public static (IReadOnlyList<LostControlRowView> Rows, string Summary) LostControlView(EncounterSnapshot? encounter, CultureInfo culture)
    {
        if (encounter is null)
        {
            return (Array.Empty<LostControlRowView>(), string.Empty);
        }

        var rows = new List<LostControlRowView>();
        var totals = new Dictionary<string, (int Times, double Seconds)>(StringComparer.OrdinalIgnoreCase);
        foreach (LostControlSnapshot lost in encounter.LostControl)
        {
            DateTime end = lost.End ?? (encounter.EndTime > lost.Start ? encounter.EndTime : lost.Start);
            double seconds = Math.Max(0, (end - lost.Start).TotalSeconds);
            string lasted = Clock(seconds) + (lost.End is null ? "+" : string.Empty);
            rows.Add(new LostControlRowView(lost.Start.ToString("HH:mm:ss", culture), lost.Character, lost.Spell, lost.Caster, lasted));
            (int times, double total) = totals.GetValueOrDefault(lost.Character);
            totals[lost.Character] = (times + 1, total + seconds);
        }

        string summary = totals.Count == 0
            ? "None of your characters lost control of themselves in this fight."
            : string.Join("  ", totals.Select(t =>
                $"{t.Key} lost control {t.Value.Times.ToString(culture)} time{(t.Value.Times == 1 ? string.Empty : "s")} for {Clock(t.Value.Seconds)} in total."));
        return (rows, summary);

        static string Clock(double seconds)
        {
            int s = (int)Math.Round(seconds);
            return $"{s / 60}:{s % 60:00}";
        }
    }

    private void ShowLostControl(EncounterSnapshot? encounter)
    {
        if (encounter is not null && Equals(_lostControlShown, encounter.LostControl) && LostControlSummary.Length > 0)
        {
            return;
        }

        _lostControlShown = encounter?.LostControl;
        (LostControlRows, LostControlSummary) = LostControlView(encounter, CultureInfo.CurrentCulture);
    }

    private void ShowLastSeconds(EncounterSnapshot? encounter)
    {
        ShowLostControl(encounter);
        _lastSecondsWindows = encounter?.LastSeconds ?? Array.Empty<LastSecondsSnapshot>();
        RebuildLastSeconds();
        IReadOnlyList<TankSpanSnapshot> spans = encounter?.TankSpans ?? Array.Empty<TankSpanSnapshot>();
        if (!spans.SequenceEqual(TankSpans) || TankDuration != (encounter?.DurationSeconds ?? 0))
        {
            TankSpans = spans;
            TankDuration = encounter?.DurationSeconds ?? 0;
            TankTimelineHeight = Controls.TankTimelineControl.HeightFor(spans);
            TankRows = spans.Select(t => new TankRowView(t.Name, Clock(t.Start), Clock(t.End), Clock(t.End - t.Start), t.Swings)).ToList();
        }

        static string Clock(double seconds)
        {
            int s = (int)Math.Round(seconds);
            return $"{s / 60}:{s % 60:00}";
        }
    }

    private void RebuildLastSeconds()
    {
        LastSecondsSnapshot? window = _lastSecondsWindows.FirstOrDefault(w => string.Equals(w.Name, _lastSecondsName, StringComparison.Ordinal))
            ?? (DamageTaken.Count > 0 ? _lastSecondsWindows.FirstOrDefault(w => string.Equals(w.Name, DamageTaken[0].Name, StringComparison.Ordinal)) : null);
        if (window is null)
        {
            LastSecondsTitle = DamageTaken.Count == 0 ? string.Empty : "Select a player on the Summary tab.";
            LastSecondsRows = Array.Empty<LastSecondsRowView>();
            return;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        string end = window.Died ? $"before their death at {window.End:HH:mm:ss}" : $"before the end of the fight at {window.End:HH:mm:ss}";
        LastSecondsTitle = $"{window.Name}: the last {CombatEncounter.RecentSeconds} seconds {end}. " +
                           $"Taken {window.Taken.ToString("N0", culture)}, healed {window.Healed.ToString("N0", culture)}.";
        var rows = new List<LastSecondsRowView>();
        foreach (RecentEventSnapshot e in window.Events.OrderBy(e => e.Time))
        {
            int seconds = (int)Math.Round((e.Time - window.End).TotalSeconds);
            string amount = e.Amount.ToString("N0", culture);
            string time = e.Time.ToString("HH:mm:ss", culture);
            rows.Add(e.IsCast
                ? new LastSecondsRowView(time, seconds.ToString(culture), e.Source, "casting " + e.What, string.Empty, string.Empty, false, true)
                : new LastSecondsRowView(time, seconds.ToString(culture), e.Source, e.What,
                    e.IsHeal ? string.Empty : amount, e.IsHeal ? amount : string.Empty, e.IsHeal));
        }

        if (window.Died)
        {
            rows.Add(new LastSecondsRowView(window.End.ToString("HH:mm:ss", culture), "0", window.Name, "died", string.Empty, string.Empty, false));
        }

        LastSecondsRows = rows;
    }
}
