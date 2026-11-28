using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Maps;

namespace EQOverwatch.App.Controls;

/// <summary>
/// Draws a zone map for the Maps tab: the layer being edited bright, the other layers dim gray. Its labels can be dragged; a click on an
/// empty spot places a new label; the mouse wheel zooms and dragging with the right button moves the map. Double-click with the right
/// button fits the whole map again.
/// </summary>
public sealed class MapCanvas : FrameworkElement
{
    public static readonly DependencyProperty MapsProperty = DependencyProperty.Register(
        nameof(Maps), typeof(MapsViewModel), typeof(MapCanvas), new FrameworkPropertyMetadata(null, OnMapsChanged));

    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1C)));
    private static readonly Pen DimLine = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46))), 1));
    private static readonly Brush DimText = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x60, 0x6B)));
    private static readonly Brush HintText = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x91, 0x9C)));
    private static readonly Brush PlayerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4A)));
    private static readonly Typeface Face = new("Segoe UI");

    private const double DragThreshold = 3;

    private string? _fittedZone;
    private double _scale = 1;
    private Point _offset; // screen point of map (0, 0)
    private MapLabelItem? _dragging;
    private Point _pressAt;
    private bool _moved;
    private bool _panning;
    private Point _panFrom;
    private Rect _bounds;

    public MapCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
    }

    public MapsViewModel? Maps
    {
        get => (MapsViewModel?)GetValue(MapsProperty);
        set => SetValue(MapsProperty, value);
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static void OnMapsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (MapCanvas)d;
        if (e.OldValue is MapsViewModel old)
        {
            old.SceneChanged -= canvas.OnSceneChanged;
        }

        if (e.NewValue is MapsViewModel maps)
        {
            maps.SceneChanged += canvas.OnSceneChanged;
        }

        canvas.InvalidateVisual();
    }

    private void OnSceneChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 500 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _fittedZone = null; // fit again to the new size
    }

    // ───────────────────────── drawing ─────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (Maps is not { } maps || maps.ShownZone is null)
        {
            string empty = Maps?.EmptyText is { Length: > 0 } text ? text : "No map to show.";
            dc.DrawText(Text(empty, 13, HintText, ppd), new Point(12, 10));
            return;
        }

        if (_fittedZone != FitKey(maps))
        {
            Fit(maps);
        }

        IReadOnlyList<MapLayer> layers = maps.Layers;
        int active = maps.SelectedLayer;

        // Other layers first, dim, so the one being edited sits on top.
        for (int i = 0; i < layers.Count; i++)
        {
            if (i == active)
            {
                continue;
            }

            foreach (MapLine line in layers[i].Lines)
            {
                dc.DrawLine(DimLine, ToScreen(line.X1, line.Y1), ToScreen(line.X2, line.Y2));
            }

            foreach (MapLabel label in layers[i].Labels)
            {
                Point at = ToScreen(label.X, label.Y);
                dc.DrawEllipse(DimText, null, at, 2, 2);
                dc.DrawText(Text(label.Text, FontSize(label.Size), DimText, ppd), new Point(at.X + 5, at.Y - FontSize(label.Size) * 0.7));
            }
        }

        foreach (MapLine line in layers[active].Lines)
        {
            dc.DrawLine(LinePen(line), ToScreen(line.X1, line.Y1), ToScreen(line.X2, line.Y2));
        }

        foreach (MapLabelItem label in maps.Labels)
        {
            Point at = ToScreen(label.X, label.Y);
            Brush brush = LabelBrush(label.Hex);
            if (ReferenceEquals(label, maps.SelectedLabel))
            {
                var pen = new Pen(brush, 1.2) { DashStyle = DashStyles.Dash };
                dc.DrawEllipse(null, pen, at, 8, 8);
            }

            dc.DrawEllipse(brush, null, at, 3.5, 3.5);
            dc.DrawText(Text(label.Text, FontSize(label.Size), brush, ppd), new Point(at.X + 7, at.Y - FontSize(label.Size) * 0.7));
        }

        // Possible Locations › Show all on map: every spawn spot of the zone, named mobs labeled.
        foreach (MapSpawnDot dot in maps.VisibleSpawnDots)
        {
            Point at = ToScreen(dot.X, dot.Y);
            dc.DrawEllipse(dot.Label.Length > 0 ? SpawnNamedBrush : SpawnBrush, null, at, dot.Label.Length > 0 ? 3.5 : 2.5, dot.Label.Length > 0 ? 3.5 : 2.5);
            if (dot.Label.Length > 0)
            {
                dc.DrawText(Text(dot.Label, 11, SpawnNamedBrush, ppd), new Point(at.X + 6, at.Y - 8));
            }
        }

        // Each character's last known /loc in this zone, with its name.
        foreach (MapPlayerSpot player in maps.PlayerSpots)
        {
            Point at = ToScreen(player.X, player.Y);
            dc.DrawEllipse(null, new Pen(PlayerBrush, 2), at, 5, 5);
            dc.DrawText(Text(player.Name, 11, PlayerBrush, ppd), new Point(at.X + 7, at.Y + 2));
        }

        if (maps.Pending is { } pending && maps.SelectedLabel is null)
        {
            Point at = ToScreen(pending.X, pending.Y);
            var pen = new Pen(LabelBrush(maps.EditColorHex), 1.5);
            dc.DrawEllipse(null, pen, at, 6, 6);
            dc.DrawLine(pen, new Point(at.X, at.Y - 10), new Point(at.X, at.Y + 10));
            dc.DrawLine(pen, new Point(at.X - 10, at.Y), new Point(at.X + 10, at.Y));
        }
    }

    private static readonly Brush SpawnBrush = FrozenBrush(Color.FromArgb(0xB0, 0xF5, 0x9E, 0x0B));
    private static readonly Brush SpawnNamedBrush = FrozenBrush(Color.FromRgb(0xF9, 0x73, 0x16));

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string FitKey(MapsViewModel maps) => maps.ShownZone + "|" + maps.SelectedPack?.Folder;

    private static double FontSize(int size) => size switch { 1 => 10, 3 => 14, _ => 12 };

    private FormattedText Text(string text, double size, Brush brush, double ppd) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush, ppd);

    private static readonly Dictionary<int, Pen> LinePens = new();

    private static Pen LinePen(MapLine line)
    {
        int key = (line.R << 16) | (line.G << 8) | line.B;
        if (LinePens.TryGetValue(key, out Pen? cached))
        {
            return cached;
        }

        // Lines keep their own color, made light enough to see on the dark background.
        byte r = line.R, g = line.G, b = line.B;
        if (r * 0.3 + g * 0.59 + b * 0.11 < 110)
        {
            (r, g, b) = ((byte)(0x9A + r / 4), (byte)(0xA3 + g / 4), (byte)(0xB5 + b / 5));
        }

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(r, g, b)), 1.4);
        pen.Freeze();
        LinePens[key] = pen;
        return pen;
    }

    private static Brush LabelBrush(string hex)
    {
        (byte r, byte g, byte b) = MapsViewModel.ParseHex(hex);
        if (r * 0.3 + g * 0.59 + b * 0.11 < 60)
        {
            (r, g, b) = (0xE0, 0xE0, 0xE0); // a black label would vanish on the dark background
        }

        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ───────────────────────── map ↔ screen ─────────────────────────

    private void Fit(MapsViewModel maps)
    {
        _fittedZone = FitKey(maps);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Take(double x, double y)
        {
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        foreach (MapLayer layer in maps.Layers)
        {
            foreach (MapLine line in layer.Lines)
            {
                Take(line.X1, line.Y1);
                Take(line.X2, line.Y2);
            }

            foreach (MapLabel label in layer.Labels)
            {
                Take(label.X, label.Y);
            }
        }

        if (minX > maxX)
        {
            (minX, minY, maxX, maxY) = (-500, -500, 500, 500);
        }

        _bounds = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        const double Pad = 24;
        double width = Math.Max(50, ActualWidth - 2 * Pad), height = Math.Max(50, ActualHeight - 2 * Pad);
        _scale = Math.Min(width / _bounds.Width, height / _bounds.Height);
        _offset = new Point(
            Pad + (width - _bounds.Width * _scale) / 2 - _bounds.X * _scale,
            Pad + (height - _bounds.Height * _scale) / 2 - _bounds.Y * _scale);
    }

    private Point ToScreen(double x, double y) => new(_offset.X + x * _scale, _offset.Y + y * _scale);

    private (double X, double Y) ToMap(Point p) => ((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);

    private MapLabelItem? LabelAt(Point p)
    {
        if (Maps is not { } maps)
        {
            return null;
        }

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = maps.Labels.Count - 1; i >= 0; i--)
        {
            MapLabelItem label = maps.Labels[i];
            Point at = ToScreen(label.X, label.Y);
            double size = FontSize(label.Size);
            FormattedText text = Text(label.Text, size, Brushes.White, ppd);
            var hit = new Rect(at.X - 7, at.Y - Math.Max(7, size * 0.7), text.Width + 16, Math.Max(14, text.Height));
            if (hit.Contains(p))
            {
                return label;
            }
        }

        return null;
    }

    // ───────────────────────── mouse ─────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Maps?.ShownZone is null)
        {
            return;
        }

        Focus();
        _pressAt = e.GetPosition(this);
        _moved = false;
        _dragging = LabelAt(_pressAt);
        if (_dragging is not null)
        {
            Maps.SelectLabelCommand.Execute(_dragging);
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        if (_panning)
        {
            _offset = new Point(_offset.X + p.X - _panFrom.X, _offset.Y + p.Y - _panFrom.Y);
            _panFrom = p;
            InvalidateVisual();
            return;
        }

        if (IsMouseCaptured && _dragging is not null && Maps is { } maps)
        {
            if (!_moved && (p - _pressAt).Length < DragThreshold)
            {
                return;
            }

            _moved = true;
            (double x, double y) = ToMap(p);
            maps.MoveLabel(_dragging, x, y);
            return;
        }

        Cursor = Maps?.ShownZone is not null && LabelAt(p) is not null ? Cursors.SizeAll : Cursors.Cross;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
        {
            return;
        }

        ReleaseMouseCapture();
        if (_dragging is null && Maps is { } maps && (e.GetPosition(this) - _pressAt).Length < DragThreshold)
        {
            (double x, double y) = ToMap(_pressAt);
            maps.PlaceAt(x, y);
        }

        _dragging = null;
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (e.ClickCount == 2)
        {
            _fittedZone = null;
            InvalidateVisual();
        }
        else
        {
            _panning = true;
            _panFrom = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.ScrollAll;
        }

        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;
        }

        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Maps?.ShownZone is null)
        {
            return;
        }

        Point p = e.GetPosition(this);
        (double x, double y) = ToMap(p);
        double factor = e.Delta > 0 ? 1.25 : 0.8;
        _scale = Math.Clamp(_scale * factor, 0.0005, 50);
        _offset = new Point(p.X - x * _scale, p.Y - y * _scale); // the spot under the mouse stays put
        InvalidateVisual();
        e.Handled = true;
    }
}
