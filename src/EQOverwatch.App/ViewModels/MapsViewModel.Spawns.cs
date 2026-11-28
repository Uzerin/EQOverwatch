using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Maps;

namespace EQOverwatch.App.ViewModels;

/// <summary>A row of Possible Locations: one mob at one spot, or one mob that spawns at random spots.</summary>
public sealed partial class SpawnRowView : ObservableObject
{
    private readonly Action<SpawnRowView, bool> _verifiedChanged;
    private bool _verified;

    public SpawnRowView(string zone, SpawnMob mob, SpawnSpot? spot, bool verified, Action<SpawnRowView, bool> verifiedChanged)
    {
        Zone = zone;
        Mob = mob;
        Spot = spot;
        _verified = verified;
        _verifiedChanged = verifiedChanged;
    }

    public string Zone { get; }

    public SpawnMob Mob { get; }

    public SpawnSpot? Spot { get; }

    public string Name => Mob.Name;

    public string Level => Mob.LevelText;

    public string Location => Spot?.LocText ?? "Random";

    public string Respawn => Mob.RespawnText;

    public bool HasSpot => Spot is not null;

    /// <summary>Ticked by the player when they have seen the mob spawn there (saved on this PC).</summary>
    public bool Verified
    {
        get => _verified;
        set
        {
            if (SetProperty(ref _verified, value))
            {
                _verifiedChanged(this, value);
            }
        }
    }
}

// Maps › Possible Locations: where each zone's mobs can spawn.
public sealed partial class MapsViewModel
{
    private readonly VerifiedSpawnStore _verified = new();
    private SpawnZone? _spawnZone;
    private string _spawnSearch = string.Empty;
    private bool _namedOnly;
    private bool _spawnZoneChosen;

    /// <summary>Copies text to the clipboard; replaced in tests.</summary>
    public Action<string> CopyText { get; set; } = text =>
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // the clipboard was busy
        }
    };

    public IReadOnlyList<SpawnZone> SpawnZones => SpawnLocations.Zones;

    public ObservableCollection<SpawnRowView> SpawnRows { get; } = new();

    [ObservableProperty]
    private string _spawnStatus = string.Empty;

    /// <summary>The Possible Locations sub-tab is showing.</summary>
    public bool IsSpawnsTab
    {
        get => SelectedSubTab == 2;
        set
        {
            if (value)
            {
                SelectedSubTab = 2;
            }
        }
    }

    /// <summary>The map and its label editor show on the first two sub-tabs.</summary>
    public bool IsMapTab => SelectedSubTab != 2;

    public void SetVerifiedPath(string path) => _verified.SetStoragePath(path);

    public SpawnZone? SpawnZone
    {
        get => _spawnZone;
        set
        {
            if (value is null || Equals(value, _spawnZone))
            {
                return;
            }

            _spawnZone = value;
            _spawnZoneChosen = true;
            OnPropertyChanged();
            BuildSpawnRows();
        }
    }

    public string SpawnSearch
    {
        get => _spawnSearch;
        set
        {
            if (SetProperty(ref _spawnSearch, value ?? string.Empty))
            {
                BuildSpawnRows();
            }
        }
    }

    public bool NamedOnly
    {
        get => _namedOnly;
        set
        {
            if (SetProperty(ref _namedOnly, value))
            {
                BuildSpawnRows();
            }
        }
    }

    /// <summary>Opening the sub-tab shows the zone the character is in, until another zone is picked.</summary>
    private void OpenSpawnsTab()
    {
        if (!_spawnZoneChosen && SpawnLocations.Find(CurrentZone()) is { } here)
        {
            _spawnZone = here;
            OnPropertyChanged(nameof(SpawnZone));
        }

        _spawnZone ??= SpawnZones.FirstOrDefault();
        OnPropertyChanged(nameof(SpawnZone));
        BuildSpawnRows();
    }

    private void BuildSpawnRows()
    {
        SpawnRows.Clear();
        if (_spawnZone is not { } zone)
        {
            SpawnStatus = string.Empty;
            return;
        }

        string search = SpawnSearch.Trim();
        int spots = 0;
        foreach (SpawnMob mob in zone.Mobs)
        {
            if ((NamedOnly && !mob.IsNamed) || (search.Length > 0 && !mob.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (mob.Spots is null)
            {
                SpawnRows.Add(new SpawnRowView(zone.ShortName, mob, null, false, OnVerifiedChanged));
                continue;
            }

            foreach (SpawnSpot spot in mob.Spots)
            {
                SpawnRows.Add(new SpawnRowView(zone.ShortName, mob, spot, _verified.IsVerified(zone.ShortName, mob.Name, spot), OnVerifiedChanged));
                spots++;
            }
        }

        string kind = zone.IsOutdoor
            ? "Outdoor zone: most mobs spawn at random spots; named mobs keep their fixed spots."
            : "Mobs here spawn at fixed spots.";
        SpawnStatus = $"{SpawnRows.Count:N0} rows · {spots:N0} spots. {kind} Live servers may differ.";
    }

    private void OnVerifiedChanged(SpawnRowView row, bool verified)
    {
        if (row.Spot is { } spot)
        {
            _verified.Set(row.Zone, row.Name, spot, verified);
        }
    }

    [RelayCommand]
    private void CopyWaypoint(SpawnRowView? row)
    {
        if (row?.Spot is { } spot)
        {
            CopyText(spot.Waypoint);
            SpawnStatus = $"Copied {spot.Waypoint}. Paste it in game.";
        }
    }

    /// <summary>Opens the zone's map with the spot marked, ready for Add label.</summary>
    [RelayCommand]
    private void ShowSpawnOnMap(SpawnRowView? row)
    {
        if (row?.Spot is not { } spot)
        {
            return;
        }

        if (!OpenZone(row.Zone))
        {
            IsSpawnsTab = true;
            SpawnStatus = "The selected map pack has no map for this zone.";
            return;
        }

        PlaceAt(-spot.X, -spot.Y);
        Sync(() => EditLoc = spot.LocText);
        EditText = row.Name;
    }
}
