using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EQOverwatch.App.Interop;

/// <summary>Whether the EverQuest client (<c>eqgame.exe</c>) is the window in front, so notifications only show while you are away from it.</summary>
public static class GameWindow
{
    private const string GameProcessName = "eqgame";

    /// <summary>True when the window in front belongs to the EverQuest client and is not minimized.</summary>
    public static bool IsInFront() => IsGameWindow(GetForegroundWindow());

    /// <summary>True when the window belongs to the EverQuest client and is not minimized.</summary>
    public static bool IsGameWindow(IntPtr window)
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
