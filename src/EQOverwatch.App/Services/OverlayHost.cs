using System.Windows;
using EQOverwatch.App.Overlays;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.Services;

/// <summary>Owns the overlay windows: restores their saved state, captures it for saving, and closes them.</summary>
public sealed partial class OverlayHost : IOverlayHost
{
    private readonly IReadOnlyDictionary<OverlayKind, OverlayWindowBase> _windows;
    private readonly Dictionary<string, OverlayWindowBase> _custom = new(StringComparer.Ordinal);
    private bool _locked;

    /// <summary>Makes the window of one of your own overlays (id, name); without it those overlays get no window.</summary>
    public Func<string, string, OverlayWindowBase>? CustomFactory { get; set; }

    public event EventHandler<OverlayKind>? CloseRequested;

    public event EventHandler<string>? CustomCloseRequested;

    public OverlayHost(IReadOnlyDictionary<OverlayKind, OverlayWindowBase> windows)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        foreach (OverlayKind kind in OverlayKindExtensions.All)
        {
            if (!windows.ContainsKey(kind))
            {
                throw new ArgumentException($"No window was supplied for the {kind} overlay.", nameof(windows));
            }
        }

        foreach ((OverlayKind kind, OverlayWindowBase window) in windows)
        {
            window.PlacementChanged += (_, _) => PlacementChanged?.Invoke(this, EventArgs.Empty);
            window.CloseRequested += (_, _) => CloseRequested?.Invoke(this, kind);
        }
    }

    public event EventHandler? PlacementChanged;

    public string DisplaySignature =>
        FormattableString.Invariant(
            $"{SystemParameters.VirtualScreenLeft:0},{SystemParameters.VirtualScreenTop:0},{SystemParameters.VirtualScreenWidth:0}x{SystemParameters.VirtualScreenHeight:0}|{SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}");

    public OverlaySettings Capture(OverlayKind kind) => CaptureWindow(Get(kind));

    private static OverlaySettings CaptureWindow(OverlayWindowBase window) => new()
    {
        Left = window.Left,
        Top = window.Top,
        Width = window.Width,
        Height = window.FullHeight,
        IsVisible = window.IsVisible,
        IsLocked = window.IsLocked,
        IsMinimized = window.IsMinimized,
    };

    public void Apply(OverlayKind kind, OverlaySettings settings)
    {
        ApplyWindow(Get(kind), settings, kind.Defaults());
        SetVisible(kind, Clamped(Get(kind), settings, kind.Defaults()).IsVisible);
    }

    private static OverlaySettings Clamped(OverlayWindowBase window, OverlaySettings settings, OverlaySettings defaults)
    {
        var screen = new ScreenRect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        return OverlayPlacement.Clamp(settings, screen, window.MinWidth, window.MinHeight, defaults);
    }

    private static void ApplyWindow(OverlayWindowBase window, OverlaySettings settings, OverlaySettings defaults)
    {
        window.IsMinimized = false;
        OverlaySettings clamped = Clamped(window, settings, defaults);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = clamped.Left;
        window.Top = clamped.Top;
        window.Width = clamped.Width;
        window.Height = clamped.Height;
        window.IsLocked = clamped.IsLocked;
        window.IsMinimized = settings.IsMinimized;
    }

    public void ShowCustom(string id, string name, OverlaySettings placement)
    {
        if (CustomFactory is null)
        {
            return;
        }

        if (!_custom.TryGetValue(id, out OverlayWindowBase? window))
        {
            window = CustomFactory(id, name);
            window.PlacementChanged += (_, _) => PlacementChanged?.Invoke(this, EventArgs.Empty);
            window.CloseRequested += (_, _) => CustomCloseRequested?.Invoke(this, id);
            _custom[id] = window;
            ApplyWindow(window, placement with { IsLocked = _locked }, OverlaySettings.DefaultTimer);
        }

        window.OverlayName = name;
        window.Title = $"{EQOverwatch.Core.AppInfo.Name} — {name}";
        SetCustomVisible(id, placement.IsVisible);
    }

    public void SetCustomVisible(string id, bool visible)
    {
        if (!_custom.TryGetValue(id, out OverlayWindowBase? window))
        {
            return;
        }

        if (visible && !window.IsVisible)
        {
            window.Show();
        }
        else if (!visible)
        {
            window.Hide();
        }
    }

    public void RemoveCustom(string id)
    {
        if (_custom.Remove(id, out OverlayWindowBase? window))
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    public OverlaySettings? CaptureCustom(string id) => _custom.TryGetValue(id, out OverlayWindowBase? window) ? CaptureWindow(window) : null;

    public void SetLocked(bool locked)
    {
        _locked = locked;
        foreach (OverlayWindowBase window in _windows.Values.Concat(_custom.Values))
        {
            window.IsLocked = locked;
        }
    }

    public void SetVisible(OverlayKind kind, bool visible)
    {
        OverlayWindowBase window = Get(kind);
        if (visible)
        {
            if (!window.IsVisible)
            {
                window.Show();
            }
        }
        else
        {
            window.Hide();
        }
    }

    public void SetOpacity(OverlayKind kind, double opacity)
    {
        OverlayWindowBase window = Get(kind);
        double value = Math.Clamp(opacity, 0.0, 1.0);
        if (Math.Abs(window.Opacity - value) > 0.001)
        {
            window.Opacity = value;
        }
    }

    // A hook an edition fills in to close windows of its own (it does nothing in this one).
    partial void CloseEditionWindows();

    public void CloseAll()
    {
        CloseEditionWindows();
        foreach (string id in _custom.Keys.ToList())
        {
            RemoveCustom(id);
        }

        foreach (OverlayWindowBase window in _windows.Values)
        {
            try
            {
                window.Close();
            }
            catch (InvalidOperationException)
            {
                // already closed
            }
        }
    }

    private OverlayWindowBase Get(OverlayKind kind) => _windows[kind];
}
