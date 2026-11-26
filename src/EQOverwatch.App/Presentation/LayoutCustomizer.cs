using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using EQOverwatch.App.Interop;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.Presentation;

/// <summary>
/// Lets the player arrange the app, and remembers it in a <see cref="UiLayoutStore"/>:
/// <list type="bullet">
/// <item><description>Dividers: every gap between two side-by-side (or stacked) panels gets a draggable divider.</description></item>
/// <item><description>Tables: column widths, order, shown columns, sort, row height; a right-click menu (Hide/Show Columns, Row height,
/// Copy Table); the first column stays put in wide tables; rows naming one of your characters are tinted.</description></item>
/// <item><description>Groups: collapsed groups stay collapsed.</description></item>
/// <item><description>Tabs: drag to reorder, right-click to Hide/Show Tabs or Pop Out; Ctrl + mouse wheel sizes a main tab's text.</description></item>
/// </list>
/// Everything is keyed by where it sits (the tab path) and what it shows (column headers, tab names), so it survives restarts.
/// </summary>
public sealed class LayoutCustomizer
{
    private static bool _classHandlersRegistered;

    private readonly UiLayoutStore _store;
    private readonly Func<string, bool> _isMine;
    private readonly Window _main;
    private readonly Func<string, WindowPlacementSetting?> _getPlacement;
    private readonly Action<string, WindowPlacementSetting> _setPlacement;
    private readonly List<WeakReference<DataGrid>> _grids = new();
    private readonly List<(WeakReference<TabControl> Tabs, string Path)> _tabControls = new();
    private readonly List<SplitInfo> _splits = new();
    private readonly List<WeakReference<Expander>> _expanders = new();
    private readonly Dictionary<TabItem, Window> _popOuts = new();
    private readonly ConditionalWeakTable<DataGridColumn, ColumnOriginal> _columnOriginals = new();
    private bool _applying;

    private LayoutCustomizer(Window main, UiLayoutStore store, Func<string, bool> isMine,
        Func<string, WindowPlacementSetting?> getPlacement, Action<string, WindowPlacementSetting> setPlacement)
    {
        _main = main;
        _store = store;
        _isMine = isMine;
        _getPlacement = getPlacement;
        _setPlacement = setPlacement;
        _store.Replaced += () => _main.Dispatcher.BeginInvoke(ReapplyAll);
    }

    /// <summary>The one customizer of the main window (null before the window is set up).</summary>
    public static LayoutCustomizer? Current { get; private set; }

    public UiLayoutStore Store => _store;

    /// <summary>Sets up the main window: dividers, tabs, text size, and the table / group behaviors for every window of the app.</summary>
    public static LayoutCustomizer Attach(Window main, UiLayoutStore store, Func<string, bool> isMine,
        Func<string, WindowPlacementSetting?> getPlacement, Action<string, WindowPlacementSetting> setPlacement)
    {
        var customizer = new LayoutCustomizer(main, store, isMine, getPlacement, setPlacement);
        Current = customizer;
        if (!_classHandlersRegistered)
        {
            _classHandlersRegistered = true;
            EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Current?.OnGridLoaded((DataGrid)s)));
            EventManager.RegisterClassHandler(typeof(Expander), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Current?.OnExpanderLoaded((Expander)s)));
            EventManager.RegisterClassHandler(typeof(Expander), Expander.ExpandedEvent, new RoutedEventHandler((s, e) => Current?.OnExpanderToggled((Expander)s, e)));
            EventManager.RegisterClassHandler(typeof(Expander), Expander.CollapsedEvent, new RoutedEventHandler((s, e) => Current?.OnExpanderToggled((Expander)s, e)));
            SortKeeper.Sorted += grid =>
            {
                if ((bool)grid.GetValue(SetUpProperty))
                {
                    Current?.SaveSort(grid);
                }
            };
        }

        main.PreviewMouseWheel += customizer.OnPreviewMouseWheel;
        ApplyRowHeight(store.Get(RowHeightKey));
        customizer.WalkWindow();
        customizer.SetUpLoaded(main); // tables and groups already on screen loaded before this ran
        customizer.WatchTabSwitches(main);
        return customizer;
    }

    // ───────────── keys ─────────────

    /// <summary>A pop-out window's content keeps the tab path it had in the main window.</summary>
    public static readonly DependencyProperty PathPrefixProperty = DependencyProperty.RegisterAttached(
        "PathPrefix", typeof(string), typeof(LayoutCustomizer), new PropertyMetadata(null));

    /// <summary>Set on a table row that names one of your characters; the row style tints it.</summary>
    public static readonly DependencyProperty IsMineProperty = DependencyProperty.RegisterAttached(
        "IsMine", typeof(bool), typeof(LayoutCustomizer), new PropertyMetadata(false));

    public static bool GetIsMine(DependencyObject element) => (bool)element.GetValue(IsMineProperty);

    public static void SetIsMine(DependencyObject element, bool value) => element.SetValue(IsMineProperty, value);

    /// <summary>A tab's place in its row (the tab strip lays tabs out in this order); -1 means its listed place.</summary>
    public static readonly DependencyProperty TabOrderProperty = DependencyProperty.RegisterAttached(
        "TabOrder", typeof(int), typeof(LayoutCustomizer), new PropertyMetadata(-1));

    public static int GetTabOrder(DependencyObject element) => (int)element.GetValue(TabOrderProperty);

    public static void SetTabOrder(DependencyObject element, int value) => element.SetValue(TabOrderProperty, value);

    private static readonly DependencyProperty SetUpProperty = DependencyProperty.RegisterAttached(
        "SetUp", typeof(bool), typeof(LayoutCustomizer), new PropertyMetadata(false));

    public const string RowHeightKey = "rows";
    public const string StartupTabKey = "startup.tab";

    private static DependencyObject? ParentOf(DependencyObject node) =>
        LogicalTreeHelper.GetParent(node) ?? (node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : null);

    /// <summary>"Main/Triggers/Raid": the window and the tabs an element sits in.</summary>
    public static string PathOf(DependencyObject element)
    {
        var parts = new List<string>();
        DependencyObject? node = element;
        string root = "Main";
        while (node is not null)
        {
            if (node.GetValue(PathPrefixProperty) is string prefix)
            {
                root = prefix;
                break;
            }

            if (node is TabItem tab)
            {
                parts.Add(TitleOf(tab));
            }

            if (node is Window window)
            {
                root = window is MainWindow ? "Main" : window.GetType().Name;
                break;
            }

            node = ParentOf(node);
        }

        parts.Reverse();
        return parts.Count == 0 ? root : root + "/" + string.Join("/", parts);
    }

    /// <summary>A tab's name: its header text, or the text inside a header made of several parts (a name and a badge).</summary>
    public static string TitleOf(TabItem tab) => tab.Header switch
    {
        string text => text,
        FrameworkElement { Tag: string tag } => tag,
        DependencyObject element => FirstText(element) ?? string.Empty,
        null => string.Empty,
        object item => item.GetType().GetProperty("Name")?.GetValue(item) as string ?? item.ToString() ?? string.Empty,
    };

    private static string HeaderText(object? header) => header switch
    {
        string text => text,
        DependencyObject element => FirstText(element) ?? string.Empty,
        null => string.Empty,
        _ => header.ToString() ?? string.Empty,
    };

    private static string? FirstText(DependencyObject element)
    {
        if (element is TextBlock block)
        {
            return block.Text;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(element))
        {
            if (child is DependencyObject d && FirstText(d) is { } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string GridKey(DataGrid grid) =>
        PathOf(grid) + "|" + string.Join(",", grid.Columns.Take(5).Select(c => HeaderText(c.Header)));

    private static string ColumnId(DataGridColumn column, int index) =>
        HeaderText(column.Header) is { Length: > 0 } title ? title : "#" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Tables and groups on a tab are made when the tab is first shown: set them up once it has been laid out.</summary>
    private void WatchTabSwitches(Window window)
    {
        window.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is TabControl)
            {
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => SetUpLoaded(window));
            }
        }));
    }

    private void SetUpLoaded(DependencyObject node)
    {
        if (node is DataGrid grid)
        {
            OnGridLoaded(grid);
        }
        else if (node is Expander expander)
        {
            OnExpanderLoaded(expander);
        }

        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                SetUpLoaded(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

    // ───────────── the window walk: dividers and tab rows ─────────────

    private void WalkWindow()
    {
        if (_main.Content is DependencyObject content)
        {
            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            Walk(content, "Main", counters);
        }

        ApplyZoomToMainTabs();
    }

    private void Walk(object node, string path, Dictionary<string, int> counters)
    {
        if (node is not DependencyObject d)
        {
            return;
        }

        if (d is TabItem tab)
        {
            path = path + "/" + TitleOf(tab);
        }

        if (d is Grid grid && !(bool)grid.GetValue(SetUpProperty))
        {
            TrySplit(grid, path, counters);
        }

        if (d is TabControl tabs && tabs.ItemsSource is null && !(bool)tabs.GetValue(SetUpProperty))
        {
            SetUpTabs(tabs, path);
        }

        foreach (object child in LogicalTreeHelper.GetChildren(d).Cast<object>().ToList())
        {
            Walk(child, path, counters);
        }
    }

    private sealed record SplitInfo(WeakReference<Grid> Grid, string Key, bool Columns, int Index, GridLength OriginalBefore, GridLength OriginalAfter);

    private sealed record ColumnOriginal(DataGridLength Width, int DisplayIndex, Visibility Visibility);

    private static ControlTemplate? _columnSplitter;
    private static ControlTemplate? _rowSplitter;

    /// <summary>An invisible divider that shows a thin accent line while the mouse is over it or it is dragged.</summary>
    private static ControlTemplate SplitterTemplate(bool columns)
    {
        string line = columns ? "Width=\"2\" HorizontalAlignment=\"Center\"" : "Height=\"2\" VerticalAlignment=\"Center\"";
        return (columns ? _columnSplitter : _rowSplitter) ?? Store(columns, (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate TargetType=\"GridSplitter\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            "<Border Background=\"Transparent\"><Border x:Name=\"Line\" Background=\"Transparent\" CornerRadius=\"1\" " + line + "/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property=\"IsMouseOver\" Value=\"True\">" +
            "<Setter TargetName=\"Line\" Property=\"Background\" Value=\"{DynamicResource AccentBrush}\"/></Trigger>" +
            "<Trigger Property=\"IsDragging\" Value=\"True\"><Setter TargetName=\"Line\" Property=\"Background\" Value=\"{DynamicResource AccentBrush}\"/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>"));

        static ControlTemplate Store(bool columns, ControlTemplate template)
        {
            if (columns)
            {
                _columnSplitter = template;
            }
            else
            {
                _rowSplitter = template;
            }

            return template;
        }
    }

    /// <summary>Puts a divider in each empty gap column (or row) between two filled ones.</summary>
    private void TrySplit(Grid grid, string path, Dictionary<string, int> counters)
    {
        var children = grid.Children.OfType<UIElement>().ToList();
        if (children.Count < 2)
        {
            return;
        }

        bool Occupies(UIElement child, int index, bool columns)
        {
            int start = columns ? Grid.GetColumn(child) : Grid.GetRow(child);
            int span = columns ? Grid.GetColumnSpan(child) : Grid.GetRowSpan(child);
            return index >= start && index < start + Math.Max(1, span);
        }

        bool IsGap(GridLength length) => length.IsAbsolute && length.Value >= 4 && length.Value <= 32;

        var gaps = new List<(bool Columns, int Index)>();
        for (int c = 1; c < grid.ColumnDefinitions.Count - 1; c++)
        {
            if (IsGap(grid.ColumnDefinitions[c].Width) && !children.Any(ch => Occupies(ch, c, true))
                && children.Any(ch => Occupies(ch, c - 1, true)) && children.Any(ch => Occupies(ch, c + 1, true)))
            {
                gaps.Add((true, c));
            }
        }

        for (int r = 1; r < grid.RowDefinitions.Count - 1; r++)
        {
            if (IsGap(grid.RowDefinitions[r].Height) && !children.Any(ch => Occupies(ch, r, false))
                && children.Where(ch => Occupies(ch, r - 1, false)).Any(HoldsList)
                && children.Where(ch => Occupies(ch, r + 1, false)).Any(HoldsList))
            {
                gaps.Add((false, r));
            }
        }

        if (gaps.Count == 0)
        {
            return;
        }

        grid.SetValue(SetUpProperty, true);
        int number = counters.GetValueOrDefault(path);
        counters[path] = number + 1;
        foreach ((bool columns, int index) in gaps)
        {
            string key = $"split:{path}:{number}:{(columns ? "c" : "r")}{index}";
            GridLength before = columns ? grid.ColumnDefinitions[index - 1].Width : grid.RowDefinitions[index - 1].Height;
            GridLength after = columns ? grid.ColumnDefinitions[index + 1].Width : grid.RowDefinitions[index + 1].Height;
            var info = new SplitInfo(new WeakReference<Grid>(grid), key, columns, index, before, after);
            _splits.Add(info);

            double gap = columns ? grid.ColumnDefinitions[index].Width.Value : grid.RowDefinitions[index].Height.Value;
            var splitter = new GridSplitter
            {
                ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                Focusable = false,
                ShowsPreview = false,
                Cursor = columns ? Cursors.SizeWE : Cursors.SizeNS,
                Template = SplitterTemplate(columns),
            };
            if (columns)
            {
                splitter.Width = gap;
                Grid.SetColumn(splitter, index);
                Grid.SetRowSpan(splitter, Math.Max(1, grid.RowDefinitions.Count));
            }
            else
            {
                splitter.Height = gap;
                Grid.SetRow(splitter, index);
                Grid.SetColumnSpan(splitter, Math.Max(1, grid.ColumnDefinitions.Count));
            }

            splitter.DragCompleted += (_, _) => SaveSplit(info);
            grid.Children.Add(splitter);
            ApplySplit(info);
        }
    }

    private static bool HoldsList(UIElement element)
    {
        if (element is DataGrid or ScrollViewer or ListBox or GroupBox or TabControl or ItemsControl)
        {
            return true;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(element))
        {
            if (child is UIElement ui && HoldsList(ui))
            {
                return true;
            }
        }

        return false;
    }

    private void SaveSplit(SplitInfo info)
    {
        if (_applying || !info.Grid.TryGetTarget(out Grid? grid))
        {
            return;
        }

        GridLength before = info.Columns ? grid.ColumnDefinitions[info.Index - 1].Width : grid.RowDefinitions[info.Index - 1].Height;
        GridLength after = info.Columns ? grid.ColumnDefinitions[info.Index + 1].Width : grid.RowDefinitions[info.Index + 1].Height;
        _store.Set(info.Key, Length(before) + "|" + Length(after));
    }

    private void ApplySplit(SplitInfo info)
    {
        if (!info.Grid.TryGetTarget(out Grid? grid))
        {
            return;
        }

        GridLength before = info.OriginalBefore, after = info.OriginalAfter;
        if (_store.Get(info.Key)?.Split('|') is [{ } b, { } a] && TryLength(b, out GridLength savedBefore) && TryLength(a, out GridLength savedAfter))
        {
            before = savedBefore;
            after = savedAfter;
        }

        if (info.Columns)
        {
            grid.ColumnDefinitions[info.Index - 1].Width = before;
            grid.ColumnDefinitions[info.Index + 1].Width = after;
        }
        else
        {
            grid.RowDefinitions[info.Index - 1].Height = before;
            grid.RowDefinitions[info.Index + 1].Height = after;
        }
    }

    private static string Length(GridLength length) => length.GridUnitType switch
    {
        GridUnitType.Auto => "a",
        GridUnitType.Star => "*" + length.Value.ToString("0.####", CultureInfo.InvariantCulture),
        _ => length.Value.ToString("0.#", CultureInfo.InvariantCulture),
    };

    private static bool TryLength(string text, out GridLength length)
    {
        length = GridLength.Auto;
        if (text == "a")
        {
            return true;
        }

        if (text.StartsWith('*') && double.TryParse(text[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double star) && star > 0)
        {
            length = new GridLength(star, GridUnitType.Star);
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels) && pixels >= 0)
        {
            length = new GridLength(pixels);
            return true;
        }

        return false;
    }

    // ───────────── tables ─────────────

    private static readonly HashSet<string> NameHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "Player", "Character", "Who", "Looted by", "Tank", "Poster", "Killed by", "Who died", "Player / spell", "Owner", "Caster",
    };

    private void OnGridLoaded(DataGrid grid)
    {
        if ((bool)grid.GetValue(SetUpProperty) || grid.Columns.Count == 0)
        {
            return;
        }

        grid.SetValue(SetUpProperty, true);
        _grids.Add(new WeakReference<DataGrid>(grid));
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            DataGridColumn column = grid.Columns[i];
            _columnOriginals.AddOrUpdate(column, new ColumnOriginal(column.Width, column.DisplayIndex, column.Visibility));
        }

        grid.CanUserReorderColumns = true;
        if (grid.Columns.Count >= 10 && grid.FrozenColumnCount == 0)
        {
            grid.FrozenColumnCount = 1; // wide tables keep their name column in view while scrolling sideways
        }

        grid.ColumnReordered += (_, _) => SaveColumns(grid);
        // A column edge dragged by the player (the header's gripper); widths that change by themselves are not saved.
        grid.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, e) =>
        {
            if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null)
            {
                SaveColumns(grid);
            }
        }));
        grid.MouseRightButtonUp += OnGridRightClick;
        grid.ContextMenuOpening += (_, e) =>
        {
            if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null)
            {
                e.Handled = true; // the header shows the layout menu instead of the table's own
            }
        };

        // Rows naming one of your characters are tinted (not on the Characters tab, where every row is yours).
        List<string> namePaths = grid.Columns.OfType<DataGridBoundColumn>()
            .Where(c => NameHeaders.Contains(HeaderText(c.Header)) && c.Binding is Binding { Path.Path: { Length: > 0 } })
            .Select(c => ((Binding)c.Binding).Path.Path)
            .ToList();
        if (namePaths.Count > 0 && !PathOf(grid).StartsWith("Main/Characters", StringComparison.Ordinal))
        {
            UpdateMineBrush();
            grid.LoadingRow += (_, e) =>
            {
                UpdateMineBrush();
                TintRow(e.Row, namePaths);
            };

            // Rows on screen were made before the table finished loading.
            foreach (object item in grid.Items)
            {
                if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                {
                    TintRow(row, namePaths);
                }
            }
        }

        ApplyGrid(grid);
    }

    private void TintRow(DataGridRow row, List<string> namePaths) =>
        row.SetValue(IsMineProperty, namePaths.Any(p => Evaluate(row.Item, p) is string s && s.Length > 0 && _isMine(s)));

    private static Color _mineColor;

    /// <summary>Your characters' rows are tinted with the theme's accent color.</summary>
    private static void UpdateMineBrush()
    {
        if (Application.Current?.TryFindResource("AccentBrush") is SolidColorBrush accent && accent.Color != _mineColor)
        {
            _mineColor = accent.Color;
            Application.Current.Resources["MineRowBrush"] = new SolidColorBrush(accent.Color) { Opacity = 0.16 };
        }
    }

    private void ApplyGrid(DataGrid grid)
    {
        _applying = true;
        try
        {
            string key = GridKey(grid);
            var saved = new Dictionary<string, (string Width, int Index, bool Visible)>(StringComparer.Ordinal);
            foreach (string line in (_store.Get("grid:" + key + ":cols") ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('\t');
                if (parts.Length == 4 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                {
                    saved[parts[0]] = (parts[1], index, parts[3] == "1");
                }
            }

            var order = new List<(DataGridColumn Column, double Rank)>();
            for (int i = 0; i < grid.Columns.Count; i++)
            {
                DataGridColumn column = grid.Columns[i];
                ColumnOriginal original = _columnOriginals.TryGetValue(column, out ColumnOriginal? o) ? o : new ColumnOriginal(column.Width, i, column.Visibility);
                if (saved.TryGetValue(ColumnId(column, i), out var state))
                {
                    column.Width = ParseColumnWidth(state.Width) ?? original.Width;
                    column.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
                    order.Add((column, state.Index));
                }
                else
                {
                    column.Width = original.Width;
                    column.Visibility = original.Visibility;
                    order.Add((column, original.DisplayIndex + 0.5));
                }
            }

            int next = 0;
            foreach ((DataGridColumn column, _) in order.OrderBy(o => o.Rank))
            {
                if (column.DisplayIndex != next)
                {
                    column.DisplayIndex = next;
                }

                next++;
            }

            ApplyTableRowHeight(grid, _store.Get("grid:" + key + ":row"));

            if (_store.Get("grid:" + key + ":sort") is { Length: > 0 } sortText)
            {
                SortDescription[] sorts = sortText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Split('\t'))
                    .Where(p => p.Length == 2)
                    .Select(p => new SortDescription(p[0], p[1] == "D" ? ListSortDirection.Descending : ListSortDirection.Ascending))
                    .ToArray();
                SortKeeper.Remember(grid, sorts);
                if (grid.ItemsSource is not null)
                {
                    SortKeeper.Restore(grid, sorts);
                }
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void SaveColumns(DataGrid grid)
    {
        if (_applying)
        {
            return;
        }

        var text = new StringBuilder();
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            DataGridColumn column = grid.Columns[i];
            text.Append(ColumnId(column, i)).Append('\t').Append(ColumnWidth(column.Width)).Append('\t')
                .Append(column.DisplayIndex.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(column.Visibility == Visibility.Visible ? "1" : "0").Append('\n');
        }

        _store.Set("grid:" + GridKey(grid) + ":cols", text.ToString());
    }

    internal void SaveSort(DataGrid grid)
    {
        if (_applying)
        {
            return;
        }

        string text = string.Join("\n", grid.Items.SortDescriptions.Select(s => s.PropertyName + "\t" + (s.Direction == ListSortDirection.Descending ? "D" : "A")));
        _store.Set("grid:" + GridKey(grid) + ":sort", text);
    }

    private static string ColumnWidth(DataGridLength width) => width.UnitType switch
    {
        DataGridLengthUnitType.Auto => "a",
        DataGridLengthUnitType.SizeToCells => "c",
        DataGridLengthUnitType.SizeToHeader => "h",
        DataGridLengthUnitType.Star => "*" + width.Value.ToString("0.####", CultureInfo.InvariantCulture),
        _ => width.Value.ToString("0.#", CultureInfo.InvariantCulture),
    };

    private static DataGridLength? ParseColumnWidth(string text) => text switch
    {
        "a" => DataGridLength.Auto,
        "c" => DataGridLength.SizeToCells,
        "h" => DataGridLength.SizeToHeader,
        _ when text.StartsWith('*') && double.TryParse(text[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double star) && star > 0
            => new DataGridLength(star, DataGridLengthUnitType.Star),
        _ when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pixels) && pixels > 0 => new DataGridLength(pixels),
        _ => null,
    };

    private static readonly Dictionary<(Type, string), PropertyInfo?> Properties = new();

    /// <summary>The value at a binding path ("Name", "Row.Player") on a row's item, or null.</summary>
    private static object? Evaluate(object? item, string path)
    {
        foreach (string name in path.Split('.'))
        {
            if (item is null)
            {
                return null;
            }

            Type type = item.GetType();
            if (!Properties.TryGetValue((type, name), out PropertyInfo? property))
            {
                property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                Properties[(type, name)] = property;
            }

            item = property?.GetValue(item);
        }

        return item;
    }

    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        bool onHeader = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null;
        bool ownMenu = grid.ContextMenu is not null
            || FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is { ContextMenu: not null }
            || FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is { ContextMenu: not null };
        if (!onHeader && ownMenu)
        {
            return; // the table's own menu (its rows' commands) opens as before; its header has the layout menu
        }

        e.Handled = true;
        TableMenu(grid).IsOpen = true;
    }

    private ContextMenu TableMenu(DataGrid grid)
    {
        var menu = new ContextMenu { PlacementTarget = grid, Placement = PlacementMode.MousePoint };
        var columns = new MenuItem { Header = "Hide/Show Columns" };
        foreach (DataGridColumn column in grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            string title = HeaderText(column.Header);
            var item = new MenuItem
            {
                Header = title.Length > 0 ? title : "(no name)",
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) =>
            {
                bool show = item.IsChecked;
                if (!show && grid.Columns.Count(c => c.Visibility == Visibility.Visible) <= 1)
                {
                    item.IsChecked = true; // a table keeps at least one column
                    return;
                }

                column.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                SaveColumns(grid);
            };
            columns.Items.Add(item);
        }

        menu.Items.Add(columns);

        string key = "grid:" + GridKey(grid) + ":row";
        string current = _store.Get(key) ?? string.Empty;
        var rows = new MenuItem { Header = "Row Height" };
        foreach ((string value, string label) in new[] { ("", "Same as Settings"), ("compact", "Compact"), ("normal", "Normal"), ("roomy", "Roomy") })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = current == value };
            item.Click += (_, _) =>
            {
                _store.Set(key, value);
                ApplyTableRowHeight(grid, value);
            };
            rows.Items.Add(item);
        }

        menu.Items.Add(rows);
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "Copy Table" };
        copy.Click += (_, _) => CopyTable(grid);
        menu.Items.Add(copy);
        return menu;
    }

    /// <summary>The shown columns and every row as tab-separated text, ready to paste into a spreadsheet.</summary>
    private static void CopyTable(DataGrid grid)
    {
        List<DataGridColumn> shown = grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).ToList();
        var text = new StringBuilder();
        text.AppendLine(string.Join("\t", shown.Select(c => HeaderText(c.Header))));
        foreach (object item in grid.Items)
        {
            if (item == CollectionView.NewItemPlaceholder)
            {
                continue;
            }

            text.AppendLine(string.Join("\t", shown.Select(c => CellText(c, item))));
        }

        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard was busy; nothing copied.
        }
    }

    private static string CellText(DataGridColumn column, object item)
    {
        BindingBase? binding = column.ClipboardContentBinding ?? (column as DataGridBoundColumn)?.Binding;
        string? path = (binding as Binding)?.Path?.Path;
        if (string.IsNullOrEmpty(path))
        {
            path = column.SortMemberPath;
        }

        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        object? value = Evaluate(item, path);
        string format = (binding as Binding)?.StringFormat ?? string.Empty;
        string text = value switch
        {
            null => string.Empty,
            IFormattable f when format.Length > 0 && !format.Contains('{') => f.ToString(format, CultureInfo.CurrentCulture),
            _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty,
        };
        return text.Replace('\t', ' ').Replace("\r", " ").Replace('\n', ' ');
    }

    /// <summary>Compact, Normal or Roomy rows for every table (the global setting).</summary>
    public static void ApplyRowHeight(string? value)
    {
        Interop.DarkWindowChrome.RowPadding = PaddingFor(value);
        Interop.DarkWindowChrome.SetLook(Interop.DarkWindowChrome.Scale, Interop.DarkWindowChrome.Compact);
    }

    private static double? PaddingFor(string? value) => value switch
    {
        "compact" => 1,
        "roomy" => 9,
        "normal" => 5,
        _ => null,
    };

    private static void ApplyTableRowHeight(DataGrid grid, string? value)
    {
        if (PaddingFor(value) is double vertical)
        {
            double horizontal = Interop.DarkWindowChrome.Compact ? 4 : 6;
            grid.Resources["GridCellPadding"] = new Thickness(horizontal, vertical, horizontal, vertical);
        }
        else
        {
            grid.Resources.Remove("GridCellPadding");
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T found)
            {
                return found;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    // ───────────── collapsed groups ─────────────

    private string? GroupKey(Expander expander)
    {
        if (expander.DataContext is not CollectionViewGroup)
        {
            return null;
        }

        // The groups this one sits in, outermost first, up to the table or list that groups them.
        var names = new List<string>();
        DependencyObject? node = expander;
        ItemsControl? owner = null;
        while (node is not null)
        {
            if (node is GroupItem { DataContext: CollectionViewGroup group })
            {
                names.Insert(0, Convert.ToString(group.Name, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            if (node is DataGrid or ListBox)
            {
                owner = (ItemsControl)node;
                break;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        if (owner is null || names.Count == 0)
        {
            return null;
        }

        string ownerKey = owner is DataGrid grid ? GridKey(grid) : PathOf(owner) + "|list";
        return "group:" + ownerKey + ":" + string.Join("/", names);
    }

    private void OnExpanderLoaded(Expander expander)
    {
        if (GroupKey(expander) is not { } key)
        {
            return;
        }

        if (!(bool)expander.GetValue(SetUpProperty))
        {
            expander.SetValue(SetUpProperty, true);
            _expanders.Add(new WeakReference<Expander>(expander));
        }

        ApplyExpander(expander, key);
    }

    private void ApplyExpander(Expander expander, string key)
    {
        _applying = true;
        try
        {
            expander.IsExpanded = _store.Get(key) != "0";
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnExpanderToggled(Expander expander, RoutedEventArgs e)
    {
        if (_applying || !ReferenceEquals(e.OriginalSource, expander) || GroupKey(expander) is not { } key)
        {
            return;
        }

        _store.Set(key, expander.IsExpanded ? null : "0");
    }

    // ───────────── tabs ─────────────

    private void SetUpTabs(TabControl tabs, string path)
    {
        tabs.SetValue(SetUpProperty, true);
        _tabControls.Add((new WeakReference<TabControl>(tabs), path));
        List<TabItem> items = tabs.Items.OfType<TabItem>().ToList();
        foreach (TabItem tab in items)
        {
            // Opened from the tab's own header only: the tab's page counts as inside the tab, and must keep its own right-click menus.
            tab.MouseRightButtonUp += (_, e) =>
            {
                if (IsInHeader(tab, e.OriginalSource as DependencyObject))
                {
                    e.Handled = true;
                    TabMenu(tabs, tab, path).IsOpen = true;
                }
            };
            tab.AllowDrop = true;
            tab.PreviewMouseLeftButtonDown += (_, e) => _dragStart = (tab, e.GetPosition(tab));
            tab.PreviewMouseMove += (_, e) => OnTabMouseMove(tab, e);
            tab.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetData(typeof(TabItem)) is TabItem source && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(source), tabs)
                    ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            tab.Drop += (_, e) =>
            {
                if (e.Data.GetData(typeof(TabItem)) is TabItem source && !ReferenceEquals(source, tab)
                    && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(source), tabs))
                {
                    MoveTab(tabs, path, source, tab);
                }

                e.Handled = true;
            };
        }

        ApplyTabs(tabs, path);
    }

    private (TabItem Tab, Point Start)? _dragStart;

    private void OnTabMouseMove(TabItem tab, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not { } start || !ReferenceEquals(start.Tab, tab))
        {
            return;
        }

        Vector moved = e.GetPosition(tab) - start.Start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance * 3 && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance * 3)
        {
            return;
        }

        _dragStart = null;
        DragDrop.DoDragDrop(tab, new DataObject(typeof(TabItem), tab), DragDropEffects.Move);
    }

    private static List<TabItem> Ordered(TabControl tabs) =>
        tabs.Items.OfType<TabItem>().Select((t, i) => (Tab: t, Rank: GetTabOrder(t) >= 0 ? GetTabOrder(t) : i))
            .OrderBy(x => x.Rank).Select(x => x.Tab).ToList();

    private void MoveTab(TabControl tabs, string path, TabItem source, TabItem target)
    {
        List<TabItem> order = Ordered(tabs);
        int to = order.IndexOf(target);
        int from = order.IndexOf(source);
        order.Remove(source);
        order.Insert(from < to ? to : to, source);
        for (int i = 0; i < order.Count; i++)
        {
            SetTabOrder(order[i], i);
        }

        _store.Set("tabs:" + path + ":order", string.Join("\n", order.Select(TitleOf)));
        RefreshTabStrip(tabs);
    }

    private static void RefreshTabStrip(TabControl tabs)
    {
        if (tabs.Items.OfType<TabItem>().FirstOrDefault() is { } first && VisualTreeHelper.GetParent(first) is UIElement strip)
        {
            strip.InvalidateMeasure();
            strip.InvalidateArrange();
        }
    }

    private void ApplyTabs(TabControl tabs, string path)
    {
        List<TabItem> items = tabs.Items.OfType<TabItem>().ToList();
        List<string> saved = (_store.Get("tabs:" + path + ":order") ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var hidden = new HashSet<string>((_store.Get("tabs:" + path + ":hidden") ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            int at = saved.IndexOf(TitleOf(items[i]));
            SetTabOrder(items[i], at >= 0 ? at : saved.Count + i);
            items[i].Visibility = hidden.Contains(TitleOf(items[i])) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (items.All(t => t.Visibility != Visibility.Visible) && items.Count > 0)
        {
            items[0].Visibility = Visibility.Visible;
        }

        if (tabs.SelectedItem is TabItem { Visibility: not Visibility.Visible })
        {
            tabs.SelectedItem = Ordered(tabs).First(t => t.Visibility == Visibility.Visible);
        }

        RefreshTabStrip(tabs);
    }

    /// <summary>True when <paramref name="source"/> is part of the tab's header (its page is not a visual child of the tab).</summary>
    private static bool IsInHeader(TabItem tab, DependencyObject? source)
    {
        while (source is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            if (ReferenceEquals(source, tab))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private ContextMenu TabMenu(TabControl tabs, TabItem tab, string path)
    {
        var menu = new ContextMenu { PlacementTarget = tab, Placement = PlacementMode.MousePoint };
        var show = new MenuItem { Header = "Hide/Show Tabs" };
        foreach (TabItem other in Ordered(tabs))
        {
            var item = new MenuItem { Header = TitleOf(other), IsCheckable = true, IsChecked = other.Visibility == Visibility.Visible, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (!item.IsChecked && tabs.Items.OfType<TabItem>().Count(t => t.Visibility == Visibility.Visible) <= 1)
                {
                    item.IsChecked = true; // a row keeps at least one tab
                    return;
                }

                other.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                _store.Set("tabs:" + path + ":hidden", string.Join("\n", tabs.Items.OfType<TabItem>().Where(t => t.Visibility != Visibility.Visible).Select(TitleOf)));
                if (ReferenceEquals(tabs.SelectedItem, other) && !item.IsChecked)
                {
                    tabs.SelectedItem = Ordered(tabs).First(t => t.Visibility == Visibility.Visible);
                }

                RefreshTabStrip(tabs);
            };
            show.Items.Add(item);
        }

        menu.Items.Add(show);
        if (_popOuts.TryGetValue(tab, out Window? open))
        {
            var back = new MenuItem { Header = "Bring Back" };
            back.Click += (_, _) => open.Close();
            menu.Items.Add(back);
        }
        else if (tab.Content is UIElement)
        {
            var pop = new MenuItem { Header = "Pop Out" };
            pop.Click += (_, _) => PopOut(tab, path);
            menu.Items.Add(pop);
        }

        return menu;
    }

    // ───────────── pop-out windows ─────────────

    private void PopOut(TabItem tab, string path)
    {
        if (tab.Content is not UIElement content || _popOuts.ContainsKey(tab))
        {
            return;
        }

        string title = TitleOf(tab);
        string tabPath = path + "/" + title;
        var placeholder = new StackPanel { Margin = new Thickness(20) };
        placeholder.Children.Add(new TextBlock { Text = title + " is open in its own window.", Margin = new Thickness(0, 0, 0, 10) });
        var bringBack = new Button { Content = "Bring it back", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 5, 14, 5) };
        placeholder.Children.Add(bringBack);

        object? context = (tab.Content as FrameworkElement)?.DataContext ?? tab.DataContext;
        tab.Content = placeholder;
        content.SetValue(PathPrefixProperty, tabPath);
        var host = new Border { Child = content, Padding = new Thickness(8) };
        host.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        var window = new Window
        {
            Title = $"{Core.AppInfo.Name} — {title}",
            Content = host,
            DataContext = context,
            Owner = _main,
            Width = Math.Max(600, _main.ActualWidth * 0.6),
            Height = Math.Max(400, _main.ActualHeight * 0.7),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = true,
            Icon = _main.Icon,
        };
        window.SetResourceReference(Window.BackgroundProperty, "WindowBgBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextPrimaryBrush");
        window.FontFamily = _main.FontFamily;
        Interop.DarkWindowChrome.Apply(window);
        string place = "Tab " + tabPath;
        WindowPlacementKeeper.Attach(window, () => PlacementFor(place), p => StorePlacement(place, p));
        window.PreviewMouseWheel += (_, e) => ZoomOnWheel(content as FrameworkElement, title, e);
        bringBack.Click += (_, _) => window.Close();
        window.Closed += (_, _) =>
        {
            host.Child = null;
            content.ClearValue(PathPrefixProperty);
            tab.Content = content;
            _popOuts.Remove(tab);
        };
        _popOuts[tab] = window;
        window.Show();
        WatchTabSwitches(window);
    }

    /// <summary>"1920,0,1920x1080|1920x1080": the monitor setup, so window places are remembered per setup.</summary>
    public static string DisplaySignature =>
        FormattableString.Invariant(
            $"{SystemParameters.VirtualScreenLeft:0},{SystemParameters.VirtualScreenTop:0},{SystemParameters.VirtualScreenWidth:0}x{SystemParameters.VirtualScreenHeight:0}|{SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}");

    /// <summary>Where a window was last on this monitor setup, else where it was last on any.</summary>
    public WindowPlacementSetting? PlacementFor(string window) => _getPlacement(window + "@" + DisplaySignature) ?? _getPlacement(window);

    public void StorePlacement(string window, WindowPlacementSetting placement)
    {
        _setPlacement(window + "@" + DisplaySignature, placement);
        _setPlacement(window, placement);
    }

    // ───────────── text size per tab ─────────────

    private TabControl? MainTabs => _main.Content is DependencyObject root ? FindTabControl(root) : null;

    private static TabControl? FindTabControl(DependencyObject node)
    {
        if (node is TabControl tabs)
        {
            return tabs;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject d && FindTabControl(d) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (MainTabs is { SelectedItem: TabItem tab })
        {
            ZoomOnWheel(tab.Content as FrameworkElement, TitleOf(tab), e);
        }
    }

    private void ZoomOnWheel(FrameworkElement? content, string title, MouseWheelEventArgs e)
    {
        if (content is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        double zoom = ZoomOf(title);
        zoom = Math.Round(Math.Clamp(zoom + (e.Delta > 0 ? 0.1 : -0.1), 0.7, 1.6), 1);
        _store.Set("zoom:" + title, Math.Abs(zoom - 1.0) < 0.01 ? null : zoom.ToString("0.0", CultureInfo.InvariantCulture));
        SetZoom(content, zoom);
    }

    private double ZoomOf(string title) =>
        double.TryParse(_store.Get("zoom:" + title), NumberStyles.Float, CultureInfo.InvariantCulture, out double z) ? z : 1.0;

    private static void SetZoom(FrameworkElement content, double zoom) =>
        content.LayoutTransform = Math.Abs(zoom - 1.0) < 0.01 ? Transform.Identity : new ScaleTransform(zoom, zoom);

    private void ApplyZoomToMainTabs()
    {
        if (MainTabs is not { } tabs)
        {
            return;
        }

        foreach (TabItem tab in tabs.Items.OfType<TabItem>())
        {
            FrameworkElement? content = tab.Content as FrameworkElement ?? (_popOuts.TryGetValue(tab, out Window? w) ? (w.Content as Border)?.Child as FrameworkElement : null);
            if (content is not null)
            {
                SetZoom(content, ZoomOf(TitleOf(tab)));
            }
        }
    }

    // ───────────── start-up tab, reset and presets ─────────────

    /// <summary>The main tabs' names, in their listed order.</summary>
    public IReadOnlyList<string> MainTabTitles => MainTabs?.Items.OfType<TabItem>().Select(TitleOf).ToList() ?? new List<string>();

    /// <summary>The main tab index to open on: the chosen start-up tab, or null to keep the last one used.</summary>
    public int? StartupTabIndex()
    {
        if (_store.Get(StartupTabKey) is not { Length: > 0 } title || MainTabs is not { } tabs)
        {
            return null;
        }

        List<TabItem> items = tabs.Items.OfType<TabItem>().ToList();
        int index = items.FindIndex(t => TitleOf(t) == title);
        return index >= 0 && items[index].Visibility == Visibility.Visible ? index : null;
    }

    /// <summary>Puts every divider, table, group and tab back to the current layout (after a preset or a reset).</summary>
    private void ReapplyAll()
    {
        ApplyRowHeight(_store.Get(RowHeightKey));
        foreach (SplitInfo split in _splits)
        {
            ApplySplit(split);
        }

        foreach (WeakReference<DataGrid> reference in _grids)
        {
            if (reference.TryGetTarget(out DataGrid? grid))
            {
                ApplyGrid(grid);
                if (_store.Get("grid:" + GridKey(grid) + ":sort") is null)
                {
                    grid.Items.SortDescriptions.Clear();
                    foreach (DataGridColumn column in grid.Columns)
                    {
                        column.SortDirection = null;
                    }

                    SortKeeper.Remember(grid, Array.Empty<SortDescription>());
                }
            }
        }

        foreach (WeakReference<Expander> reference in _expanders)
        {
            if (reference.TryGetTarget(out Expander? expander) && GroupKey(expander) is { } key)
            {
                ApplyExpander(expander, key);
            }
        }

        foreach ((WeakReference<TabControl> reference, string path) in _tabControls)
        {
            if (reference.TryGetTarget(out TabControl? tabs))
            {
                ApplyTabs(tabs, path);
            }
        }

        ApplyZoomToMainTabs();
    }
}
