using System.Reflection;

namespace EQOverwatch.Core;

/// <summary>Product name and version shown in the title bar, the toolbar badge and the Discord export footer.</summary>
public static class AppInfo
{
    public const string Name = "EQ Overwatch";

    /// <summary>The public GitHub repository releases are published to ("owner/name").</summary>
    public const string Repository = "Uzerin/EQOverwatch";

    public const string RepositoryUrl = "https://github.com/" + Repository;

    /// <summary>Where the published versions can be downloaded.</summary>
    public const string ReleasesUrl = RepositoryUrl + "/releases/latest";

    /// <summary>Folder under <c>%LocalAppData%</c> that holds <c>settings.json</c>.</summary>
    public const string SettingsFolderName = "EQOverwatch";

    /// <summary>The folder earlier versions (EQ Companion) used; its settings are migrated on first start.</summary>
    public const string LegacySettingsFolderName = "EQCompanion";

    /// <summary>"v1.0.24": the assembly's major.minor.build, read at run time so it can never disagree with the binary.</summary>
    public static string Version { get; } = FormatVersion(typeof(AppInfo).Assembly);

    /// <summary>"EQ Overwatch v1.0.24".</summary>
    public static string Title => $"{Name} {Version}";

    /// <summary>"v{major}.{minor}.{build}" for <paramref name="assembly"/>.</summary>
    public static string FormatVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        System.Version? version = assembly.GetName().Version;
        return version is null ? "v0.0.0" : $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
