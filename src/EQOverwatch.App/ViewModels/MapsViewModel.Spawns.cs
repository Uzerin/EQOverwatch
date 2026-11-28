using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Maps;

namespace EQOverwatch.App.ViewModels;

/// <summary>A spawn spot drawn on the map by Show all on map; named mobs carry their name.</summary>
public sealed record MapSpawnDot(double X, double Y, string Label);

/// <summary>A row of Possible Locations: one mob at one spot, or one mob that spawns at random spots.</summary>
public sealed partial class SpawnRowView : ObservableObject
{
    private readonly Action<SpawnRowView, bool> _verifiedChanged;
    private readonly Action<SpawnRowView, bool> _namedChanged;
    private bool _verified;
    private bool _named;

    public SpawnRowView(string zone, SpawnMob mob, SpawnSpot? spot, bool verified, bool named, ConColor con,
        Action<SpawnRowView, bool> verifiedChanged, Action<SpawnRowView, bool> namedChanged)
    {
        Zone = zone;
        Mob = mob;
        Spot = spot;
        Con = con;
        _verified = verified;
        _named = named;
        _verifiedChanged = verifiedChanged;
        _namedChanged = namedChanged;
    }

    public string Zone { get; }

    public SpawnMob Mob { get; }

    public SpawnSpot? Spot { get; }

    /// <summary>How the mob cons to the character played last (None when its level is not known).</summary>
    public ConColor Con { get; }

    public string Name => Mob.Name;

    public string Level => Mob.LevelText;

    public string Location => Spot?.LocText ?? "Random";

    public string Respawn => Mob.RespawnText;

    public string Chance => Spot?.ChanceText ?? string.Empty;

    /// <summary>The placeholders: the other mobs that spawn at a named mob's spot.</summary>
    public string SharesWith => Named && Spot is { } spot ? spot.SharesWithText : string.Empty;

    public bool HasSpot => Spot is not null;

    /// <summary>Ticked for known named mobs; the player can change it (saved on this PC).</summary>
    public bool Named
    {
        get => _named;
        set
        {
            if (SetProperty(ref _named, value))
            {
                OnPropertyChanged(nameof(SharesWith));
                _namedChanged(this, value);
            }
        }
    }

    /// <summary>Changed with the other rows of the same mob, without saving again.</summary>
    internal void ShowNamed(bool value)
    {
        if (SetProperty(ref _named, value, nameof(Named)))
        {
            OnPropertyChanged(nameof(SharesWith));
        }
    }

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
    private readonly NamedSpawnStore _named = new();
    private SpawnZone? _spawnZone;
    private string _spawnSearch = string.Empty;
    private bool _namedOnly;
    private bool _spawnZoneChosen;
    private string? _spawnDotsZone;

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

    /// <summary>The level of the character played last, for con colors (0 when not known).</summary>
    public Func<int> PlayerLevel { get; set; } = () => 0;

    public IReadOnlyList<SpawnZone> SpawnZones => SpawnLocations.Zones;

    public ObservableCollection<SpawnRowView> SpawnRows { get; } = new();

    /// <summary>The spots Show all on map put on the map, while that zone is shown.</summary>
    public IReadOnlyList<MapSpawnDot> SpawnDots { get; private set; } = Array.Empty<MapSpawnDot>();

    public IReadOnlyList<MapSpawnDot> VisibleSpawnDots =>
        ShownZone is not null && string.Equals(ShownZone, _spawnDotsZone, StringComparison.OrdinalIgnoreCase) ? SpawnDots : Array.Empty<MapSpawnDot>();

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

    public void SetNamedPath(string path) => _named.SetStoragePath(path);

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
        }

        _spawnZone ??= SpawnZones.FirstOrDefault();
        OnPropertyChanged(nameof(SpawnZone));
        BuildSpawnRows();
    }

    /// <summary>Opens a mob's zone on Possible Locations, searched for that mob (from Ctrl+F).</summary>
    public bool OpenSpawn(string zone, string mob)
    {
        if (SpawnLocations.Find(zone) is not { } found)
        {
            return false;
        }

        _spawnZone = found;
        _spawnZoneChosen = true;
        _spawnSearch = mob;
        OnPropertyChanged(nameof(SpawnZone));
        OnPropertyChanged(nameof(SpawnSearch));
        if (IsSpawnsTab)
        {
            BuildSpawnRows();
        }
        else
        {
            IsSpawnsTab = true;
        }

        return IsSpawnsTab;
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
        int level = PlayerLevel();
        int spots = 0;
        foreach (SpawnMob mob in zone.Mobs)
        {
            bool named = _named.IsNamed(zone.ShortName, mob);
            if ((NamedOnly && !named) || (search.Length > 0 && !mob.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            ConColor con = Cons.For(level, mob.MinLevel);
            if (mob.Spots is null)
            {
                SpawnRows.Add(new SpawnRowView(zone.ShortName, mob, null, false, named, con, OnVerifiedChanged, OnNamedChanged));
                continue;
            }

            foreach (SpawnSpot spot in mob.Spots)
            {
                SpawnRows.Add(new SpawnRowView(zone.ShortName, mob, spot, _verified.IsVerified(zone.ShortName, mob.Name, spot), named, con,
                    OnVerifiedChanged, OnNamedChanged));
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

    private void OnNamedChanged(SpawnRowView row, bool named)
    {
        _named.Set(row.Zone, row.Mob, named);
        foreach (SpawnRowView other in SpawnRows.Where(r => !ReferenceEquals(r, row) && r.Mob == row.Mob))
        {
            other.ShowNamed(named);
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

    /// <summary>Draws every spot of the zone on its map (named mobs labeled), in the app only.</summary>
    [RelayCommand]
    private void ShowAllOnMap()
    {
        if (_spawnZone is not { } zone)
        {
            return;
        }

        var dots = new List<MapSpawnDot>();
        foreach (SpawnMob mob in zone.Mobs.Where(m => m.Spots is not null))
        {
            string label = _named.IsNamed(zone.ShortName, mob) ? mob.Name : string.Empty;
            dots.AddRange(mob.Spots!.Select(s => new MapSpawnDot(-s.X, -s.Y, label)));
        }

        if (!OpenZone(zone.ShortName))
        {
            IsSpawnsTab = true;
            SpawnStatus = "The selected map pack has no map for this zone.";
            return;
        }

        SpawnDots = dots;
        _spawnDotsZone = zone.ShortName;
        StatusText = $"{dots.Count:N0} spawn spots shown. They are not saved to the map files.";
        RaiseScene();
    }
}
