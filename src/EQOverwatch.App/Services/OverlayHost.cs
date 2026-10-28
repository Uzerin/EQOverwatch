using System.Windows;
using EQOverwatch.App.Overlays;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.Services;

/// <summary>Owns the six overlay windows: restores their saved state, captures it for saving, and closes them.</summary>
public sealed class OverlayHost : IOverlayHost
{
    private readonly IReadOnlyDictionary<OverlayKind, OverlayWindowBase> _windows;

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

        foreach (OverlayWindowBase window in windows.Values)
        {
            window.PlacementChanged += (_, _) => PlacementChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? PlacementChanged;

    public OverlaySettings Capture(OverlayKind kind)
    {
        OverlayWindowBase window = Get(kind);
        return new OverlaySettings
        {
            Left = window.Left,
            Top = window.Top,
            Width = window.Width,
            Height = window.Height,
            IsVisible = window.IsVisible,
            IsLocked = window.IsLocked,
        };
    }

    public void Apply(OverlayKind kind, OverlaySettings settings)
    {
        OverlayWindowBase window = Get(kind);
        OverlaySettings defaults = kind.Category() is { } category ? OverlaySettings.ForCategory(category) : OverlaySettings.DefaultDps;

        // SystemParameters.VirtualScreen* spans every monitor, in the same device-independent units as Window.Left/Top.
        var screen = new ScreenRect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        OverlaySettings clamped = OverlayPlacement.Clamp(settings, screen, window.MinWidth, window.MinHeight, defaults);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = clamped.Left;
        window.Top = clamped.Top;
        window.Width = clamped.Width;
        window.Height = clamped.Height;
        window.IsLocked = clamped.IsLocked;

        SetVisible(kind, clamped.IsVisible);
    }

    public void SetLocked(bool locked)
    {
        foreach (OverlayWindowBase window in _windows.Values)
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

    public void CloseAll()
    {
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
