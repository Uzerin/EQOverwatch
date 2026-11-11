using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Roster;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

/// <summary>A verified player in the roster list.</summary>
/// <param name="Class">Full class name, or empty when only the name is known.</param>
/// <param name="Pets">The player's known pets, comma-separated.</param>
public sealed record RosterPlayerRow(string Name, string Class, string Pets, int Level = 0, string Server = "")
{
    public string LevelText => Level > 0 ? Level.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
}

/// <summary>A pet and its owner in the roster list.</summary>
public sealed record RosterPetRow(string Pet, string Owner, string Server = "");

/// <summary>The verified players and pets: the lists on the Players &amp; Pets tab and the pet-damage switch.</summary>
public sealed partial class MainViewModel
{
    private int _rosterDirty;

    public ObservableCollection<RosterPlayerRow> RosterPlayers { get; } = new();
    public ObservableCollection<RosterPetRow> RosterPets { get; } = new();

    [ObservableProperty]
    private string _rosterSummary = string.Empty;

    /// <summary>Narrows the players list to names that contain this text (capitals do not matter).</summary>
    [ObservableProperty]
    private string _playerSearch = string.Empty;

    partial void OnPlayerSearchChanged(string value) => SyncRoster();

    /// <summary>Count a known pet's damage under its owner instead of giving the pet its own row.</summary>
    [ObservableProperty]
    private bool _mergePetDamage = true;

    partial void OnMergePetDamageChanged(bool value)
    {
        _pipeline.Roster.MergePetDamage = value;
        RequestSave();
    }

    /// <summary>The Class column for a fight's participant, looked up on the server of the log that recorded the fight.</summary>
    private string ClassLabelOnServer(string name, string server) => _pipeline.Roster.GetClassLabel(name, server);

    /// <summary>The roster changed on a pipeline thread: mark it, and let the UI timer refresh the lists and save.</summary>
    private void OnRosterChanged() => Interlocked.Exchange(ref _rosterDirty, 1);

    private void RestoreRoster(AppSettings settings)
    {
        _pipeline.Roster.Load(settings.Roster);
        MergePetDamage = _pipeline.Roster.MergePetDamage;
        SyncRoster();
        RefreshCharacterInfo();
    }

    /// <summary>Called from <see cref="Refresh"/> on the UI thread.</summary>
    private void DrainRosterChanges()
    {
        if (Interlocked.Exchange(ref _rosterDirty, 0) == 0)
        {
            return;
        }

        SyncRoster();
        RefreshCharacterInfo();
        RequestSave();
        RefreshParticipants(); // a class learned now shows on the selected fight
    }

    private void SyncRoster()
    {
        RosterSettings data = _pipeline.Roster.ToSettings();
        Dictionary<string, string> petsByOwner = data.Pets
            .GroupBy(p => OwnerKey(p.Server, p.Owner), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(p => p.Pet).OrderBy(n => n, StringComparer.Ordinal)), StringComparer.OrdinalIgnoreCase);

        string search = PlayerSearch?.Trim() ?? string.Empty;
        RosterPlayers.Clear();
        foreach (RosterPlayerEntry player in data.Players.Where(p => search.Length == 0 || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            RosterPlayers.Add(new RosterPlayerRow(
                player.Name, player.Class, petsByOwner.GetValueOrDefault(OwnerKey(player.Server, player.Name), string.Empty), player.Level, player.Server));
        }

        RosterPets.Clear();
        foreach (RosterPetEntry pet in data.Pets)
        {
            RosterPets.Add(new RosterPetRow(pet.Pet, pet.Owner, pet.Server));
        }

        RosterSummary = $"{data.Players.Count:N0} verified player(s), {data.Pets.Count:N0} pet(s)." +
                        (search.Length > 0 ? $" Showing {RosterPlayers.Count:N0} player(s) that match \"{search}\"." : string.Empty);
    }

    private static string OwnerKey(string server, string owner) => $"{server}|{owner}";

    [RelayCommand]
    private void ForgetPlayer(RosterPlayerRow? row)
    {
        if (row is not null && _pipeline.Roster.ForgetPlayer(row.Name, row.Server))
        {
            DrainRosterChanges();
        }
    }

    [RelayCommand]
    private void ForgetPet(RosterPetRow? row)
    {
        if (row is null
            || !_dialogs.Confirm("Forget this pet?", $"Forget {row.Pet}, the pet of {row.Owner}? It is learned again the next time a /pet leader line names it."))
        {
            return;
        }

        if (_pipeline.Roster.ForgetPet(row.Pet, row.Server))
        {
            DrainRosterChanges();
        }
    }

    [RelayCommand]
    private void ForgetAllPets()
    {
        if (RosterPets.Count == 0
            || !_dialogs.Confirm("Forget all pets?", $"Forget all {RosterPets.Count:N0} pet(s)? They are learned again from /pet leader lines. Players are not affected."))
        {
            return;
        }

        _pipeline.Roster.ForgetAllPets();
        DrainRosterChanges();
    }

    [RelayCommand]
    private void ClearRoster()
    {
        _pipeline.Roster.Clear();
        DrainRosterChanges();
    }
}
