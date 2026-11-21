using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>One hit or heal in the Damage Taken tab's "Last 10 Seconds" list.</summary>
public sealed record LastSecondsRowView(string Time, string Seconds, string From, string What, string Damage, string Healed, bool IsHeal);

public partial class MainViewModel
{
    private IReadOnlyList<LastSecondsSnapshot> _lastSecondsWindows = Array.Empty<LastSecondsSnapshot>();
    private string? _lastSecondsName;

    /// <summary>The row picked in the Damage Taken summary; its last seconds show in the "Last 10 Seconds" sub-tab.</summary>
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

    private void ShowLastSeconds(EncounterSnapshot? encounter)
    {
        _lastSecondsWindows = encounter?.LastSeconds ?? Array.Empty<LastSecondsSnapshot>();
        RebuildLastSeconds();
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
            rows.Add(new LastSecondsRowView(e.Time.ToString("HH:mm:ss", culture), seconds.ToString(culture), e.Source, e.What,
                e.IsHeal ? string.Empty : amount, e.IsHeal ? amount : string.Empty, e.IsHeal));
        }

        if (window.Died)
        {
            rows.Add(new LastSecondsRowView(window.End.ToString("HH:mm:ss", culture), "0", window.Name, "died", string.Empty, string.Empty, false));
        }

        LastSecondsRows = rows;
    }
}
