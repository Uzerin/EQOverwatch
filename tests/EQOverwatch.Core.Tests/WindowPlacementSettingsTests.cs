using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>Saved window positions survive a save and load; unusable ones are dropped.</summary>
public sealed class WindowPlacementSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Window_placements_are_saved_and_loaded_and_bad_ones_are_dropped()
    {
        string path = Path.Combine(_dir, "settings.json");
        var main = new WindowPlacementSetting { Left = 1940, Top = 60, Right = 3240, Bottom = 1000, IsMaximized = true };
        var colors = new WindowPlacementSetting { Left = -900, Top = 120, Right = -300, Bottom = 700 }; // a monitor left of the main one
        var settings = new AppSettings
        {
            WindowPlacements =
            {
                [WindowPlacementSetting.Main] = main,
                [WindowPlacementSetting.Colors] = colors,
                [WindowPlacementSetting.CharacterData] = new WindowPlacementSetting { Left = 10, Top = 10, Right = 40, Bottom = 40 }, // too small
                ["Broken"] = new WindowPlacementSetting { Left = 500, Top = 500, Right = 100, Bottom = 100 },                      // inverted
            },
        };

        var service = new AppSettingsService(path);
        service.Save(settings);
        AppSettings loaded = new AppSettingsService(path).Load();

        Assert.Equal(2, loaded.WindowPlacements.Count);
        Assert.Equal(main, loaded.WindowPlacements["main"]); // names compare without regard to capitals
        Assert.Equal(colors, loaded.WindowPlacements[WindowPlacementSetting.Colors]);
        Assert.Equal((1300, 940), (main.Width, main.Height));
    }

    [Fact]
    public void Settings_from_before_window_placements_load_with_none()
    {
        string path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{\"EncounterTimeoutSeconds\": 8}");

        Assert.Empty(new AppSettingsService(path).Load().WindowPlacements);
    }
}
