using System.Windows;
using System.Windows.Controls;

namespace EQOverwatch.App.Controls;

/// <summary>
/// Lays out a tab strip that never moves its rows. Up to <see cref="Columns"/> tabs that fit on one line sit left to right at
/// their natural widths; more tabs (or ones that do not fit) form an evenly spaced grid of <see cref="Columns"/> per row, in
/// their listed order. WPF's own
/// TabPanel moves the selected tab's row to the bottom, which this replaces.
/// </summary>
public sealed class StaticTabPanel : Panel
{
    /// <summary>Tabs per row once they no longer fit on one line.</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(StaticTabPanel),
        new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    private bool _grid;
    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Visible();
        double total = 0, widest = 0;
        _rowHeight = 0;
        foreach (var child in visible)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            total += child.DesiredSize.Width;
            widest = Math.Max(widest, child.DesiredSize.Width);
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }

        _grid = visible.Count > Columns || (visible.Count > 1 && total > availableSize.Width);
        if (!_grid)
        {
            return new Size(total, _rowHeight);
        }

        var (columns, rows) = Shape(visible.Count);
        var width = double.IsInfinity(availableSize.Width) ? widest * columns : availableSize.Width;
        foreach (var child in visible)
        {
            child.Measure(new Size(width / columns, _rowHeight));
        }

        return new Size(width, rows * _rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Visible();
        if (!_grid)
        {
            double x = 0;
            foreach (var child in visible)
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, _rowHeight));
                x += child.DesiredSize.Width;
            }

            return finalSize;
        }

        var (columns, _) = Shape(visible.Count);
        var cell = finalSize.Width / columns;
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Arrange(new Rect(i % columns * cell, i / columns * _rowHeight, cell, _rowHeight));
        }

        return finalSize;
    }

    private (int Columns, int Rows) Shape(int count)
    {
        var columns = Math.Max(1, Math.Min(Columns, count));
        return (columns, (count + columns - 1) / columns);
    }

    private List<UIElement> Visible()
    {
        var list = new List<UIElement>(InternalChildren.Count);
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Collapsed)
            {
                list.Add(child);
            }
            else
            {
                child.Arrange(new Rect());
            }
        }

        return list;
    }
}
