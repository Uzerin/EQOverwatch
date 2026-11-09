using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.Controls;

/// <summary>
/// A color swatch that opens a color wheel when clicked: the hue goes around the wheel, the saturation from the center out, and a slider sets
/// the brightness. <see cref="Hex"/> ("#RRGGBB") is read and written, so it binds two-way to a color setting.
/// </summary>
public partial class ColorSwatchPicker : UserControl
{
    private const int WheelSize = 180;

    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorSwatchPicker),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHexChanged));

    private double _hue;        // 0..360
    private double _saturation; // 0..1
    private double _value = 1;  // 0..1
    private bool _internal;
    private bool _dragging;
    private double _wheelValue = -1;

    public ColorSwatchPicker()
    {
        // The slider's Value="1" in XAML raises ValueChanged while the control is still being built, before Preview and HexReadout
        // exist; ignore it (it used to crash the Settings tab and the Colors window when they first opened).
        _internal = true;
        try
        {
            InitializeComponent();
        }
        finally
        {
            _internal = false;
        }
    }

    /// <summary>The color as "#RRGGBB". Text that is not a color is ignored (the swatch keeps its last color).</summary>
    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorSwatchPicker)d;
        if (picker._internal || !ThemeColors.TryParse(e.NewValue as string, out Rgba color))
        {
            return;
        }

        // The wheel's own choice coming back through the binding: keep the exact hue and saturation (they are lost at black and gray).
        HsvToRgb(picker._hue, picker._saturation, picker._value, out byte r, out byte g, out byte b);
        if (r == color.R && g == color.G && b == color.B)
        {
            picker.ShowColor(updateSlider: false);
            return;
        }

        RgbToHsv(color.R, color.G, color.B, out picker._hue, out picker._saturation, out picker._value);
        picker.ShowColor(updateSlider: true);
    }

    private void OnSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (ThemeColors.TryParse(Hex, out Rgba color))
        {
            RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);
        }

        Pop.IsOpen = true;
        ShowColor(updateSlider: true);
    }

    private void OnWheelDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        ((UIElement)sender).CaptureMouse();
        PickAt(e.GetPosition((IInputElement)sender));
    }

    private void OnWheelMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            PickAt(e.GetPosition((IInputElement)sender));
        }
    }

    private void OnWheelUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_internal)
        {
            return;
        }

        _value = e.NewValue;
        ShowColor(updateSlider: false);
        Publish();
    }

    private void PickAt(Point p)
    {
        double radius = WheelSize / 2.0;
        double dx = p.X - radius;
        double dy = p.Y - radius;
        double distance = Math.Min(Math.Sqrt((dx * dx) + (dy * dy)), radius);
        _hue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360.0) % 360.0;
        _saturation = distance / radius;
        ShowColor(updateSlider: false);
        Publish();
    }

    /// <summary>Writes the chosen color to <see cref="Hex"/>.</summary>
    private void Publish()
    {
        HsvToRgb(_hue, _saturation, _value, out byte r, out byte g, out byte b);
        _internal = true;
        try
        {
            SetCurrentValue(HexProperty, $"#{r:X2}{g:X2}{b:X2}");
        }
        finally
        {
            _internal = false;
        }
    }

    /// <summary>Redraws the swatch, the wheel (when the brightness changed), the marker and the readout.</summary>
    private void ShowColor(bool updateSlider)
    {
        HsvToRgb(_hue, _saturation, _value, out byte r, out byte g, out byte b);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        Swatch.Background = brush;
        Preview.Background = brush;
        HexReadout.Text = $"#{r:X2}{g:X2}{b:X2}";

        if (updateSlider)
        {
            _internal = true;
            Brightness.Value = _value;
            _internal = false;
        }

        if (Pop.IsOpen && Math.Abs(_wheelValue - _value) > 0.004)
        {
            Wheel.Source = BuildWheel(_value);
            _wheelValue = _value;
        }

        double radius = WheelSize / 2.0;
        double angle = _hue * Math.PI / 180.0;
        Canvas.SetLeft(Marker, radius + (Math.Cos(angle) * _saturation * radius) - (Marker.Width / 2));
        Canvas.SetTop(Marker, radius + (Math.Sin(angle) * _saturation * radius) - (Marker.Height / 2));
    }

    private static BitmapSource BuildWheel(double value)
    {
        var pixels = new byte[WheelSize * WheelSize * 4];
        double radius = WheelSize / 2.0;
        for (int y = 0; y < WheelSize; y++)
        {
            for (int x = 0; x < WheelSize; x++)
            {
                double dx = x + 0.5 - radius;
                double dy = y + 0.5 - radius;
                double distance = Math.Sqrt((dx * dx) + (dy * dy));
                int i = ((y * WheelSize) + x) * 4;
                if (distance > radius)
                {
                    continue; // outside the circle stays transparent
                }

                double hue = (Math.Atan2(dy, dx) * 180.0 / Math.PI + 360.0) % 360.0;
                HsvToRgb(hue, distance / radius, value, out byte r, out byte g, out byte b);
                double edge = Math.Clamp(radius - distance, 0.0, 1.0); // a soft rim
                byte alpha = (byte)Math.Round(255 * edge);
                pixels[i] = (byte)(b * alpha / 255);     // premultiplied BGRA
                pixels[i + 1] = (byte)(g * alpha / 255);
                pixels[i + 2] = (byte)(r * alpha / 255);
                pixels[i + 3] = alpha;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(WheelSize, WheelSize, 96, 96, PixelFormats.Pbgra32, null, pixels, WheelSize * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Hue 0 to 360, saturation and value 0 to 1.</summary>
    internal static void HsvToRgb(double hue, double saturation, double value, out byte r, out byte g, out byte b)
    {
        double c = value * saturation;
        double h = (hue % 360.0) / 60.0;
        double x = c * (1 - Math.Abs((h % 2) - 1));
        double m = value - c;
        (double rr, double gg, double bb) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        r = (byte)Math.Round((rr + m) * 255);
        g = (byte)Math.Round((gg + m) * 255);
        b = (byte)Math.Round((bb + m) * 255);
    }

    internal static void RgbToHsv(byte r, byte g, byte b, out double hue, out double saturation, out double value)
    {
        double rr = r / 255.0, gg = g / 255.0, bb = b / 255.0;
        double max = Math.Max(rr, Math.Max(gg, bb));
        double min = Math.Min(rr, Math.Min(gg, bb));
        double delta = max - min;
        value = max;
        saturation = max <= 0 ? 0 : delta / max;
        if (delta <= 0)
        {
            hue = 0;
            return;
        }

        hue = max == rr ? 60 * (((gg - bb) / delta) % 6)
            : max == gg ? 60 * (((bb - rr) / delta) + 2)
            : 60 * (((rr - gg) / delta) + 4);
        if (hue < 0)
        {
            hue += 360;
        }
    }
}
