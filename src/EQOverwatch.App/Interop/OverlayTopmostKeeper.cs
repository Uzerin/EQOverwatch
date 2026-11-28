using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EQOverwatch.App.Interop;

/// <summary>
/// Keeps the overlays above the game. Windows can push a topmost window behind another one (switching between
/// EverQuest clients does this), so every time a window comes to the front, and every 2 seconds as a backup,
/// each open overlay is put back on top without taking focus.
/// </summary>
public static class OverlayTopmostKeeper
{
    private static readonly List<Window> Windows = new();
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private static WinEventDelegate? _callback; // kept alive while the hook is set
    private static IntPtr _hook;
    private static DispatcherTimer? _timer;

    /// <summary>Starts keeping <paramref name="window"/> on top until it closes.</summary>
    public static void Add(Window window)
    {
        if (Windows.Contains(window))
        {
            return;
        }

        Windows.Add(window);
        window.Closed += (_, _) => Windows.Remove(window);
        Start();
    }

    private static void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        Dispatcher ui = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => BringAllToTop(), ui);
        _timer.Start();
        _callback = (_, _, _, _, _, _, _) => ui.BeginInvoke(BringAllToTop, DispatcherPriority.Background);
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Puts every visible overlay back at the top of the always-on-top windows.</summary>
    public static void BringAllToTop()
    {
        foreach (Window window in Windows.ToArray())
        {
            if (!window.IsVisible)
            {
                continue;
            }

            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
        }
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
