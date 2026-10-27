using System.Text.Json;
using System.Text.Json.Serialization;
using EQCompanion.Core.Discovery;
using EQCompanion.Core.Models;
using EQCompanion.Core.Theming;

namespace EQCompanion.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Writes are atomic: the JSON goes to a temp file in the same folder,
/// is flushed to disk, and then replaces the real file, so a crash mid-write can never leave a truncated settings.json.
/// <see cref="QueueSave"/> debounces bursts (dragging a window raises hundreds of events) into a single write.
/// <para>
/// Settings live in <c>%LocalAppData%\EQOverwatch\settings.json</c>. When that file does not exist yet but the previous
/// version's <c>%LocalAppData%\EQCompanion\settings.json</c> does, <see cref="Load"/> migrates it: the old file is read,
/// normalized and written to the new location, and the old file is left untouched.
/// </para>
/// </summary>
public sealed class AppSettingsService : IDisposable
{
    public static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }, // enums are written by name; numbers are still accepted when reading
    };

    private readonly object _gate = new();
    private readonly object _ioGate = new();
    private readonly TimeSpan _saveDelay;
    private Timer? _timer;
    private AppSettings? _pending;
    private bool _disposed;

    /// <param name="filePath">The settings file; null means <see cref="DefaultPath"/>.</param>
    /// <param name="saveDelay">Debounce delay for <see cref="QueueSave"/>.</param>
    /// <param name="legacyFilePath">
    /// An older settings file to migrate when <paramref name="filePath"/> does not exist. Null means
    /// <see cref="LegacyDefaultPath"/> when the default location is used, and no migration for an explicit path.
    /// </param>
    public AppSettingsService(string? filePath = null, TimeSpan? saveDelay = null, string? legacyFilePath = null)
    {
        FilePath = filePath ?? DefaultPath;
        LegacyFilePath = legacyFilePath ?? (filePath is null ? LegacyDefaultPath : null);
        _saveDelay = saveDelay ?? DefaultSaveDelay;
    }

    /// <summary><c>%LocalAppData%\EQOverwatch\settings.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName, "settings.json");

    /// <summary><c>%LocalAppData%\EQCompanion\settings.json</c>, where EQ Companion kept its settings.</summary>
    public static string LegacyDefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.LegacySettingsFolderName, "settings.json");

    public string FilePath { get; }

    /// <summary>The older settings file that may be migrated, or null.</summary>
    public string? LegacyFilePath { get; }

    /// <summary>True when the last <see cref="Load"/> took its values from <see cref="LegacyFilePath"/>.</summary>
    public bool MigratedFromLegacy { get; private set; }

    /// <summary>Why the last <see cref="Load"/> fell back to defaults, or null.</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>Raised (on a pool thread for queued saves) when writing fails. The previous file is left intact.</summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>
    /// Reads the settings file. A missing file yields defaults. A corrupt file is renamed to <c>settings.json.corrupt</c>
    /// and defaults are returned so the app still starts. Values are normalized (nulls, NaN, out-of-range timeouts).
    /// </summary>
    public AppSettings Load()
    {
        LastLoadError = null;
        MigratedFromLegacy = false;
        if (!File.Exists(FilePath))
        {
            return TryMigrateLegacy() ?? new AppSettings();
        }

        try
        {
            using FileStream stream = File.OpenRead(FilePath);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
            return Normalize(loaded ?? new AppSettings());
        }
        catch (JsonException ex)
        {
            LastLoadError = $"settings.json was unreadable and has been reset: {ex.Message}";
            try
            {
                File.Move(FilePath, FilePath + ".corrupt", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // keep going with defaults even if the bad file can't be set aside
            }

            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadError = $"settings.json could not be read: {ex.Message}";
            return new AppSettings();
        }
    }

    /// <summary>
    /// Copies the previous version's settings to the new location. Returns null when there is nothing usable to migrate
    /// (no legacy file, or one that cannot be read), so the caller starts from defaults.
    /// </summary>
    private AppSettings? TryMigrateLegacy()
    {
        if (string.IsNullOrWhiteSpace(LegacyFilePath)
            || string.Equals(Path.GetFullPath(LegacyFilePath), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(LegacyFilePath))
        {
            return null;
        }

        AppSettings migrated;
        try
        {
            using FileStream stream = File.OpenRead(LegacyFilePath);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);
            migrated = Normalize(loaded ?? new AppSettings());
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // the old file is left alone; start fresh
        }

        MigratedFromLegacy = true;
        try
        {
            Save(migrated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLoadError = $"Your previous settings were loaded but could not be copied to {FilePath}: {ex.Message}";
        }

        return migrated;
    }

    /// <summary>Writes the settings now, atomically. Throws on failure.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        string directory = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        string temp = FilePath + ".tmp";

        lock (_ioGate)
        {
            Directory.CreateDirectory(directory);

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(json, 0, json.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
            {
                File.Replace(temp, FilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, FilePath);
            }
        }
    }

    /// <summary>Schedules a save after the debounce delay. A newer call replaces the pending snapshot.</summary>
    public void QueueSave(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = settings;
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(_saveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes any pending snapshot immediately. Failures are reported through <see cref="SaveFailed"/>.</summary>
    public void Flush()
    {
        AppSettings? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (pending is null)
        {
            return;
        }

        try
        {
            Save(pending);
        }
        catch (Exception ex)
        {
            SaveFailed?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        Flush();

        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Drops nameless and duplicate (by name) entries, clamps times, fixes invalid enums and clips names.</summary>
    internal static List<TrackedAbilitySetting> NormalizeTrackedAbilities(IEnumerable<TrackedAbilitySetting>? abilities)
    {
        var result = new List<TrackedAbilitySetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        foreach (TrackedAbilitySetting? ability in abilities ?? Enumerable.Empty<TrackedAbilitySetting>())
        {
            string name = ability?.Name?.Trim() ?? string.Empty;
            if (ability is null || name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            result.Add(new TrackedAbilitySetting
            {
                Id = ability.Id == Guid.Empty || !ids.Add(ability.Id) ? Guid.NewGuid() : ability.Id,
                Name = name.Length > TrackedAbilitySetting.MaxNameLength ? name[..TrackedAbilitySetting.MaxNameLength] : name,
                SpellId = Math.Max(0, ability.SpellId),
                ActiveSeconds = Math.Clamp(ability.ActiveSeconds, 0, TrackedAbilitySetting.MaxSeconds),
                CooldownSeconds = Math.Clamp(ability.CooldownSeconds, 0, TrackedAbilitySetting.MaxSeconds),
                Scope = Enum.IsDefined(ability.Scope) ? ability.Scope : AbilityScope.Everyone,
                ActiveOverlay = Enum.IsDefined(ability.ActiveOverlay) ? ability.ActiveOverlay : TimerOverlayCategory.Buffs,
                CooldownOverlay = Enum.IsDefined(ability.CooldownOverlay) ? ability.CooldownOverlay : TimerOverlayCategory.RaidTriggers,
                IsEnabled = ability.IsEnabled,
            });
        }

        return result;
    }

    /// <summary>Drops empty and duplicate entries, fixes invalid overlays and clips names, keeping the first entry per spell id.</summary>
    internal static List<TrackedSpellSetting> NormalizeTrackedSpells(IEnumerable<TrackedSpellSetting>? spells)
    {
        var result = new List<TrackedSpellSetting>();
        var seen = new HashSet<int>();
        foreach (TrackedSpellSetting? spell in spells ?? Enumerable.Empty<TrackedSpellSetting>())
        {
            if (spell is null || spell.SpellId <= 0 || !seen.Add(spell.SpellId))
            {
                continue;
            }

            string name = spell.SpellName ?? string.Empty;
            result.Add(new TrackedSpellSetting
            {
                SpellId = spell.SpellId,
                SpellName = name.Length > TrackedSpellSetting.MaxNameLength ? name[..TrackedSpellSetting.MaxNameLength] : name,
                Overlay = Enum.IsDefined(spell.Overlay) ? spell.Overlay : TimerOverlayCategory.Buffs,
                IsEnabled = spell.IsEnabled,
            });
        }

        return result;
    }

    /// <summary>Trims names, drops blanks and duplicates (case-insensitive), keeps the first spelling and the original order.</summary>
    internal static List<string> NormalizeCharacterNames(IEnumerable<string>? names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? name in names ?? Array.Empty<string>())
        {
            string trimmed = name?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.WatchedLogs ??= new List<WatchedLogSettings>();
        settings.WatchedLogs.RemoveAll(l => l is null || string.IsNullOrWhiteSpace(l.FilePath));
        settings.Triggers ??= new List<TriggerRule>();
        settings.Triggers.RemoveAll(t => t is null);
        settings.BuffsOverlay ??= OverlaySettings.ForCategory(TimerOverlayCategory.Buffs);
        settings.DebuffsOverlay ??= OverlaySettings.ForCategory(TimerOverlayCategory.Debuffs);
        settings.SongsOverlay ??= OverlaySettings.ForCategory(TimerOverlayCategory.Songs);
        settings.RaidTriggersOverlay ??= OverlaySettings.ForCategory(TimerOverlayCategory.RaidTriggers);
        settings.ChChainOverlay ??= OverlaySettings.ForCategory(TimerOverlayCategory.ChChain);
        settings.DpsOverlay ??= OverlaySettings.DefaultDps;

        foreach (WatchedLogSettings log in settings.WatchedLogs)
        {
            log.ChChainPosition = Math.Clamp(log.ChChainPosition, 0, WatchedLogConfig.MaxChChainPosition);
        }

        settings.EqFolderPath = EqDirectoryScanner.Normalize(settings.EqFolderPath);
        settings.AdditionalMonitoredCharacters = NormalizeCharacterNames(settings.AdditionalMonitoredCharacters);
        settings.ChChain = (settings.ChChain ?? new ChChainSettings()).Sanitized();
        settings.HistoricalDaysLimit = AppSettings.HistoricalDaysChoices.Contains(settings.HistoricalDaysLimit)
            ? settings.HistoricalDaysLimit
            : AppSettings.DefaultHistoricalDaysLimit;

        settings.RaidBossTimers ??= new List<RaidBossTimerRule>();
        settings.RaidBossTimers.RemoveAll(r => r is null);
        foreach (RaidBossTimerRule rule in settings.RaidBossTimers)
        {
            rule.MobName ??= string.Empty;
            rule.SpellName ??= string.Empty;
            rule.MatchText ??= string.Empty;
            rule.StartTtsText ??= string.Empty;
            rule.WarningTtsText ??= string.Empty;
            rule.ApplyLegacyTiming();
            rule.RecastSeconds = Math.Clamp(rule.RecastSeconds, 1, 86400);
        }

        settings.Theme = ThemePresets.Sanitize(settings.Theme ?? new ThemeConfig());

        settings.SpellsFilePath ??= string.Empty;
        settings.SkippedUpdateVersion ??= string.Empty;
        settings.TrackedSpellIds ??= new HashSet<int>();
        settings.TrackedSpells = NormalizeTrackedSpells(settings.TrackedSpells);
        settings.TrackedAbilities = NormalizeTrackedAbilities(settings.TrackedAbilities);
        settings.DefaultCasterLevel = Math.Clamp(settings.DefaultCasterLevel, AppSettings.MinCasterLevel, AppSettings.MaxCasterLevel);

        double timeout = settings.EncounterTimeoutSeconds;
        settings.EncounterTimeoutSeconds = double.IsFinite(timeout)
            ? Math.Clamp(timeout, AppSettings.MinEncounterTimeoutSeconds, AppSettings.MaxEncounterTimeoutSeconds)
            : AppSettings.DefaultEncounterTimeoutSeconds;

        return settings;
    }
}
