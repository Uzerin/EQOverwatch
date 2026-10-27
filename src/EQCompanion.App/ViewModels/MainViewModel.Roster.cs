using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQCompanion.Core.Roster;
using EQCompanion.Core.Settings;

namespace EQCompanion.App.ViewModels;

/// <summary>A verified player in the roster list.</summary>
/// <param name="Class">Full class name, or empty when only the name is known.</param>
/// <param name="Pets">The player's known pets, comma-separated.</param>
public sealed record RosterPlayerRow(string Name, string Class, string Pets);

/// <summary>A pet and its owner in the roster list.</summary>
public sealed record RosterPetRow(string Pet, string Owner);

/// <summary>The verified players and pets: the lists in Settings and the pet-damage switch.</summary>
public sealed partial class MainViewModel
{
    private int _rosterDirty;

    public ObservableCollection<RosterPlayerRow> RosterPlayers { get; } = new();
    public ObservableCollection<RosterPetRow> RosterPets { get; } = new();

    [ObservableProperty]
    private string _rosterSummary = string.Empty;

    /// <summary>Count a known pet's damage under its owner instead of giving the pet its own row.</summary>
    [ObservableProperty]
    private bool _mergePetDamage = true;

    partial void OnMergePetDamageChanged(bool value)
    {
        _pipeline.Roster.MergePetDamage = value;
        RequestSave();
    }

    /// <summary>The roster changed on a pipeline thread: mark it, and let the UI timer refresh the lists and save.</summary>
    private void OnRosterChanged() => Interlocked.Exchange(ref _rosterDirty, 1);

    private void RestoreRoster(AppSettings settings)
    {
        _pipeline.Roster.Load(settings.Roster);
        MergePetDamage = _pipeline.Roster.MergePetDamage;
        SyncRoster();
    }

    /// <summary>Called from <see cref="Refresh"/> on the UI thread.</summary>
    private void DrainRosterChanges()
    {
        if (Interlocked.Exchange(ref _rosterDirty, 0) == 0)
        {
            return;
        }

        SyncRoster();
        RequestSave();
        RefreshParticipants(); // a class learned now shows on the selected fight
    }

    private void SyncRoster()
    {
        RosterSettings data = _pipeline.Roster.ToSettings();
        Dictionary<string, string> petsByOwner = data.Pets
            .GroupBy(p => p.Owner, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(p => p.Pet).OrderBy(n => n, StringComparer.Ordinal)), StringComparer.Ordinal);

        RosterPlayers.Clear();
        foreach (RosterPlayerEntry player in data.Players)
        {
            RosterPlayers.Add(new RosterPlayerRow(player.Name, player.Class, petsByOwner.GetValueOrDefault(player.Name, string.Empty)));
        }

        RosterPets.Clear();
        foreach (RosterPetEntry pet in data.Pets)
        {
            RosterPets.Add(new RosterPetRow(pet.Pet, pet.Owner));
        }

        RosterSummary = $"{data.Players.Count:N0} verified player(s), {data.Pets.Count:N0} pet(s).";
    }

    [RelayCommand]
    private void ForgetPlayer(RosterPlayerRow? row)
    {
        if (row is not null && _pipeline.Roster.ForgetPlayer(row.Name))
        {
            DrainRosterChanges();
        }
    }

    [RelayCommand]
    private void ForgetPet(RosterPetRow? row)
    {
        if (row is not null && _pipeline.Roster.ForgetPet(row.Pet))
        {
            DrainRosterChanges();
        }
    }

    [RelayCommand]
    private void ClearRoster()
    {
        _pipeline.Roster.Clear();
        DrainRosterChanges();
    }
}
