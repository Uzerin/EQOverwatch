using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.ViewModels;

/// <summary>One line of the By player list: a player (bold, with their total) or one of that player's spells beneath.</summary>
public sealed record SpellCastDisplayRow(bool IsPlayer, string Name, int Casts, string PerMinute, string First, string Last, int Failed);

/// <summary>One line of the By spell list: a spell across the whole raid.</summary>
public sealed record SpellTotalDisplayRow(string Spell, int Casts, int Casters, string PerMinute, string TopCaster, int Failed);

public sealed partial class MainViewModel
{
    private EncounterSnapshot? _castEncounter;
    private SpellCastReport? _castReport;
    private (int Id, int Casts, bool Active, string Filter) _castKey = (-1, -1, false, string.Empty);

    [ObservableProperty]
    private string _spellCastFilter = string.Empty;

    [ObservableProperty]
    private string _spellCastSummary = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<SpellCastDisplayRow> _spellCastPlayerRows = Array.Empty<SpellCastDisplayRow>();

    [ObservableProperty]
    private IReadOnlyList<SpellTotalDisplayRow> _spellCastTotalRows = Array.Empty<SpellTotalDisplayRow>();

    [ObservableProperty]
    private IReadOnlyList<CastLane> _spellCastLanes = Array.Empty<CastLane>();

    [ObservableProperty]
    private double _spellCastDuration = 1.0;

    partial void OnSpellCastFilterChanged(string value) => UpdateSpellCasts(_castEncounter);

    /// <summary>Rebuilds the Spell Casts tab for the selected fight; does nothing when neither the fight's casts nor the filter changed.</summary>
    private void UpdateSpellCasts(EncounterSnapshot? encounter)
    {
        _castEncounter = encounter;
        if (encounter is null)
        {
            _castKey = (-1, -1, false, string.Empty);
            _castReport = null;
            SpellCastPlayerRows = Array.Empty<SpellCastDisplayRow>();
            SpellCastTotalRows = Array.Empty<SpellTotalDisplayRow>();
            SpellCastLanes = Array.Empty<CastLane>();
            SpellCastSummary = string.Empty;
            return;
        }

        string filter = SpellCastFilter?.Trim() ?? string.Empty;
        var key = (encounter.Id, encounter.PlayerCastRows.Count, encounter.IsActive, filter);
        if (key == _castKey)
        {
            return;
        }

        _castKey = key;
        SpellCastReport report = SpellCastReport.Build(encounter, filter);
        _castReport = report;

        var rows = new List<SpellCastDisplayRow>();
        foreach (PlayerCastRow player in report.Players)
        {
            rows.Add(new SpellCastDisplayRow(true, player.Name, player.Casts, PerMinute(player.CastsPerMinute), string.Empty, string.Empty, player.Failed));
            foreach (PlayerSpellRow spell in player.Spells)
            {
                rows.Add(new SpellCastDisplayRow(
                    false, "     " + spell.Spell, spell.Casts, PerMinute(spell.CastsPerMinute),
                    SpellCastReport.FormatOffset(spell.FirstOffsetSeconds), SpellCastReport.FormatOffset(spell.LastOffsetSeconds), spell.Failed));
            }
        }

        SpellCastPlayerRows = rows;
        SpellCastTotalRows = report.Spells
            .Select(t => new SpellTotalDisplayRow(t.Spell, t.Casts, t.Casters, PerMinute(t.CastsPerMinute), $"{t.TopCaster} ({t.TopCasterCasts})", t.Failed))
            .ToArray();
        SpellCastLanes = report.Lanes;
        SpellCastDuration = report.DurationSeconds;

        SpellCastSummary = report.TotalCasts == 0
            ? (filter.Length == 0
                ? "No player spell casts were seen in this fight. Casts of other players only show in your log when they are in range."
                : $"No casts matching \"{filter}\" were seen in this fight.")
            : $"{report.TotalCasts:N0} casts by {report.Players.Count} players, {report.Spells.Count} different spells.";
    }

    private static string PerMinute(double value) => value.ToString("0.0", CultureInfo.CurrentCulture);

    [RelayCommand]
    private void CopySpellCasts()
    {
        if (_castReport is null || _castEncounter is null || _castReport.TotalCasts == 0)
        {
            StatusMessage = "There are no spell casts to copy for this fight.";
            return;
        }

        try
        {
            _clipboard.SetText(_castReport.BuildText(_castEncounter.TargetName));
            StatusMessage = $"Copied the spell casts for {_castEncounter.TargetName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not copy the spell casts: " + ex.Message;
        }
    }
}
