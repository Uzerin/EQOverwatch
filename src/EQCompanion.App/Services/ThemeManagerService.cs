using System.Windows;
using System.Windows.Media;
using EQCompanion.Core.Theming;

namespace EQCompanion.App.Services;

/// <summary>
/// Applies a <see cref="ThemePalette"/> at runtime by replacing the brush entries in <c>Application.Current.Resources</c>.
/// Every themed element references those brushes through <c>{DynamicResource ...}</c>, so the main window and all six overlay
/// windows change immediately, without a restart.
/// </summary>
public sealed class ThemeManagerService : IThemeService
{
    /// <summary>The three built-in presets (defined in Core so they can be unit tested without WPF).</summary>
    public static IReadOnlyList<string> PresetNames => ThemePresets.Names;

    /// <summary>The palette most recently applied.</summary>
    public ThemePalette Current { get; private set; } = ThemePresets.ObsidianSlate;

    /// <summary>Call on the UI thread.</summary>
    public void Apply(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        Current = ThemePresets.Sanitize(palette);

        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in ThemeDerived.Build(Current))
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Value));
            brush.Freeze();
            resources[pair.Key] = brush;
        }
    }
}
