using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.Interop;

/// <summary>
/// The Windows side of the auto-press: it presses keys the way a keyboard does (scan codes), and says whether the EverQuest client
/// (<c>eqgame.exe</c>) is the window in front. It reads no game memory and sends nothing to the client but ordinary key presses.
/// </summary>
public sealed class Win32InputClicker : IClickSender
{
    private const string GameProcessName = "eqgame";
    private const int PressMilliseconds = 20;

    public bool IsGameFocused() => IsGameWindow(GetForegroundWindow());

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
