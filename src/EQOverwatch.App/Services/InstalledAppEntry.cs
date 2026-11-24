using System.IO;
using EQOverwatch.Core;
using Microsoft.Win32;

namespace EQOverwatch.App.Services;

/// <summary>
/// Keeps the version shown in Windows' Installed apps right when the app was set up with the installer: Install update replaces the exe
/// in place, so after it the entry the installer wrote (HKCU, id <see cref="AppInfo.SettingsFolderName"/>) gets the running version.
/// Copies run from an unzipped folder have no entry and are left alone.
/// </summary>
public static class InstalledAppEntry
{
    /// <summary>The installer's uninstall key (Inno Setup adds "_is1" to the AppId of installer\EQOverwatch.iss).</summary>
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppInfo.SettingsFolderName + "_is1";

    public static void SyncVersion(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0)
            {
                return;
            }

            string installed = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar);
            string running = Path.GetFullPath(Path.GetDirectoryName(exePath) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(installed, running, StringComparison.OrdinalIgnoreCase))
            {
                return; // another copy (for example an unzipped one) is running
            }

            string version = AppInfo.Version.TrimStart('v'); // the installer writes "1.0.103"
            if (!string.Equals(key.GetValue("DisplayVersion") as string, version, StringComparison.Ordinal))
            {
                key.SetValue("DisplayVersion", version);
                key.SetValue("DisplayName", AppInfo.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            // Not being able to fix the shown version never matters to the app itself.
        }
    }
}
