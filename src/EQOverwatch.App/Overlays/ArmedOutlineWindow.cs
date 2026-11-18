using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EQOverwatch.App.Interop;

namespace EQOverwatch.App.Overlays;

/// <summary>
/// The thin red outline round an overlay while the auto-click is on. A window of its own, so it stays full red when the overlay fades
/// out of combat; it is click-through and follows the overlay when it moves, is resized or hidden.
/// </summary>
public sealed class ArmedOutlineWindow : Window
{
    private const double Inflate = 2;
    private OverlayWindowBase? _target;

    public ArmedOutlineWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "EQ Overwatch auto-click outline";
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0x4B, 0x4A)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
        };
        SourceInitialized += (_, _) => Win32OverlayHelper.SetClickThrough(this, true);
    }

    /// <summary>The overlay to go round, or null to hide.</summary>
    public void Follow(OverlayWindowBase? target)
    {
        if (_target is not null)
        {
            _target.LocationChanged -= OnTargetChanged;
            _target.SizeChanged -= OnTargetChanged;
            _target.IsVisibleChanged -= OnTargetVisibleChanged;
        }

        _target = target;
        if (target is not null)
        {
            target.LocationChanged += OnTargetChanged;
            target.SizeChanged += OnTargetChanged;
            target.IsVisibleChanged += OnTargetVisibleChanged;
        }

        Place();
    }

    private void OnTargetChanged(object? sender, EventArgs e) => Place();

    private void OnTargetVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Place();

    private void Place()
    {
        if (_target is not { IsVisible: true } target)
        {
            Hide();
            return;
        }

        Left = target.Left - Inflate;
        Top = target.Top - Inflate;
        Width = target.ActualWidth + (2 * Inflate);
        Height = target.ActualHeight + (2 * Inflate);
        if (!IsVisible)
        {
            Show();
        }
    }
}
