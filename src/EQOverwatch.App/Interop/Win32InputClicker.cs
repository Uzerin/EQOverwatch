using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.Interop;

/// <summary>
/// The Windows side of the auto-click: it presses the left mouse button where the pointer is, and says whether the EverQuest client
/// (<c>eqgame.exe</c>) is the window in front. It reads no game memory and sends nothing to the client; it is an ordinary mouse click.
/// </summary>
public sealed class Win32InputClicker : IClickSender
{
    private const string GameProcessName = "eqgame";
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const int PressMilliseconds = 20;

    public bool IsGameFocused()
    {
        IntPtr window = GetForegroundWindow();
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

    public void ClickAtCursor()
    {
        int size = Marshal.SizeOf<INPUT>();
        SendInput(1, new[] { MouseInput(MOUSEEVENTF_LEFTDOWN) }, size);
        Thread.Sleep(PressMilliseconds);
        SendInput(1, new[] { MouseInput(MOUSEEVENTF_LEFTUP) }, size);
    }

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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
