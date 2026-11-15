using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Characters;

namespace EQOverwatch.App.ViewModels;

/// <summary>
/// One slot of the inventory picture. Worn slots and bags show a drawn icon for the slot; other items show a lettered tile whose color
/// comes from the item's name, so the same item always looks the same. Augment sockets are small squares, with a dot when filled.
/// </summary>
public sealed record SlotView(
    string Label,
    string ToolTip,
    bool IsEmpty,
    bool IsBag,
    bool IsOpen,
    Geometry? Glyph,
    Brush GlyphBrush,
    string Initials,
    Brush? TileFill,
    Brush? TileInk,
    string CountText,
    IReadOnlyList<bool> Augments,
    InventorySlot Slot)
{
    public bool ShowGlyph => Glyph is not null;

    public bool ShowTile => Glyph is null && !IsEmpty;

    public bool HasAugments => Augments.Count > 0;
}

/// <summary>The right side of the Inventory tab: the worn gear and bags (or bank, or shared bank) laid out like the game's window.</summary>
public sealed partial class InventoryPanelViewModel : ObservableObject
{
    public const int InventorySection = 0;
    public const int BankSection = 1;
    public const int SharedBankSection = 2;

    private static readonly Color[] TileColors =
    {
        Color.FromRgb(0x7F, 0x77, 0xDD), Color.FromRgb(0x1D, 0x9E, 0x75), Color.FromRgb(0xD8, 0x5A, 0x30), Color.FromRgb(0xD4, 0x53, 0x7E),
        Color.FromRgb(0x37, 0x8A, 0xDD), Color.FromRgb(0xBA, 0x75, 0x17), Color.FromRgb(0x63, 0x99, 0x22),
    };

    private static readonly Brush FilledGlyph = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)));
    private static readonly Brush EmptyGlyph = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xE5, 0xE7, 0xEB)));
    private static readonly Brush BagGlyph = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x86, 0xF5)));

    private IReadOnlyList<InventoryItem> _rows = Array.Empty<InventoryItem>();
    private string? _openLocation;
    private bool _closedByUser;

    [ObservableProperty]
    private int _section;

    [ObservableProperty]
    private IReadOnlyList<SlotView> _worn = Array.Empty<SlotView>();

    [ObservableProperty]
    private IReadOnlyList<SlotView> _containers = Array.Empty<SlotView>();

    [ObservableProperty]
    private IReadOnlyList<SlotView> _openContents = Array.Empty<SlotView>();

    [ObservableProperty]
    private string _openTitle = string.Empty;

    [ObservableProperty]
    private string _containersTitle = "Bags (click one to open it)";

    public bool IsInventorySection { get => Section == InventorySection; set { if (value) Section = InventorySection; } }

    public bool IsBankSection { get => Section == BankSection; set { if (value) Section = BankSection; } }

    public bool IsSharedBankSection { get => Section == SharedBankSection; set { if (value) Section = SharedBankSection; } }

    public bool ShowWorn => Section == InventorySection;

    public bool HasOpenBag => OpenContents.Count > 0;

    public bool HasData => _rows.Count > 0;

    /// <summary>Bags sit two to a row beside the worn gear, like the game; the bank and shared bank get six to a row.</summary>
    public double ContainerPanelWidth => Section == InventorySection ? 2 * 58 : 6 * 58;

    partial void OnSectionChanged(int value)
    {
        _openLocation = null;
        _closedByUser = false;
        OnPropertyChanged(nameof(IsInventorySection));
        OnPropertyChanged(nameof(IsBankSection));
        OnPropertyChanged(nameof(IsSharedBankSection));
        OnPropertyChanged(nameof(ShowWorn));
        OnPropertyChanged(nameof(ContainerPanelWidth));
        Rebuild();
    }

    partial void OnOpenContentsChanged(IReadOnlyList<SlotView> value) => OnPropertyChanged(nameof(HasOpenBag));

    /// <summary>Shows a character's inventory rows (every row of its file, empty slots included).</summary>
    public void Load(IReadOnlyList<InventoryItem> rows)
    {
        _rows = rows;
        OnPropertyChanged(nameof(HasData));
        Rebuild();
    }

    /// <summary>Opens a bag (or closes it when it is open already). Clicking anything else does nothing.</summary>
    [RelayCommand]
    private void Open(SlotView? slot)
    {
        if (slot is not { IsBag: true })
        {
            return;
        }

        _closedByUser = _openLocation == slot.Slot.Location;
        _openLocation = _closedByUser ? null : slot.Slot.Location;
        Rebuild();
    }

    private void Rebuild()
    {
        (string kind, string label) = Section switch
        {
            BankSection => ("Bank", "Bank"),
            SharedBankSection => ("SharedBank", "Shared bank"),
            _ => ("General", "General"),
        };

        Worn = Section == InventorySection ? InventoryLayout.Worn(_rows).Select(s => View(s, worn: true)).ToList() : Array.Empty<SlotView>();
        IReadOnlyList<InventorySlot> containers = InventoryLayout.Containers(_rows, kind, label);
        if ((_openLocation is null && !_closedByUser) || (_openLocation is not null && containers.All(c => c.Location != _openLocation)))
        {
            _openLocation = containers.FirstOrDefault(c => c.IsBag)?.Location; // the first bag starts open
        }

        Containers = containers.Select(s => View(s, worn: false)).ToList();
        ContainersTitle = Section == InventorySection ? "Bags (click one to open it)" : $"{label} slots (click a bag to open it)";
        InventorySlot? open = containers.FirstOrDefault(c => c.Location == _openLocation && c.IsBag);
        OpenTitle = open is null ? string.Empty : $"{open.Label}: {open.Item} ({open.Contents.Count(c => !c.IsEmpty)} of {open.Capacity} slots used)";
        OpenContents = open?.Contents.Select(s => View(s, worn: false)).ToList() ?? (IReadOnlyList<SlotView>)Array.Empty<SlotView>();
    }

    private SlotView View(InventorySlot slot, bool worn)
    {
        string augmentText = slot.Augments.Count == 0 ? string.Empty
            : $"\nAugment sockets: {string.Join(", ", slot.Augments.Select(a => a ? "filled" : "empty"))}";
        string tip = slot.IsEmpty
            ? $"{slot.Label}: empty"
            : $"{slot.Label}: {slot.Item}{(slot.Count > 1 ? $" ×{slot.Count}" : string.Empty)}{(slot.IsBag ? $"\n{slot.Capacity} slots, click to open" : string.Empty)}{augmentText}";

        Geometry? glyph = worn ? SlotIcons.For(SlotName(slot.Location, slot.Label)) : slot.IsBag ? SlotIcons.Bag : null;
        Brush glyphBrush = slot.IsBag ? BagGlyph : slot.IsEmpty ? EmptyGlyph : FilledGlyph;
        Brush? fill = null, ink = null;
        string initials = string.Empty;
        if (glyph is null && !slot.IsEmpty)
        {
            Color color = TileColors[(int)(Hash(slot.Item!) % (uint)TileColors.Length)];
            fill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)));
            ink = Frozen(new SolidColorBrush(color));
            initials = Initials(slot.Item!);
        }

        return new SlotView(
            slot.Label, tip, slot.IsEmpty, slot.IsBag, slot.IsBag && slot.Location == _openLocation, glyph, glyphBrush, initials, fill, ink,
            slot.Count > 1 ? slot.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty, slot.Augments, slot);
    }

    private static string SlotName(string location, string label) => location.Length > 0 ? location.Split('-')[0].Trim() : label;

    /// <summary>"Frightening Flatbread" is "FF"; "Spell: Hammer of Souls" is "HS"; small words are skipped.</summary>
    public static string Initials(string item)
    {
        string name = item.StartsWith("Spell: ", StringComparison.OrdinalIgnoreCase) ? item[7..] : item;
        string letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsUpper(w[0]) || char.IsDigit(w[0])).Take(2).Select(w => w[0]));
        return letters.Length > 0 ? letters : name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
    }

    private static uint Hash(string text)
    {
        uint h = 0;
        foreach (char c in text)
        {
            h = unchecked(h * 31 + c);
        }

        return h;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>Simple line icons for the worn slots and for bags, drawn on a 24 by 24 grid.</summary>
public static class SlotIcons
{
    private static readonly Dictionary<string, Geometry> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Charm"] = Parse("M12 4 L18 12 L12 20 L6 12 Z"),
        ["Ear"] = Parse("M9 18 C9 15 15 14 15 9 A3 3 0 0 0 9 9 M9 17.5 A1.5 1.5 0 1 0 9.01 17.5"),
        ["Head"] = Parse("M5 15 A7 7 0 0 1 19 15 L19 17 L5 17 Z M12 8 L12 12"),
        ["Face"] = Parse("M9 7 L15 7 A4 4 0 0 1 19 11 L19 12 A4 4 0 0 1 15 16 L9 16 A4 4 0 0 1 5 12 L5 11 A4 4 0 0 1 9 7 Z M9.5 10.2 A1.3 1.3 0 1 0 9.51 10.2 M14.5 10.2 A1.3 1.3 0 1 0 14.51 10.2"),
        ["Neck"] = Parse("M6 5 C6 13 18 13 18 5 M12 12.5 A2.5 2.5 0 1 0 12.01 12.5"),
        ["Shoulders"] = Parse("M4 14 C5 9 9 8 12 8 C15 8 19 9 20 14"),
        ["Arms"] = Parse("M8 4 L6 20 M16 4 L18 20"),
        ["Back"] = Parse("M7 4 L17 4 L19 20 L5 20 Z"),
        ["Wrist"] = Parse("M9 9 L15 9 A3 3 0 0 1 15 15 L9 15 A3 3 0 0 1 9 9 Z"),
        ["Range"] = Parse("M7 4 C15 8 15 16 7 20 M7 4 L7 20"),
        ["Hands"] = Parse("M8 20 L8 11 M11 20 L11 6 M14 20 L14 7 M17 20 L17 12 M8 20 L17 20"),
        ["Primary"] = Parse("M6 18 L17 7 L18 5 L16 6 L5 17 M5 15 L9 19"),
        ["Secondary"] = Parse("M12 4 L19 7 L19 12 C19 16 16 19 12 20 C8 19 5 16 5 12 L5 7 Z"),
        ["Fingers"] = Parse("M12 8 A5 5 0 1 0 12.01 8 M10 6 L12 4 L14 6"),
        ["Chest"] = Parse("M6 5 L9 7 L15 7 L18 5 L19 11 L17 12 L17 20 L7 20 L7 12 L5 11 Z"),
        ["Legs"] = Parse("M7 4 L17 4 L16 20 L13 20 L12 11 L11 20 L8 20 Z"),
        ["Feet"] = Parse("M8 4 L8 14 L5 17 L5 19 L18 19 L18 17 L13 14 L13 4"),
        ["Waist"] = Parse("M4 10 L20 10 L20 14 L4 14 Z M10 9 L14 9 L14 15 L10 15 Z"),
        ["Power Source"] = Parse("M13 3 L6 13 L11 13 L10 21 L17 11 L12 11 Z"),
        ["Ammo"] = Parse("M12 3 L12 21 M9 6 L12 3 L15 6 M9 21 L12 18 L15 21"),
    };

    public static Geometry Bag { get; } = Parse("M6 8 L18 8 L17 20 L7 20 Z M9 8 A3 3 0 0 1 15 8");

    public static Geometry? For(string slot) => Icons.GetValueOrDefault(slot) ?? Icons["Charm"];

    private static Geometry Parse(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
