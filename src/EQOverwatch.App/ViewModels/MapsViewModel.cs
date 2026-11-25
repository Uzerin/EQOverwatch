using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>An entry of the Expansion drop-down: All (null), an expansion number, or Other (0).</summary>
public sealed record MapExpansionChoice(int? Number, string Label);

/// <summary>An entry of a Zone drop-down: a zone with a map file in the chosen pack.</summary>
public sealed record MapZoneChoice(string ShortName, string Label, int Expansion);

/// <summary>An entry of the Size drop-down.</summary>
public sealed record MapLabelSizeChoice(int Size, string Label);

/// <summary>A label on the layer being edited.</summary>
public sealed partial class MapLabelItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocText))]
    private double _x;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocText))]
    private double _y;

    [ObservableProperty]
    private double _z;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _hex = "#FFFFFF";

    [ObservableProperty]
    private int _size = 2;

    /// <summary>The spot as /loc numbers (Y, X), as EQ prints them.</summary>
    public string LocText => MapsViewModel.FormatLoc(-Y, -X, null);

    public static MapLabelItem From(MapLabel label) => new()
    {
        X = label.X,
        Y = label.Y,
        Z = label.Z,
        Text = label.Text,
        Hex = $"#{label.R:X2}{label.G:X2}{label.B:X2}",
        Size = Math.Clamp(label.Size, 1, 3),
    };

    public MapLabel ToLabel()
    {
        (byte r, byte g, byte b) = MapsViewModel.ParseHex(Hex);
        return new MapLabel(X, Y, Z, r, g, b, Size, Text);
    }
}

/// <summary>A character's last known /loc on the map shown (map coordinates).</summary>
public sealed record MapPlayerSpot(string Name, double X, double Y);

/// <summary>
/// The Maps tab: shows a zone's map (the zone of the character you played last, or one picked from the list), and adds, moves, edits
/// and deletes the labels of one layer file in the EQ folder's map pack. Only labels change; the map's lines are never touched.
/// </summary>
public sealed partial class MapsViewModel : ObservableObject
{
    public const int DefaultLayer = 3;

    private readonly Func<string> _eqFolder;
    private readonly ZoneTracker _zones;
    private readonly Func<IEnumerable<WatchedLogConfig>> _logs;
    private readonly IDialogService _dialogs;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, string> _chosenCandidate = new(StringComparer.OrdinalIgnoreCase);

    private ZoneDirectory _directory = ZoneDirectory.Empty;
    private HashSet<string> _zonesInPack = new(StringComparer.OrdinalIgnoreCase);
    private MapLayer[] _layers = EmptyLayers();
    private string _loadedFolder = string.Empty;
    private long _zoneVersionSeen = -1;
    private bool _syncingForm;
    private int _selectedSubTab;
    private MapPack? _selectedPack;
    private MapExpansionChoice? _selectedExpansion;
    private MapZoneChoice? _selectedZone;
    private MapZoneChoice? _selectedCurrentChoice;
    private int _selectedLayer = DefaultLayer;

    public MapsViewModel(
        Func<string> eqFolder,
        ZoneTracker zones,
        Func<IEnumerable<WatchedLogConfig>> logs,
        IDialogService dialogs,
        Action<Action>? post = null)
    {
        _eqFolder = eqFolder;
        _zones = zones;
        _logs = logs;
        _dialogs = dialogs;
        _post = post ?? (a => a());

        Expansions.Add(new MapExpansionChoice(null, "All"));
        for (int i = 1; i <= ZoneDirectory.ExpansionNames.Count; i++)
        {
            Expansions.Add(new MapExpansionChoice(i, ZoneDirectory.ExpansionNames[i - 1]));
        }

        Expansions.Add(new MapExpansionChoice(0, "Other"));
        _selectedExpansion = Expansions[0];
    }

    /// <summary>Raised when anything drawn on the map changes.</summary>
    public event EventHandler? SceneChanged;

    public static IReadOnlyList<string> LayerNames { get; } = new[] { "Base", "1", "2", "3" };

    public static IReadOnlyList<MapLabelSizeChoice> Sizes { get; } =
        new[] { new MapLabelSizeChoice(1, "Small"), new MapLabelSizeChoice(2, "Medium"), new MapLabelSizeChoice(3, "Large") };

    public ObservableCollection<MapPack> Packs { get; } = new();

    public ObservableCollection<MapExpansionChoice> Expansions { get; } = new();

    public ObservableCollection<MapZoneChoice> ZoneChoices { get; } = new();

    /// <summary>When the followed character's zone name fits more than one map (a zone and its later version), the maps to pick from.</summary>
    public ObservableCollection<MapZoneChoice> CurrentChoices { get; } = new();

    /// <summary>The labels of the layer being edited.</summary>
    public ObservableCollection<MapLabelItem> Labels { get; } = new();

    /// <summary>The zone, base map and layers drawn on the map (layer 0 is the base map).</summary>
    public IReadOnlyList<MapLayer> Layers => _layers;

    /// <summary>The short name of the zone shown, or null.</summary>
    public string? ShownZone { get; private set; }

    /// <summary>Where a new label will go (map coordinates), or null.</summary>
    public (double X, double Y)? Pending { get; private set; }

    /// <summary>Each character's last known /loc in the zone shown (map coordinates), with its name; characters with none there are left out.</summary>
    public IReadOnlyList<MapPlayerSpot> PlayerSpots { get; private set; } = Array.Empty<MapPlayerSpot>();

    [ObservableProperty]
    private string _currentZoneText = string.Empty;

    [ObservableProperty]
    private string _followNote = string.Empty;

    [ObservableProperty]
    private string _layerFileText = string.Empty;

    [ObservableProperty]
    private string _labelListTitle = "Labels";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle))]
    private MapLabelItem? _selectedLabel;

    [ObservableProperty]
    private string _editText = string.Empty;

    [ObservableProperty]
    private string _editLoc = string.Empty;

    [ObservableProperty]
    private string _editColorHex = "#85B7EB";

    [ObservableProperty]
    private MapLabelSizeChoice _editSize = Sizes[1];

    [ObservableProperty]
    private string _errorText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private bool _isDirty;

    [ObservableProperty]
    private string _emptyText = string.Empty;

    public bool IsEditing => SelectedLabel is not null;

    public bool HasCurrentChoices => CurrentChoices.Count > 1;

    public bool HasZone => ShownZone is not null;

    public string FormTitle => SelectedLabel is { } label ? $"Editing: {label.Text}" : $"New label on layer {LayerNames[SelectedLayer]}";

    public string StatusLine => IsDirty ? "Unsaved changes on this layer." : StatusText;

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(StatusLine));

    // ───────────────────────── the choices that change what is shown ─────────────────────────

    /// <summary>0 for Current zone, 1 for Select zone.</summary>
    public int SelectedSubTab
    {
        get => _selectedSubTab;
        set
        {
            if (value == _selectedSubTab || value is < 0 or > 1)
            {
                return;
            }

            if (!TryLeave())
            {
                Revert(nameof(SelectedSubTab));
                Revert(nameof(IsCurrentZoneTab));
                Revert(nameof(IsSelectZoneTab));
                return;
            }

            _selectedSubTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCurrentZoneTab));
            OnPropertyChanged(nameof(IsSelectZoneTab));
            ShowZone();
        }
    }

    /// <summary>The Current zone sub-tab is showing.</summary>
    public bool IsCurrentZoneTab
    {
        get => SelectedSubTab == 0;
        set
        {
            if (value)
            {
                SelectedSubTab = 0;
            }
        }
    }

    /// <summary>The Select zone sub-tab is showing.</summary>
    public bool IsSelectZoneTab
    {
        get => SelectedSubTab == 1;
        set
        {
            if (value)
            {
                SelectedSubTab = 1;
            }
        }
    }

    public IReadOnlyList<string> LayerChoices => LayerNames;

    public IReadOnlyList<MapLabelSizeChoice> SizeChoices => Sizes;

    public MapPack? SelectedPack
    {
        get => _selectedPack;
        set
        {
            if (Equals(value, _selectedPack) || value is null)
            {
                return;
            }

            if (!TryLeave())
            {
                Revert(nameof(SelectedPack));
                return;
            }

            _selectedPack = value;
            OnPropertyChanged();
            ReadPack();
            ShowZone();
        }
    }

    /// <summary>Opens a zone's map on the Select zone sub-tab (from Ctrl+F). False when the pack has no map for it.</summary>
    public bool OpenZone(string shortName)
    {
        IsSelectZoneTab = true;
        if (Expansions.Count > 0)
        {
            SelectedExpansion = Expansions[0]; // All
        }

        if (ZoneChoices.FirstOrDefault(z => z.ShortName.Equals(shortName, StringComparison.OrdinalIgnoreCase)) is not { } zone)
        {
            return false;
        }

        SelectedZone = zone;
        return true;
    }

    public MapExpansionChoice? SelectedExpansion
    {
        get => _selectedExpansion;
        set
        {
            if (Equals(value, _selectedExpansion) || value is null)
            {
                return;
            }

            bool zoneStays = SelectedZone is { } zone && Matches(value, zone.Expansion);
            if (!IsCurrentZoneTab && !zoneStays && !TryLeave())
            {
                Revert(nameof(SelectedExpansion));
                return;
            }

            _selectedExpansion = value;
            OnPropertyChanged();
            RebuildZoneChoices();
            if (!IsCurrentZoneTab)
            {
                ShowZone();
            }
        }
    }

    public MapZoneChoice? SelectedZone
    {
        get => _selectedZone;
        set
        {
            if (Equals(value, _selectedZone) || value is null)
            {
                return;
            }

            if (!IsCurrentZoneTab && !TryLeave())
            {
                Revert(nameof(SelectedZone));
                return;
            }

            _selectedZone = value;
            OnPropertyChanged();
            if (!IsCurrentZoneTab)
            {
                ShowZone();
            }
        }
    }

    public MapZoneChoice? SelectedCurrentChoice
    {
        get => _selectedCurrentChoice;
        set
        {
            if (Equals(value, _selectedCurrentChoice) || value is null)
            {
                return;
            }

            if (IsCurrentZoneTab && !TryLeave())
            {
                Revert(nameof(SelectedCurrentChoice));
                return;
            }

            _selectedCurrentChoice = value;
            OnPropertyChanged();
            if (_directory.ByShortName(value.ShortName) is { } info)
            {
                _chosenCandidate[info.Name] = value.ShortName;
            }

            if (IsCurrentZoneTab)
            {
                ShowZone();
            }
        }
    }

    public int SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (value == _selectedLayer || value < 0 || value >= MapFiles.LayerCount)
            {
                return;
            }

            if (!TryLeave())
            {
                Revert(nameof(SelectedLayer));
                return;
            }

            _selectedLayer = value;
            OnPropertyChanged();
            ShowLayer();
        }
    }

    // ───────────────────────── loading ─────────────────────────

    /// <summary>Reads the map packs and zone names from the EQ folder, and where each character is from the end of its log.</summary>
    public void Initialize(WatchedLogConfig? primary)
    {
        _followed = primary;
        string folder = _eqFolder();
        _loadedFolder = folder;
        _directory = ZoneDirectory.Load(folder);

        Packs.Clear();
        foreach (MapPack pack in MapFiles.Packs(folder))
        {
            Packs.Add(pack);
        }

        string? skin = primary is null ? null : MapFiles.CharacterPack(folder, primary.CharacterName, primary.ServerName);
        _selectedPack = Packs.FirstOrDefault(p => string.Equals(p.Name, skin, StringComparison.OrdinalIgnoreCase))
            ?? Packs.FirstOrDefault(p => p.IsRoot) ?? Packs.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedPack));
        ReadPack();
        ShowZone();

        // Where each character was when its log was last written (live lines take over from there).
        var logs = _logs().Select(l => (l.FilePath, l.CharacterName, l.ServerName)).ToList();
        ZoneTracker zones = _zones;
        _ = Task.Run(() =>
        {
            foreach ((string path, string character, string server) in logs)
            {
                zones.ReadFromLog(path, character, server);
            }
        });
    }

    /// <summary>
    /// Called by the app's refresh timer with the log of the character you played last: follows that character to a new zone and moves
    /// every character's /loc marker.
    /// </summary>
    public void Refresh(WatchedLogConfig? primary)
    {
        _followed = primary;
        if (!string.Equals(_eqFolder(), _loadedFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsDirty)
            {
                Initialize(primary);
            }

            return;
        }

        long version = _zones.Version;
        if (version == _zoneVersionSeen && string.Equals(_lastPrimary, Key(primary), StringComparison.Ordinal))
        {
            return;
        }

        _zoneVersionSeen = version;
        _lastPrimary = Key(primary);
        if (IsCurrentZoneTab)
        {
            ShowZone();
        }
        else
        {
            UpdatePlayerSpot();
        }
    }

    private string _lastPrimary = string.Empty;

    private static string Key(WatchedLogConfig? log) => log is null ? string.Empty : log.CharacterName + "|" + log.ServerName;

    private WatchedLogConfig? _followed;

    /// <summary>The character the map follows: the one you played last.</summary>
    private WatchedLogConfig? Primary() => _followed;

    /// <summary>Re-reads the map packs and the zone shown (after files were changed outside the app).</summary>
    [RelayCommand]
    private void Reload()
    {
        if (!TryLeave())
        {
            return;
        }

        IsDirty = false;
        string? pack = SelectedPack?.Name;
        Initialize(Primary());
        if (pack is not null && Packs.FirstOrDefault(p => p.Name == pack) is { } same && !Equals(same, _selectedPack))
        {
            _selectedPack = same;
            OnPropertyChanged(nameof(SelectedPack));
            ReadPack();
            ShowZone();
        }
    }

    private void ReadPack()
    {
        _zonesInPack = SelectedPack is { } pack
            ? new HashSet<string>(MapFiles.Zones(pack.Folder), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RebuildZoneChoices();
    }

    private static bool Matches(MapExpansionChoice choice, int expansion) => choice.Number is null || choice.Number == expansion;

    private void RebuildZoneChoices()
    {
        var all = _zonesInPack.Select(zone => _directory.ByShortName(zone) is { } info
                ? new MapZoneChoice(zone, info.Name, info.Expansion)
                : new MapZoneChoice(zone, zone, 0))
            .ToList();

        // Two maps with the same name (a zone and its later version) say which expansion each is.
        var named = all.GroupBy(z => z.Label, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Count() == 1 ? g : g.Select(z => z with { Label = $"{z.Label} ({ZoneDirectory.ExpansionName(z.Expansion)})" }))
            .Where(z => SelectedExpansion is null || Matches(SelectedExpansion, z.Expansion))
            .OrderBy(z => z.Label.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? z.Label[4..] : z.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The list is changed item by item (never cleared) so a bound drop-down keeps its selection meanwhile.
        string? keep = _selectedZone?.ShortName;
        EQOverwatch.App.Presentation.ObservableSync.SyncValues(ZoneChoices, named);
        MapZoneChoice? zoneNow = ZoneChoices.FirstOrDefault(z => z.ShortName == keep) ?? ZoneChoices.FirstOrDefault();
        if (!Equals(zoneNow, _selectedZone))
        {
            _selectedZone = zoneNow;
            OnPropertyChanged(nameof(SelectedZone));
        }
    }

    /// <summary>Shows the zone the sub-tab calls for: the followed character's, or the one picked.</summary>
    private void ShowZone()
    {
        string? zone = IsCurrentZoneTab ? CurrentZone() : SelectedZone?.ShortName;
        if (IsDirty && !string.Equals(zone, ShownZone, StringComparison.OrdinalIgnoreCase))
        {
            // The character moved while there are unsaved changes: they stay on screen until saved or thrown away.
            FollowNote = zone is null ? string.Empty : "Save or discard your changes to follow to the new zone.";
            UpdatePlayerSpot();
            return;
        }

        FollowNote = string.Empty;
        if (string.Equals(zone, ShownZone, StringComparison.OrdinalIgnoreCase) && !_reloadFiles)
        {
            UpdatePlayerSpot();
            return;
        }

        _reloadFiles = false;
        ShownZone = zone;
        OnPropertyChanged(nameof(HasZone));
        try
        {
            _layers = zone is not null && SelectedPack is { } pack ? MapFiles.Load(pack.Folder, zone) : EmptyLayers();
            ErrorText = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _layers = EmptyLayers();
            ErrorText = $"Couldn't read the map files: {ex.Message}";
        }

        EmptyText = zone is null ? (IsCurrentZoneTab ? CurrentZoneText : "Choose a zone.") : string.Empty;
        UpdatePlayerSpot();
        ShowLayer();
    }

    private bool _reloadFiles;

    /// <summary>The map of the followed character's zone in the chosen pack, and the line saying where they are.</summary>
    private string? CurrentZone()
    {
        CurrentChoices.Clear();
        OnPropertyChanged(nameof(HasCurrentChoices));
        WatchedLogConfig? primary = Primary();
        if (primary is null)
        {
            CurrentZoneText = "No character logs yet. Add them on the Characters tab.";
            return null;
        }

        string who = primary.CharacterName;
        if (_zones.Get(who, primary.ServerName) is not { } where)
        {
            CurrentZoneText = $"{who}: zone not known yet. It shows after {who} zones.";
            return null;
        }

        List<ZoneInfo> maps = _directory.ByName(where.Zone).Where(z => _zonesInPack.Contains(z.ShortName)).ToList();
        if (maps.Count == 0)
        {
            CurrentZoneText = $"{who} is in {where.Zone}, which has no map in {SelectedPack?.Label ?? "the maps folder"}.";
            return null;
        }

        CurrentZoneText = $"{who} is in {where.Zone}";
        if (maps.Count > 1)
        {
            foreach (ZoneInfo map in maps)
            {
                CurrentChoices.Add(new MapZoneChoice(map.ShortName, $"{map.Name} ({ZoneDirectory.ExpansionName(map.Expansion)})", map.Expansion));
            }

            string chosen = _chosenCandidate.GetValueOrDefault(maps[0].Name) ?? maps[0].ShortName;
            _selectedCurrentChoice = CurrentChoices.FirstOrDefault(c => c.ShortName == chosen) ?? CurrentChoices[0];
            OnPropertyChanged(nameof(SelectedCurrentChoice));
            OnPropertyChanged(nameof(HasCurrentChoices));
            return _selectedCurrentChoice.ShortName;
        }

        return maps[0].ShortName;
    }

    private void UpdatePlayerSpot()
    {
        var spots = new List<MapPlayerSpot>();
        if (ShownZone is { } zone)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WatchedLogConfig log in _logs())
            {
                if (seen.Add(log.CharacterName + "|" + log.ServerName)
                    && _zones.Get(log.CharacterName, log.ServerName) is { Loc: { } loc } where
                    && _directory.ByName(where.Zone).Any(z => string.Equals(z.ShortName, zone, StringComparison.OrdinalIgnoreCase)))
                {
                    spots.Add(new MapPlayerSpot(log.CharacterName, -loc.X, -loc.Y));
                }
            }
        }

        if (!spots.SequenceEqual(PlayerSpots))
        {
            PlayerSpots = spots;
            RaiseScene();
        }
    }

    /// <summary>Lists the labels of the layer chosen, fresh from what was read from its file.</summary>
    private void ShowLayer()
    {
        Labels.Clear();
        foreach (MapLabel label in _layers[SelectedLayer].Labels)
        {
            Labels.Add(MapLabelItem.From(label));
        }

        Pending = null;
        SelectedLabel = null;
        IsDirty = false;
        ResetForm();
        UpdateLayerText();
        OnPropertyChanged(nameof(FormTitle));
        RaiseScene();
    }

    private void UpdateLayerText()
    {
        string file = ShownZone is null ? string.Empty : MapFiles.FileName(ShownZone, SelectedLayer);
        bool exists = ShownZone is not null && SelectedPack is { } pack && File.Exists(MapFiles.LayerPath(pack.Folder, ShownZone, SelectedLayer));
        LayerFileText = file.Length == 0 ? string.Empty : exists ? file : file + " (new file)";
        LabelListTitle = $"Labels on layer {LayerNames[SelectedLayer]} ({Labels.Count})";
    }

    // ───────────────────────── editing ─────────────────────────

    /// <summary>A click on an empty part of the map: a new label will go there.</summary>
    public void PlaceAt(double x, double y)
    {
        if (ShownZone is null)
        {
            return;
        }

        SelectedLabel = null;
        Pending = (Math.Round(x), Math.Round(y));
        Sync(() => EditLoc = FormatLoc(-Pending.Value.Y, -Pending.Value.X, ParseLoc(EditLoc)?.Z ?? 0));
        ErrorText = string.Empty;
        RaiseScene();
    }

    /// <summary>Selects a label of the layer for editing.</summary>
    [RelayCommand]
    private void SelectLabel(MapLabelItem? label)
    {
        if (label is not null && Labels.Contains(label))
        {
            SelectedLabel = label;
        }
    }

    /// <summary>A label dragged on the map.</summary>
    public void MoveLabel(MapLabelItem label, double x, double y)
    {
        double rx = Math.Round(x), ry = Math.Round(y);
        if (label.X == rx && label.Y == ry)
        {
            return;
        }

        label.X = rx;
        label.Y = ry;
        if (ReferenceEquals(label, SelectedLabel))
        {
            Sync(() => EditLoc = FormatLoc(-ry, -rx, label.Z));
        }

        MarkChanged();
    }

    partial void OnSelectedLabelChanged(MapLabelItem? value)
    {
        if (value is not null)
        {
            Pending = null;
            Sync(() =>
            {
                EditText = value.Text;
                EditLoc = FormatLoc(-value.Y, -value.X, value.Z);
                EditColorHex = value.Hex;
                EditSize = Sizes.FirstOrDefault(s => s.Size == value.Size) ?? Sizes[1];
            });
        }

        ErrorText = string.Empty;
        RaiseScene();
    }

    partial void OnEditTextChanged(string value)
    {
        if (_syncingForm || SelectedLabel is not { } label || value.Trim().Length == 0)
        {
            return;
        }

        label.Text = value.Trim();
        OnPropertyChanged(nameof(FormTitle));
        MarkChanged();
    }

    partial void OnEditLocChanged(string value)
    {
        if (_syncingForm || ParseLoc(value) is not { } loc)
        {
            return;
        }

        ErrorText = string.Empty;
        if (SelectedLabel is { } label)
        {
            label.X = Math.Round(-loc.X);
            label.Y = Math.Round(-loc.Y);
            label.Z = loc.Z;
            MarkChanged();
        }
        else
        {
            Pending = (Math.Round(-loc.X), Math.Round(-loc.Y));
            RaiseScene();
        }
    }

    partial void OnEditColorHexChanged(string value)
    {
        if (!_syncingForm && SelectedLabel is { } label && value.Length > 0)
        {
            label.Hex = value;
            MarkChanged();
        }
        else
        {
            RaiseScene();
        }
    }

    partial void OnEditSizeChanged(MapLabelSizeChoice value)
    {
        if (!_syncingForm && SelectedLabel is { } label && value is not null)
        {
            label.Size = value.Size;
            MarkChanged();
        }
    }

    [RelayCommand]
    private void AddLabel()
    {
        string text = EditText.Trim();
        if (ShownZone is null)
        {
            ErrorText = "Choose a zone first.";
            return;
        }

        if (text.Length == 0)
        {
            ErrorText = "Enter the label text first.";
            return;
        }

        if (Pending is not { } spot)
        {
            ErrorText = "Click the map or enter a /loc first.";
            return;
        }

        var label = new MapLabelItem
        {
            X = spot.X,
            Y = spot.Y,
            Z = ParseLoc(EditLoc)?.Z ?? 0,
            Text = text,
            Hex = EditColorHex,
            Size = EditSize.Size,
        };
        Labels.Add(label);
        Pending = null;
        ResetForm();
        MarkChanged();
    }

    [RelayCommand]
    private void DeleteLabel(MapLabelItem? label)
    {
        if (label is null || !Labels.Remove(label))
        {
            return;
        }

        if (ReferenceEquals(label, SelectedLabel))
        {
            SelectedLabel = null;
            ResetForm();
        }

        MarkChanged();
    }

    /// <summary>Stops editing the selected label; the form adds a new one again.</summary>
    [RelayCommand]
    private void NewLabel()
    {
        SelectedLabel = null;
        ResetForm();
        RaiseScene();
    }

    /// <summary>Puts the followed character's last /loc in the /loc box (moving the selected label there).</summary>
    [RelayCommand]
    private void UseMyLoc()
    {
        if (Primary() is not { } primary || _zones.Get(primary.CharacterName, primary.ServerName) is not { } where)
        {
            ErrorText = "No /loc from the character you played last yet. Type /loc in game first.";
            return;
        }

        if (where.Loc is not { } loc)
        {
            ErrorText = $"{primary.CharacterName} hasn't used /loc since entering {where.Zone}.";
            return;
        }

        if (ShownZone is null || !_directory.ByName(where.Zone).Any(z => string.Equals(z.ShortName, ShownZone, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorText = $"{primary.CharacterName}'s last /loc is in {where.Zone}, not this zone.";
            return;
        }

        EditLoc = FormatLoc(loc.Y, loc.X, loc.Z);
    }

    [RelayCommand]
    private void Save() => SaveLayer();

    /// <summary>Throws away the unsaved changes on the layer and shows it as its file is.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        IsDirty = false;
        StatusText = string.Empty;
        _reloadFiles = true;
        ShowZone();
    }

    /// <summary>Asks about unsaved changes before the app closes. False when the user chose to stay.</summary>
    public bool ConfirmClose() => TryLeave();

    private bool SaveLayer()
    {
        if (ShownZone is not { } zone || SelectedPack is not { } pack)
        {
            ErrorText = "Choose a zone first.";
            return false;
        }

        MapLayer layer = _layers[SelectedLayer].WithLabels(Labels.Select(l => l.ToLabel()));
        try
        {
            string path = MapFiles.Save(pack.Folder, zone, SelectedLayer, layer);
            _layers[SelectedLayer] = layer;
            string where = pack.IsRoot ? "maps" : $"maps\\{pack.Name}";
            IsDirty = false;
            ErrorText = string.Empty;
            StatusText = $"Saved {Labels.Count} label{(Labels.Count == 1 ? string.Empty : "s")} to {where}\\{Path.GetFileName(path)}. "
                + "A copy of the old file is kept as .bak the first time. They show in game after you zone or reopen the map.";
            UpdateLayerText();
            if (FollowNote.Length > 0)
            {
                ShowZone(); // the character moved on while there were changes
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = $"Couldn't save {MapFiles.FileName(zone, SelectedLayer)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>True when nothing unsaved would be lost: there was none, or the user saved or threw it away.</summary>
    private bool TryLeave()
    {
        if (!IsDirty)
        {
            return true;
        }

        string file = ShownZone is null ? "this layer" : MapFiles.FileName(ShownZone, SelectedLayer);
        switch (_dialogs.AskUnsavedChanges("Unsaved map labels", $"You have unsaved label changes in {file}. Save them?"))
        {
            case UnsavedChoice.Save:
                return SaveLayer();
            case UnsavedChoice.Discard:
                IsDirty = false;
                _reloadFiles = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Puts a drop-down back on the value it had (after the user chose to stay).</summary>
    private void Revert(string property) => _post(() => OnPropertyChanged(property));

    private void MarkChanged()
    {
        IsDirty = true;
        LabelListTitle = $"Labels on layer {LayerNames[SelectedLayer]} ({Labels.Count})";
        RaiseScene();
    }

    private void ResetForm() => Sync(() =>
    {
        EditText = string.Empty;
        EditLoc = string.Empty;
    });

    private void Sync(Action change)
    {
        bool was = _syncingForm;
        _syncingForm = true;
        try
        {
            change();
        }
        finally
        {
            _syncingForm = was;
        }
    }

    private void RaiseScene() => SceneChanged?.Invoke(this, EventArgs.Empty);

    private static MapLayer[] EmptyLayers() => Enumerable.Repeat(MapLayer.Empty, MapFiles.LayerCount).ToArray();

    // ───────────────────────── text ─────────────────────────

    /// <summary>"Y, X" or "Y, X, Z", as EQ prints /loc.</summary>
    public static string FormatLoc(double y, double x, double? z) => z is { } height
        ? string.Create(CultureInfo.InvariantCulture, $"{y:0.##}, {x:0.##}, {height:0.##}")
        : string.Create(CultureInfo.InvariantCulture, $"{y:0.##}, {x:0.##}");

    /// <summary>Reads "Y, X" or "Y, X, Z" (spaces or commas between the numbers).</summary>
    public static (double Y, double X, double Z)? ParseLoc(string text)
    {
        string[] parts = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        var numbers = new double[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || !double.IsFinite(numbers[i]))
            {
                return null;
            }
        }

        return (numbers[0], numbers[1], numbers[2]);
    }

    public static (byte R, byte G, byte B) ParseHex(string hex)
    {
        string text = hex.Trim().TrimStart('#');
        if (text.Length == 8)
        {
            text = text[2..]; // #AARRGGBB
        }

        return text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)
            ? ((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : ((byte)255, (byte)255, (byte)255);
    }
}
