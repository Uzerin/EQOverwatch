using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace EQOverwatch.App.Presentation;

/// <summary>
/// Keeps the column a table was sorted by when its rows are replaced. Many tables get a new list when they refresh (a new fight, a
/// new log line); a new list means a new view, which would drop the sort and jump back to the list's own order. Turned on for every
/// DataGrid by the shared DataGrid style.
/// </summary>
public static class SortKeeper
{
    public static readonly DependencyProperty KeepProperty = DependencyProperty.RegisterAttached(
        "Keep", typeof(bool), typeof(SortKeeper), new PropertyMetadata(false, OnKeepChanged));

    /// <summary>The sort last chosen by clicking a header: (sort member, direction), first to last.</summary>
    private static readonly DependencyProperty SavedProperty = DependencyProperty.RegisterAttached(
        "Saved", typeof(SortDescription[]), typeof(SortKeeper), new PropertyMetadata(null));

    private static readonly DependencyPropertyDescriptor ItemsSourceDescriptor =
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));

    /// <summary>Raised after the player sorted a table by clicking a header (the layout remembers it).</summary>
    public static event Action<DataGrid>? Sorted;

    /// <summary>Makes <paramref name="sorts"/> the sort this grid keeps when its rows are replaced.</summary>
    public static void Remember(DataGrid grid, SortDescription[] sorts) => grid.SetValue(SavedProperty, sorts);

    public static bool GetKeep(DependencyObject element) => (bool)element.GetValue(KeepProperty);

    public static void SetKeep(DependencyObject element, bool value) => element.SetValue(KeepProperty, value);

    private static void OnKeepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            grid.Sorting += OnSorting;
            ItemsSourceDescriptor.AddValueChanged(grid, OnItemsSourceChanged);
        }
        else
        {
            grid.Sorting -= OnSorting;
            ItemsSourceDescriptor.RemoveValueChanged(grid, OnItemsSourceChanged);
        }
    }

    private static void OnSorting(object? sender, DataGridSortingEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            // The grid sorts after this event; remember the result once it has.
            grid.Dispatcher.BeginInvoke(() =>
            {
                grid.SetValue(SavedProperty, grid.Items.SortDescriptions.ToArray());
                Sorted?.Invoke(grid);
            });
        }
    }

    private static void OnItemsSourceChanged(object? sender, EventArgs e)
    {
        if (sender is not DataGrid grid || grid.GetValue(SavedProperty) is not SortDescription[] { Length: > 0 } saved || grid.ItemsSource is null)
        {
            return;
        }

        Restore(grid, saved);
    }

    /// <summary>Puts the saved sort back on the grid's (new) view and its column headers.</summary>
    public static void Restore(DataGrid grid, IReadOnlyList<SortDescription> saved)
    {
        using (grid.Items.DeferRefresh())
        {
            grid.Items.SortDescriptions.Clear();
            foreach (SortDescription sort in saved)
            {
                grid.Items.SortDescriptions.Add(sort);
            }
        }

        foreach (DataGridColumn column in grid.Columns)
        {
            SortDescription? match = saved.Cast<SortDescription?>().FirstOrDefault(s => s!.Value.PropertyName == column.SortMemberPath);
            column.SortDirection = match?.Direction;
        }
    }
}
