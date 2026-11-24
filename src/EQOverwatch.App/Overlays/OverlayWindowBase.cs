using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using EQOverwatch.App.Interop;

namespace EQOverwatch.App.Overlays;

/// <summary>
/// Shared behaviour of the two overlays: a borderless, transparent, topmost, non-activating window with two states.
/// <list type="bullet">
/// <item><description><b>Unlocked</b> (positioning): interactive, resizable via grip, dark backdrop, dashed border, drag strip.</description></item>
/// <item><description><b>Locked</b> (gameplay): click-through, not resizable, borderless, near-transparent backdrop.</description></item>
/// </list>
/// The visuals react to <see cref="IsLocked"/> through styles in <c>Themes/OverlayStyles.xaml</c>.
/// </summary>
public abstract class OverlayWindowBase : Window
{
    public static readonly DependencyProperty IsLockedProperty = DependencyProperty.Register(
        nameof(IsLocked), typeof(bool), typeof(OverlayWindowBase),
        new PropertyMetadata(false, (d, _) => ((OverlayWindowBase)d).ApplyLockState()));

    public static readonly DependencyProperty OverlayNameProperty = DependencyProperty.Register(
        nameof(OverlayName), typeof(string), typeof(OverlayWindowBase), new PropertyMetadata("Overlay"));

    protected OverlayWindowBase()
    {
        // AllowsTransparency can only be set before the window is shown, so it lives in the constructor.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // appearing must not pull focus out of EverQuest
        ResizeMode = ResizeMode.CanResizeWithGrip;

        SourceInitialized += (_, _) => ApplyLockState();

        LocationChanged += (_, _) => RaisePlacementChanged();
        SizeChanged += (_, _) => RaisePlacementChanged();
        IsVisibleChanged += (_, _) => RaisePlacementChanged();
    }

    /// <summary>Raised when the window is moved, resized, shown/hidden or locked/unlocked (persist the new state).</summary>
    public event EventHandler? PlacementChanged;

    /// <summary>Raised by the ✕ on the title strip: the owner switches this overlay off.</summary>
    public event EventHandler? CloseRequested;

    private const double MinimizedHeight = 26;
    private double _fullHeight;
    private double _fullMinHeight;
    private bool _isMinimized;

    /// <summary>Shrunk to its title strip. <see cref="FullHeight"/> keeps the height to restore.</summary>
    public bool IsMinimized
    {
        get => _isMinimized;
        set
        {
            if (value == _isMinimized)
            {
                return;
            }

            _isMinimized = value;
            if (value)
            {
                _fullHeight = Height;
                _fullMinHeight = MinHeight;
                MinHeight = 0;
                Height = MinimizedHeight;
            }
            else
            {
                MinHeight = _fullMinHeight;
                Height = Math.Max(_fullHeight, _fullMinHeight);
            }

            if (Content is FrameworkElement root)
            {
                root.Tag = value ? "Minimized" : null;
            }

            RaisePlacementChanged();
        }
    }

    /// <summary>The height to save: the full height even while minimized.</summary>
    public double FullHeight => _isMinimized ? _fullHeight : Height;

    /// <summary>Hooked to the — / + button of the title strip.</summary>
    protected void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        IsMinimized = !IsMinimized;
        if (sender is System.Windows.Controls.Button button)
        {
            button.Content = IsMinimized ? "+" : "—";
        }
    }

    /// <summary>Hooked to the ✕ button of the title strip.</summary>
    protected void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>True = gameplay mode (click-through). False = reposition/resize mode.</summary>
    public bool IsLocked
    {
        get => (bool)GetValue(IsLockedProperty);
        set => SetValue(IsLockedProperty, value);
    }

    /// <summary>Shown in the drag strip while unlocked.</summary>
    public string OverlayName
    {
        get => (string)GetValue(OverlayNameProperty);
        set => SetValue(OverlayNameProperty, value);
    }

    /// <summary>Hooked to the drag strip's MouseLeftButtonDown in XAML.</summary>
    protected void OnTitleStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsLocked && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void RaisePlacementChanged() => PlacementChanged?.Invoke(this, EventArgs.Empty);

    private void ApplyLockState()
    {
        ResizeMode = IsLocked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;

        // Before SourceInitialized there is no HWND yet; the SourceInitialized handler applies the state then.
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            Win32OverlayHelper.SetClickThrough(this, IsLocked);
        }

        RaisePlacementChanged();
    }
}
