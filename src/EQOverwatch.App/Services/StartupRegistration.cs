using System.IO;
using Microsoft.Win32;

namespace EQOverwatch.App.Services;

/// <summary>Starts EQ Overwatch (minimized) when you sign in to Windows: a value under your own Run key, nothing system-wide.</summary>
public static class StartupRegistration
{
    public const string MinimizedArgument = "--minimized";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EQ Overwatch";

    public static void Set(bool on, string? exePath)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return;
            }

            if (on && !string.IsNullOrEmpty(exePath))
            {
                key.SetValue(ValueName, $"\"{exePath}\" {MinimizedArgument}");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // left as it was
        }
    }
}
