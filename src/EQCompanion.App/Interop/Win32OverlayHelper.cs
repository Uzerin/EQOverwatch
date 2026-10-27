using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EQCompanion.App.Interop;

/// <summary>
/// Win32 extended-window-style plumbing for the overlays. A "locked" overlay is layered, transparent to mouse input
/// (clicks fall through to EverQuest), hidden from Alt+Tab and never takes focus; an "unlocked" overlay is a normal
/// interactive window so it can be dragged and resized.
/// </summary>
public static class Win32OverlayHelper
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// Applies (or removes) click-through on <paramref name="window"/>. Always sets WS_EX_LAYERED and WS_EX_TOOLWINDOW.
    /// When click-through is on it also sets WS_EX_TRANSPARENT and WS_EX_NOACTIVATE so the overlay can never steal focus
    /// from the game; turning it off clears both so the window can be moved and resized.
    /// Creates the native window handle if the window has not been shown yet.
    /// </summary>
    public static void SetClickThrough(Window window, bool isClickThrough)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        long style = GetExStyle(hwnd);

        style |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        if (isClickThrough)
        {
            style |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        }
        else
        {
            style &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        }

        SetExStyle(hwnd, style);
    }

    /// <summary>True when the window currently has WS_EX_TRANSPARENT set.</summary>
    public static bool IsClickThrough(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && (GetExStyle(hwnd) & WS_EX_TRANSPARENT) != 0;
    }

    private static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(hwnd, GWL_EXSTYLE);

    private static void SetExStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(style));
        }
        else
        {
            SetWindowLong32(hwnd, GWL_EXSTYLE, (int)style);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // 32-bit processes do not export the *Ptr variants.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
}
