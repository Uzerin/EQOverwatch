using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.Interop;

/// <summary>
/// The Windows side of the auto-click: it presses the left mouse button, puts the pointer on the saved spot and holds it there for the
/// click (then puts it back), and says whether the EverQuest client (<c>eqgame.exe</c>) is the window in front. It reads no game memory
/// and sends nothing to the client; it is an ordinary mouse click.
/// </summary>
public sealed class Win32InputClicker : IClickSender
{
    private const string GameProcessName = "eqgame";
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const int PressMilliseconds = 20;

    public bool IsGameFocused() => IsGameWindow(GetForegroundWindow());

    private readonly object _holdGate = new();
    private POINT? _returnTo;
    private POINT _heldAt;

    public bool HoldAt(ClickSpot spot)
    {
        using var physical = new PhysicalPixels();
        IntPtr window = GetForegroundWindow();
        if (!IsGameWindow(window))
        {
            return false;
        }

        var point = new POINT { X = spot.X, Y = spot.Y };
        if (!ClientToScreen(window, ref point) || !GetCursorPos(out POINT before))
        {
            return false;
        }

        lock (_holdGate)
        {
            _returnTo ??= before;
            _heldAt = point;
        }

        SetCursorPos(point.X, point.Y);
        var clip = new RECT { Left = point.X, Top = point.Y, Right = point.X + 1, Bottom = point.Y + 1 };
        ClipCursor(ref clip);
        return true;
    }

    public void KeepHeld()
    {
        using var physical = new PhysicalPixels();
        POINT at;
        lock (_holdGate)
        {
            if (_returnTo is null)
            {
                return;
            }

            at = _heldAt;
        }

        // Some programs (the game itself among them) can change the cursor clip: put it back.
        SetCursorPos(at.X, at.Y);
        var clip = new RECT { Left = at.X, Top = at.Y, Right = at.X + 1, Bottom = at.Y + 1 };
        ClipCursor(ref clip);
    }

    public void Release()
    {
        using var physical = new PhysicalPixels();
        POINT? back;
        lock (_holdGate)
        {
            back = _returnTo;
            _returnTo = null;
        }

        if (back is null)
        {
            return;
        }

        ClipCursor(IntPtr.Zero);
        SetCursorPos(back.Value.X, back.Value.Y);
    }

    /// <summary>True when the window belongs to the EverQuest client and is not minimized.</summary>
    internal static bool IsGameWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || IsIconic(window))
        {
            return false;
        }

        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals(GameProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>For its lifetime, this thread works in physical pixels, the same ones the game window and the mouse hook use.</summary>
    internal readonly struct PhysicalPixels : IDisposable
    {
        private static readonly IntPtr PerMonitorAwareV2 = new(-4);
        private readonly IntPtr _previous;

        public PhysicalPixels()
        {
            _previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        }

        public void Dispose()
        {
            if (_previous != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(_previous);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr rect);

    public void ClickAtCursor()
    {
        int size = Marshal.SizeOf<INPUT>();
        SendInput(1, new[] { MouseInput(MOUSEEVENTF_LEFTDOWN) }, size);
        Thread.Sleep(PressMilliseconds);
        SendInput(1, new[] { MouseInput(MOUSEEVENTF_LEFTUP) }, size);
    }

    /// <summary>
    /// Presses the key with its modifiers, as scan codes (what the game reads from the keyboard), then lets them go in reverse order.
    /// </summary>
    public void PressKey(KeyChord key)
    {
        var downs = new List<ushort>();
        if (key.Ctrl)
        {
            downs.Add(0x11);
        }

        if (key.Alt)
        {
            downs.Add(0x12);
        }

        if (key.Shift)
        {
            downs.Add(0x10);
        }

        downs.Add((ushort)key.VirtualKey);
        int size = Marshal.SizeOf<INPUT>();
        foreach (ushort vk in downs)
        {
            SendInput(1, new[] { KeyInput(vk, up: false) }, size);
        }

        Thread.Sleep(PressMilliseconds);
        for (int i = downs.Count - 1; i >= 0; i--)
        {
            SendInput(1, new[] { KeyInput(downs[i], up: true) }, size);
        }
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0;

    private static INPUT KeyInput(ushort virtualKey, bool up)
    {
        ushort scan = (ushort)MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC);
        bool extended = virtualKey is >= 0x21 and <= 0x2E; // page up/down, end, home, arrows, insert, delete
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wScan = scan,
                    dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0),
                },
            },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private static INPUT MouseInput(uint flags) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
