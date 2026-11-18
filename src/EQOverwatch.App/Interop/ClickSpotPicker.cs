using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.Interop;

/// <summary>
/// Set Position for the auto-click: brings the EverQuest window that was last in front forward, waits for one left click inside it (the
/// click is kept from the game, so nothing is cast), then brings EQ Overwatch back. Esc, or no click within the time allowed, cancels.
/// </summary>
public sealed class ClickSpotPicker
{
    /// <summary>How long the picker waits for the click.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int VK_ESCAPE = 0x1B;
    private const int SW_RESTORE = 9;

    private readonly Window _owner;
    private HookProc? _mouseProc;
    private HookProc? _keyProc;
    private IntPtr _mouseHook;
    private IntPtr _keyHook;
    private IntPtr _game;
    private bool _swallowUp;
    private TaskCompletionSource<ClickSpot?>? _done;
    private DispatcherTimer? _timer;
    private Window? _prompt;

    public ClickSpotPicker(Window owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>
    /// The spot clicked, in pixels from the top-left of the EverQuest window's client area; null when cancelled, timed out, or no
    /// EverQuest window was found (<paramref name="problem"/> says which).
    /// </summary>
    public Task<ClickSpot?> PickAsync(Action<string>? problem = null)
    {
        _game = LastGameWindow();
        if (_game == IntPtr.Zero)
        {
            problem?.Invoke("No EverQuest window was found. Start EverQuest, then try Set Position again.");
            return Task.FromResult<ClickSpot?>(null);
        }

        _done = new TaskCompletionSource<ClickSpot?>();
        if (IsIconic(_game))
        {
            ShowWindow(_game, SW_RESTORE);
        }

        ForceForeground(_game);
        ShowPrompt();

        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        IntPtr module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            Finish(null);
            problem?.Invoke($"The click could not be watched for (Windows error {Marshal.GetLastWin32Error()}).");
            return _done.Task;
        }

        _timer = new DispatcherTimer { Interval = Timeout };
        _timer.Tick += (_, _) => Finish(null);
        _timer.Start();
        return _done.Task;
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = wParam.ToInt32();
            if (message == WM_LBUTTONDOWN && _done is { Task.IsCompleted: false })
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (ToClient(info.pt) is { } spot)
                {
                    _swallowUp = true;
                    _owner.Dispatcher.BeginInvoke(() => Finish(spot));
                    return new IntPtr(1); // kept from the game: nothing is cast
                }
            }
            else if (message == WM_LBUTTONUP && _swallowUp)
            {
                _swallowUp = false;
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN)
            && Marshal.ReadInt32(lParam) == VK_ESCAPE && _done is { Task.IsCompleted: false })
        {
            _owner.Dispatcher.BeginInvoke(() => Finish(null));
            return new IntPtr(1);
        }

        return CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    /// <summary>The screen point in the game window's client area, or null when it is outside it.</summary>
    private ClickSpot? ToClient(Win32InputClicker.POINT screen)
    {
        using var physical = new Win32InputClicker.PhysicalPixels();
        Win32InputClicker.POINT point = screen;
        if (!Win32InputClicker.ScreenToClient(_game, ref point) || !GetClientRect(_game, out Win32InputClicker.RECT client))
        {
            return null;
        }

        return point.X >= 0 && point.Y >= 0 && point.X < client.Right && point.Y < client.Bottom ? new ClickSpot(point.X, point.Y) : null;
    }

    private void Finish(ClickSpot? spot)
    {
        if (_done is null || _done.Task.IsCompleted)
        {
            return;
        }

        _timer?.Stop();
        if (_keyHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }

        // The mouse hook stays a moment longer so the button's release is kept from the game too.
        IntPtr mouseHook = _mouseHook;
        var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        later.Tick += (_, _) =>
        {
            later.Stop();
            if (mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(mouseHook);
            }

            if (_mouseHook == mouseHook)
            {
                _mouseHook = IntPtr.Zero;
            }
        };
        later.Start();

        _prompt?.Close();
        _prompt = null;
        IntPtr own = new WindowInteropHelper(_owner).Handle;
        if (own != IntPtr.Zero)
        {
            ForceForeground(own);
        }

        _owner.Activate();
        _done.TrySetResult(spot);
    }

    private void ShowPrompt()
    {
        var text = new TextBlock
        {
            Text = "Click your Complete Heal button in EverQuest.  Esc cancels.",
            Foreground = Brushes.White,
            FontSize = 16,
            Margin = new Thickness(16, 10, 16, 10),
        };
        _prompt = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(230, 180, 30, 40)),
            Topmost = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = text,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width / 2) - 240,
            Top = SystemParameters.WorkArea.Top + 40,
        };
        _prompt.SourceInitialized += (_, _) => Win32OverlayHelper.SetClickThrough(_prompt, true);
        _prompt.Show();
    }

    /// <summary>The EverQuest window nearest the front (windows are listed front to back), or zero.</summary>
    private static IntPtr LastGameWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out uint processId);
            try
            {
                using Process process = Process.GetProcessById((int)processId);
                if (process.ProcessName.Equals("eqgame", StringComparison.OrdinalIgnoreCase))
                {
                    found = window;
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // a window whose process has gone
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Windows only lets the program in front hand the front to another window; joining the input of the window in front for a moment
    /// makes this program count as it (no key is sent to the game).
    /// </summary>
    private static void ForceForeground(IntPtr window)
    {
        IntPtr front = GetForegroundWindow();
        uint frontThread = front == IntPtr.Zero ? 0 : GetWindowThreadProcessId(front, out _);
        uint ownThread = GetCurrentThreadId();
        bool attached = frontThread != 0 && frontThread != ownThread && AttachThreadInput(ownThread, frontThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ownThread, frontThread, false);
            }
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public Win32InputClicker.POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32InputClicker.RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);
}
