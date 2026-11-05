using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace EQOverwatch.App.Interop;

/// <summary>Reads a hotkey written as text, such as "Ctrl+Alt+F9", into the modifier flags and virtual-key code Windows wants.</summary>
public static class HotkeyParser
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;

    /// <summary>
    /// True for one or more of Ctrl, Alt and Shift followed by one key (a letter, a digit, F1 to F24 or a named key). A key without a
    /// modifier is refused, so the hotkey can never take over a plain key press everywhere on the PC.
    /// </summary>
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string? keyToken = null;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                default:
                    if (keyToken is not null)
                    {
                        return false; // two keys
                    }

                    keyToken = raw;
                    break;
            }
        }

        if (keyToken is null || modifiers == 0)
        {
            return false;
        }

        if (keyToken.Length == 1 && char.IsDigit(keyToken[0]))
        {
            keyToken = "D" + keyToken; // "5" is the Key named D5
        }
        else if (keyToken.All(char.IsDigit))
        {
            return false; // a bare number would be read as a key code
        }

        if (!Enum.TryParse(keyToken, ignoreCase: true, out Key key) || key == Key.None)
        {
            return false;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0)
        {
            return false;
        }

        virtualKey = (uint)vk;
        return true;
    }
}

/// <summary>A hotkey that works from any program (RegisterHotKey), delivered through the main window's message loop.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly int _id;
    private bool _registered;

    public GlobalHotkey(Window window, int id)
    {
        _id = id;
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("The window has no handle.");
        _source.AddHook(Hook);
    }

    /// <summary>Raised on the UI thread when the hotkey is pressed.</summary>
    public event Action? Pressed;

    /// <summary>Replaces the registered hotkey. False when Windows refuses it, usually because another program holds it.</summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        _registered = RegisterHotKey(_handle, _id, modifiers | ModNoRepeat, virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_handle, _id);
            _registered = false;
        }
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
