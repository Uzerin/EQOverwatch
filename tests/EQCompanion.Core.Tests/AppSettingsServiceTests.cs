using EQCompanion.Core.Models;
using EQCompanion.Core.Settings;
using Xunit;

namespace EQCompanion.Core.Tests;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "nested", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static AppSettings Sample() => new()
    {
        WatchedLogs =
        {
            new WatchedLogSettings { FilePath = @"C:\EQ\eqlog_Stackprime_Frostreaver.txt", CharacterName = "Stackprime", IsTriggerEnabled = true, IsPrimaryDps = true },
            new WatchedLogSettings { FilePath = @"C:\EQ\eqlog_Oodles_Frostreaver.txt", CharacterName = "Oodles", IsTriggerEnabled = false },
        },
        Triggers =
        {
            new TriggerRule
            {
                Name = "Complete Heal", Pattern = @"^(?<caster>\w+) begins to cast", UseRegex = true,
                TimerDurationSeconds = 10, TimerLabelTemplate = "${caster}: CH", AlertTextTemplate = "",
                AudioText = "heal incoming", AudioFilePath = @"C:\sounds\ding.wav",
            },
        },
        EncounterTimeoutSeconds = 15,
        IsAudioMuted = true,
        SpellsFilePath = @"C:\EQ\spells_us.txt",
        DefaultCasterLevel = 65,
        TrackedSpellIds = { 100, 101, 5000 },
        BuffsOverlay = new OverlaySettings { Left = 10, Top = 20, Width = 300, Height = 200, IsVisible = false, IsLocked = true },
        DpsOverlay = new OverlaySettings { Left = -1500, Top = 40, Width = 280, Height = 220, IsVisible = true, IsLocked = true },
    };

    [Fact]
    public void Default_path_is_under_local_app_data()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EQOverwatch", "settings.json");
        Assert.Equal(expected, AppSettingsService.DefaultPath);
        Assert.Equal(expected, new AppSettingsService().FilePath);
    }

    [Fact]
    public void Missing_file_loads_defaults()
    {
        var service = new AppSettingsService(SettingsPath);

        AppSettings settings = service.Load();

        Assert.Empty(settings.WatchedLogs);
        Assert.Empty(settings.Triggers);
        Assert.Equal(8, settings.EncounterTimeoutSeconds);
        Assert.False(settings.IsAudioMuted);
        Assert.Equal(string.Empty, settings.SpellsFilePath);
        Assert.Equal(60, settings.DefaultCasterLevel);
        Assert.Empty(settings.TrackedSpellIds);
        Assert.Equal(OverlaySettings.ForCategory(TimerOverlayCategory.Buffs), settings.BuffsOverlay);
        Assert.Null(service.LastLoadError);
    }

    [Fact]
    public void Everything_round_trips_through_json()
    {
        var service = new AppSettingsService(SettingsPath);
        AppSettings original = Sample();

        service.Save(original); // also creates the missing folder
        AppSettings loaded = new AppSettingsService(SettingsPath).Load();

        Assert.Equal(2, loaded.WatchedLogs.Count);
        Assert.Equal("Stackprime", loaded.WatchedLogs[0].CharacterName);
        Assert.True(loaded.WatchedLogs[0].IsPrimaryDps);
        Assert.False(loaded.WatchedLogs[1].IsTriggerEnabled);

        TriggerRule rule = Assert.Single(loaded.Triggers);
        Assert.Equal(original.Triggers[0].Id, rule.Id);
        Assert.Equal("${caster}: CH", rule.TimerLabelTemplate);
        Assert.Equal(10, rule.TimerDurationSeconds);
        Assert.Equal(@"C:\sounds\ding.wav", rule.AudioFilePath);

        Assert.Equal(15, loaded.EncounterTimeoutSeconds);
        Assert.True(loaded.IsAudioMuted);
        Assert.Equal(@"C:\EQ\spells_us.txt", loaded.SpellsFilePath);
        Assert.Equal(65, loaded.DefaultCasterLevel);
        Assert.Equal(new[] { 100, 101, 5000 }, loaded.TrackedSpellIds.OrderBy(i => i));
        Assert.Equal(original.BuffsOverlay, loaded.BuffsOverlay);
        Assert.Equal(original.DpsOverlay, loaded.DpsOverlay);
    }

    [Fact]
    public void Saving_is_atomic_and_leaves_no_temp_file_behind()
    {
        var service = new AppSettingsService(SettingsPath);
        service.Save(Sample());
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath + ".tmp", "{ half written garbage from a previous crash");

        var changed = Sample();
        changed.EncounterTimeoutSeconds = 30;
        service.Save(changed); // overwrites the stale temp file, then replaces the real file

        Assert.False(File.Exists(SettingsPath + ".tmp"));
        Assert.Equal(30, service.Load().EncounterTimeoutSeconds);
    }

    [Fact]
    public void A_failed_write_leaves_the_previous_file_intact_and_reports_the_error()
    {
        var service = new AppSettingsService(SettingsPath, TimeSpan.FromMilliseconds(20));
        service.Save(Sample());
        string before = File.ReadAllText(SettingsPath);

        // Make the temp path unusable: a directory squats on "settings.json.tmp".
        Directory.CreateDirectory(SettingsPath + ".tmp");
        Exception? failure = null;
        service.SaveFailed += ex => failure = ex;

        service.QueueSave(new AppSettings { EncounterTimeoutSeconds = 99 });
        service.Flush();

        Assert.NotNull(failure);
        Assert.Equal(before, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Corrupt_json_is_quarantined_and_defaults_are_returned()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ \"WatchedLogs\": [ this is not json");
        var service = new AppSettingsService(SettingsPath);

        AppSettings settings = service.Load();

        Assert.Empty(settings.WatchedLogs);
        Assert.NotNull(service.LastLoadError);
        Assert.False(File.Exists(SettingsPath));
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void Loaded_values_are_normalized()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """
            {
              "WatchedLogs": [ { "FilePath": "" }, { "FilePath": "C:\\EQ\\eqlog_A_B.txt" }, null ],
              "Triggers": null,
              "EncounterTimeoutSeconds": 9999,
              "DefaultCasterLevel": 900,
              "SpellsFilePath": null,
              "TrackedSpellIds": null,
              "BuffsOverlay": null
            }
            """);

        AppSettings settings = new AppSettingsService(SettingsPath).Load();

        Assert.Equal("C:\\EQ\\eqlog_A_B.txt", Assert.Single(settings.WatchedLogs).FilePath);
        Assert.Empty(settings.Triggers);
        Assert.Equal(AppSettings.MaxEncounterTimeoutSeconds, settings.EncounterTimeoutSeconds);
        Assert.Equal(AppSettings.MaxCasterLevel, settings.DefaultCasterLevel);
        Assert.Equal(string.Empty, settings.SpellsFilePath);
        Assert.Empty(settings.TrackedSpellIds);
        Assert.Equal(OverlaySettings.ForCategory(TimerOverlayCategory.Buffs), settings.BuffsOverlay);
        Assert.Equal(OverlaySettings.DefaultDps, settings.DpsOverlay);
    }

    [Fact]
    public void Queued_saves_are_debounced_to_the_latest_snapshot()
    {
        using var service = new AppSettingsService(SettingsPath, TimeSpan.FromMilliseconds(150));

        for (int i = 1; i <= 20; i++)
        {
            service.QueueSave(new AppSettings { EncounterTimeoutSeconds = i });
        }

        Assert.False(File.Exists(SettingsPath)); // nothing written yet: still inside the debounce window

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(SettingsPath) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(20, new AppSettingsService(SettingsPath).Load().EncounterTimeoutSeconds);
    }

    [Fact]
    public void Flush_and_Dispose_write_pending_changes_immediately()
    {
        var service = new AppSettingsService(SettingsPath, TimeSpan.FromMinutes(5));

        service.QueueSave(new AppSettings { EncounterTimeoutSeconds = 11 });
        service.Flush();
        Assert.Equal(11, new AppSettingsService(SettingsPath).Load().EncounterTimeoutSeconds);

        service.QueueSave(new AppSettings { EncounterTimeoutSeconds = 12 });
        service.Dispose();
        Assert.Equal(12, new AppSettingsService(SettingsPath).Load().EncounterTimeoutSeconds);

        service.QueueSave(new AppSettings { EncounterTimeoutSeconds = 13 }); // ignored after disposal
        service.Flush();
        Assert.Equal(12, new AppSettingsService(SettingsPath).Load().EncounterTimeoutSeconds);
    }
}
