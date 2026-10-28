using System.Windows;
using System.Windows.Controls;

namespace EQOverwatch.App.Presentation;

/// <summary>
/// Lays its children out as cards in as many columns as fit: one column in a narrow window, two in a wider one, up to
/// <see cref="MaxColumns"/>. Each child is given the width of one column and goes into the currently shortest column, in order, so
/// the layout follows the window as it is resized and no card stretches across the whole window.
/// </summary>
public sealed class ResponsiveColumnsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(420.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnGapProperty = DependencyProperty.Register(
        nameof(ColumnGap), typeof(double), typeof(ResponsiveColumnsPanel),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>A column is never narrower than this; a window too narrow for two of them gets one column.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>Space between columns, and between cards in a column.</summary>
    public double ColumnGap
    {
        get => (double)GetValue(ColumnGapProperty);
        set => SetValue(ColumnGapProperty, value);
    }

    /// <summary>How many columns of at least <paramref name="minColumnWidth"/> fit in <paramref name="availableWidth"/> (1 to <paramref name="maxColumns"/>).</summary>
    public static int ColumnCount(double availableWidth, double minColumnWidth, int maxColumns, double gap)
    {
        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0 || minColumnWidth <= 0)
        {
            return 1;
        }

        int fit = (int)Math.Floor((availableWidth + gap) / (minColumnWidth + gap));
        return Math.Clamp(fit, 1, Math.Max(1, maxColumns));
    }

    /// <summary>
    /// The column (0-based) each card goes into: every card, in order, into the column that is shortest so far (the first on a tie).
    /// </summary>
    public static int[] Place(IReadOnlyList<double> heights, int columns, double gap)
    {
        columns = Math.Max(1, columns);
        var columnHeight = new double[columns];
        var placement = new int[heights.Count];
        for (int i = 0; i < heights.Count; i++)
        {
            int target = 0;
            for (int c = 1; c < columns; c++)
            {
                if (columnHeight[c] < columnHeight[target])
                {
                    target = c;
                }
            }

            placement[i] = target;
            columnHeight[target] += heights[i] + gap;
        }

        return placement;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = ColumnCount(availableSize.Width, MinColumnWidth, MaxColumns, ColumnGap);
        double width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        double columnWidth = (width - (ColumnGap * (columns - 1))) / columns;

        var heights = new List<double>(InternalChildren.Count);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            heights.Add(child.Visibility == Visibility.Collapsed ? 0 : child.DesiredSize.Height);
        }

        return new Size(width, Tallest(heights, columns));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = ColumnCount(finalSize.Width, MinColumnWidth, MaxColumns, ColumnGap);
        double columnWidth = (finalSize.Width - (ColumnGap * (columns - 1))) / columns;

        var heights = new List<double>(InternalChildren.Count);
        foreach (UIElement child in InternalChildren)
        {
            if (Math.Abs(child.DesiredSize.Width - columnWidth) > 0.5 && child.Visibility != Visibility.Collapsed)
            {
                child.Measure(new Size(columnWidth, double.PositiveInfinity)); // the final width differs from the measured one
            }

            heights.Add(child.Visibility == Visibility.Collapsed ? 0 : child.DesiredSize.Height);
        }

        int[] placement = Place(heights, columns, ColumnGap);
        var top = new double[columns];
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            UIElement child = InternalChildren[i];
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            int column = placement[i];
            child.Arrange(new Rect(column * (columnWidth + ColumnGap), top[column], columnWidth, heights[i]));
            top[column] += heights[i] + ColumnGap;
        }

        return finalSize;
    }

    private double Tallest(IReadOnlyList<double> heights, int columns)
    {
        int[] placement = Place(heights, columns, ColumnGap);
        var total = new double[columns];
        for (int i = 0; i < heights.Count; i++)
        {
            total[placement[i]] += heights[i] + ColumnGap;
        }

        return Math.Max(0, total.Max() - ColumnGap);
    }
}
