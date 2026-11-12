using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.Interop;

/// <summary>
/// Puts a window back where it was last time and remembers where it is now. Uses Windows' own window placement, which keeps the
/// normal size of a maximized window and moves a window that would be off every screen (an unplugged monitor) back onto one.
/// The placement is stored a second after the window stops moving or resizing, and again when it closes.
/// </summary>
public static class WindowPlacementKeeper
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private const int SwShowNormal = 1;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;
    private const int WpfRestoreToMaximized = 2;

    /// <summary>
    /// Call from the window's constructor, before it is shown. <paramref name="load"/> gives the saved placement (null for none) and
    /// <paramref name="store"/> keeps a new one.
    /// </summary>
    public static void Attach(Window window, Func<WindowPlacementSetting?> load, Action<WindowPlacementSetting> store)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(store);

        WindowPlacementSetting? saved = load();
        if (saved is { IsUsable: true })
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual; // otherwise WPF centers it again after it is placed
        }

        bool applied = false;
        window.SourceInitialized += (_, _) =>
        {
            if (saved is { IsUsable: true } placement)
            {
                Apply(window, placement);
            }

            applied = true;
        };

        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = SaveDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Store();
        };

        void Later()
        {
            if (applied)
            {
                timer.Stop();
                timer.Start();
            }
        }

        void Store()
        {
            if (Read(window) is { IsUsable: true } now)
            {
                store(now);
            }
        }

        window.LocationChanged += (_, _) => Later();
        window.SizeChanged += (_, _) => Later();
        window.StateChanged += (_, _) => Later();
        window.Closing += (_, _) =>
        {
            timer.Stop();
            Store();
        };
    }

    private static void Apply(Window window, WindowPlacementSetting placement)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var native = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>(),
            Flags = 0,
            ShowCmd = placement.IsMaximized ? SwShowMaximized : SwShowNormal,
            MinPosition = new NativePoint(-1, -1),
            MaxPosition = new NativePoint(-1, -1),
            NormalPosition = new NativeRect(placement.Left, placement.Top, placement.Right, placement.Bottom),
        };

        SetWindowPlacement(handle, ref native);
        if (placement.IsMaximized)
        {
            window.WindowState = WindowState.Maximized; // keeps WPF's own state in step with the window
        }
    }

    /// <summary>The window's placement now, or null when it has no handle yet.</summary>
    private static WindowPlacementSetting? Read(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var native = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(handle, ref native))
        {
            return null;
        }

        // A minimized window comes back at its normal size, or maximized when it was minimized from maximized.
        bool maximized = native.ShowCmd == SwShowMaximized
            || (native.ShowCmd == SwShowMinimized && (native.Flags & WpfRestoreToMaximized) != 0);
        return new WindowPlacementSetting
        {
            Left = native.NormalPosition.Left,
            Top = native.NormalPosition.Top,
            Right = native.NormalPosition.Right,
            Bottom = native.NormalPosition.Bottom,
            IsMaximized = maximized,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, [In] ref WindowPlacement placement);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);
}
