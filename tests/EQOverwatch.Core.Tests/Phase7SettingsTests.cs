using System.IO;
using EQOverwatch.Core;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class Phase7SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqo-" + Guid.NewGuid().ToString("N"));

    private string NewPath => Path.Combine(_dir, "EQOverwatch", "settings.json");
    private string LegacyPath => Path.Combine(_dir, "EQCompanion", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void The_default_settings_folder_is_EQOverwatch_and_the_legacy_one_is_EQOverwatch()
    {
        Assert.Contains(AppInfo.SettingsFolderName, AppSettingsService.DefaultPath);
        Assert.Contains(AppInfo.LegacySettingsFolderName, AppSettingsService.LegacyDefaultPath);
        Assert.Equal("EQOverwatch", AppInfo.SettingsFolderName);
    }

    [Fact]
    public void Old_settings_are_migrated_to_the_new_location_and_the_old_file_is_left_alone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        var legacy = new AppSettings { EncounterTimeoutSeconds = 21, DefaultCasterLevel = 70 };
        new AppSettingsService(LegacyPath).Save(legacy);
        string legacyText = File.ReadAllText(LegacyPath);

        var service = new AppSettingsService(NewPath, null, LegacyPath);
        AppSettings loaded = service.Load();

        Assert.True(service.MigratedFromLegacy);
        Assert.Equal(21, loaded.EncounterTimeoutSeconds);
        Assert.Equal(70, loaded.DefaultCasterLevel);
        Assert.True(File.Exists(NewPath));
        Assert.Equal(legacyText, File.ReadAllText(LegacyPath));
    }

    [Fact]
    public void Existing_new_settings_win_over_legacy_ones()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        new AppSettingsService(LegacyPath).Save(new AppSettings { EncounterTimeoutSeconds = 21 });
        new AppSettingsService(NewPath).Save(new AppSettings { EncounterTimeoutSeconds = 9 });

        var service = new AppSettingsService(NewPath, null, LegacyPath);

        Assert.Equal(9, service.Load().EncounterTimeoutSeconds);
        Assert.False(service.MigratedFromLegacy);
    }

    [Fact]
    public void Phase_7_fields_round_trip_and_are_normalized()
    {
        var settings = new AppSettings
        {
            EqFolderPath = @"C:\EQ",
            AdditionalMonitoredCharacters = { "Oodles", "oodles", "  Ream ", "", "Bad Name1" },
            HistoricalDaysLimit = 30,
            IsCooldownTrackerEnabled = false,
            ChChain = new ChChainSettings { PositionTagFormat = "no token", CueKeyword = "", TotalClerics = 99, LateThresholdSeconds = -4 },
        };
        settings.WatchedLogs.Add(new WatchedLogSettings
        {
            FilePath = @"C:\EQ\Logs\eqlog_Ream_Teek.txt", CharacterName = "Ream", ChChainPosition = 3,
        });

        new AppSettingsService(NewPath).Save(settings);
        AppSettings loaded = new AppSettingsService(NewPath).Load();

        Assert.Equal(@"C:\EQ", loaded.EqFolderPath);
        Assert.Equal(30, loaded.HistoricalDaysLimit);
        Assert.False(loaded.IsCooldownTrackerEnabled);
        Assert.Equal(3, Assert.Single(loaded.WatchedLogs).ChChainPosition);

        // names: de-duplicated (case-insensitive), trimmed and free of blanks
        Assert.Equal(1, loaded.AdditionalMonitoredCharacters.Count(n => n.Equals("Oodles", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("Ream", loaded.AdditionalMonitoredCharacters);
        Assert.DoesNotContain("", loaded.AdditionalMonitoredCharacters);

        // chain settings: bad values fall back to valid ones
        Assert.Contains("{N}", loaded.ChChain.PositionTagFormat);
        Assert.False(string.IsNullOrWhiteSpace(loaded.ChChain.CueKeyword));
        Assert.InRange(loaded.ChChain.TotalClerics, 1, 12);
        Assert.InRange(loaded.ChChain.LateThresholdSeconds, 0.1, 9.0);
    }

    [Fact]
    public void The_defaults_match_the_spec()
    {
        var settings = new AppSettings();

        Assert.Equal(7, settings.HistoricalDaysLimit);
        Assert.Equal(60, settings.DefaultCasterLevel);
        Assert.Equal("{N}-{N}-{N}", settings.ChChain.PositionTagFormat);
        Assert.Equal("CAST NOW", settings.ChChain.CueKeyword);
        Assert.Equal(4, settings.ChChain.TotalClerics);
        Assert.Equal(1.0, settings.ChChain.LateThresholdSeconds);
        Assert.False(settings.ChChainOverlay.IsVisible);
        Assert.True(settings.IsCooldownTrackerEnabled);
    }

    [Fact]
    public void Historical_day_limits_translate_to_a_start_time()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0);

        Assert.Equal(now.AddDays(-7), AppSettings.GetHistoricalStart(7, now));
        Assert.Null(AppSettings.GetHistoricalStart(0, now));
        Assert.Equal("All History", AppSettings.DescribeHistoricalDays(0));
        Assert.Equal("1 Day", AppSettings.DescribeHistoricalDays(1));
        Assert.Equal("14 Days", AppSettings.DescribeHistoricalDays(14));
    }

    [Fact]
    public void The_app_version_is_1_0_0()
    {
        Assert.Equal("v1.0.18", AppInfo.Version);
        Assert.Equal("EQ Overwatch v1.0.18", AppInfo.Title);
        Assert.Equal("EQ Overwatch", AppInfo.Name);
    }

    [Fact]
    public void Chain_slots_are_exclusive_in_a_watched_log_collection()
    {
        var logs = new WatchedLogCollection();
        WatchedLogConfig a = logs.AddFile(Path.Combine(_dir, "eqlog_Ream_Teek.txt"));
        WatchedLogConfig b = logs.AddFile(Path.Combine(_dir, "eqlog_Oodles_Teek.txt"));

        a.ChChainPosition = 4;
        b.ChChainPosition = 4;

        Assert.Equal(0, a.ChChainPosition);
        Assert.Equal(4, b.ChChainPosition);

        b.ChChainPosition = 99; // clamped to the 12 slots
        Assert.Equal(WatchedLogConfig.MaxChChainPosition, b.ChChainPosition);
    }

    [Fact]
    public void The_chain_overlay_category_is_registered()
    {
        Assert.Contains(TimerOverlayCategory.ChChain, TimerOverlayCategoryExtensions.All);
        Assert.Equal("CH Chain", TimerOverlayCategory.ChChain.DisplayName());
    }
}
