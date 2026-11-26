using System.IO;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App.ViewModels;

// Every character's log feeds the DPS parse, the catch-up at start-up and the history load (a line several logs saw counts once). The
// character you played last (the newest log line) decides the map's zone and where the Server box starts.
public partial class MainViewModel
{
    private bool _allLogsLoadOffered;
    private WatchedLogConfig? _lastWrittenLog;
    private bool _lastWrittenKnown;

    /// <summary>
    /// The log of the character you played last: the one whose log had the newest line since the app opened, or before any line came in,
    /// the log file written most recently. Null with no logs.
    /// </summary>
    public WatchedLogConfig? LastPlayedLog()
    {
        if (Logs.Count == 0)
        {
            return null;
        }

        if (_pipeline.Activity.MostRecentCharacter() is { } name
            && Logs.FirstOrDefault(l => l.CharacterName.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } active)
        {
            return active;
        }

        if (!_lastWrittenKnown || (_lastWrittenLog is not null && !Logs.Contains(_lastWrittenLog)))
        {
            _lastWrittenKnown = true;
            _lastWrittenLog = Logs.OrderByDescending(l => LastWrite(l.FilePath)).First();
        }

        return _lastWrittenLog;

        static DateTime LastWrite(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return DateTime.MinValue;
            }
        }
    }

    /// <summary>The list of logs changed: the log written last is looked up again.</summary>
    private void ForgetLastPlayedLog() => _lastWrittenKnown = false;

    /// <summary>Every watched log that exists, in the order of the Characters tab.</summary>
    private IReadOnlyList<string> ExistingLogPaths() =>
        Logs.Select(l => l.FilePath).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Once (the first open of this version, or a new install once it has logs): offers to read every log's whole history into Mob History.
    /// It can take several minutes, so it can be declined; Reload All Logs in Settings does it later.
    /// </summary>
    private void OfferAllLogsLoad()
    {
        if (_allLogsLoadOffered || _restoring || _isShutDown)
        {
            return;
        }

        IReadOnlyList<string> paths = ExistingLogPaths();
        if (paths.Count == 0)
        {
            return; // asked once there are logs
        }

        _allLogsLoadOffered = true;
        RequestSave();

        string message =
            $"EQ Overwatch now combines all {paths.Count} of your character logs into one DPS parse, so a fight keeps being parsed when " +
            "one character dies or is far away. A hit seen in several logs counts once.\n\n" +
            "Reading the whole history of every log into Mob History is a one-time job and can take several minutes. " +
            "You can keep using the app while it runs.\n\n" +
            "Load all logs now? (You can do it later with Reload All Logs in Settings.)";
        if (!_dialogs.Confirm("Load fights from all your logs?", message))
        {
            ImportStatus = "Not loaded. Reload All Logs in Settings reads every log when you want.";
            return;
        }

        AutoImportTask = LoadAllLogsAsync(days: 0);
    }

    private DateTime? _backfillFrom;
    private string _backfillKey = string.Empty;
    private bool _encounterListRebuilt;

    /// <summary>A minute either side of every recorded fight against a raid boss, read before the recent logs at start-up.</summary>
    private IReadOnlyList<(DateTime Start, DateTime End)> RaidBossWindows() =>
        _bossHistory.Fights()
            .Where(f => IsRaidBoss(f.Boss))
            .Select(f => (f.Start.AddMinutes(-1), f.Start.AddSeconds(Math.Max(0, f.Seconds)).AddMinutes(1)))
            .ToList();

    /// <summary>Which logs and history setting a remembered start belongs to.</summary>
    private string BackfillKey(IReadOnlyList<string> paths) =>
        HistoricalDaysLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
        string.Join("|", paths.Select(p => p.ToUpperInvariant()).Order(StringComparer.Ordinal));

    /// <summary>
    /// After a read: when the meter is full, the next start only needs the logs from an hour before its oldest fight (older fights
    /// would be dropped again anyway).
    /// </summary>
    private void RememberBackfillStart(string key)
    {
        // Raid boss fights are kept apart (read from their own windows), so only the other fights decide where the next read starts.
        List<Core.Models.EncounterSnapshot> completed = _pipeline.LiveDps.Snapshot.Completed.Where(e => !IsRaidBoss(e.TargetName)).ToList();
        _backfillKey = key;
        _backfillFrom = completed.Count >= Core.Services.LiveDpsTracker.DefaultMaxCompletedEncounters
            ? completed.Min(e => e.StartTime).AddHours(-1)
            : null;
        RequestSave();
    }

    /// <summary>Catches the DPS meter up from every log at start-up (the Historical Days to Load), as if the app had been open all along.</summary>
    private async Task CatchUpLiveDpsAsync(IReadOnlyList<string> paths)
    {
        using var cts = new CancellationTokenSource();
        _importCts = cts;
        IsImporting = true;
        ImportProgress = 0;

        DateTime now = _clock();
        string window = AppSettings.DescribeHistoricalDays(HistoricalDaysLimit);
        ImportStatus = $"Loading {window} of {LogsText(paths.Count)} into the DPS meter…";

        var progress = new Progress<double>(fraction => ImportProgress = fraction * 100.0);
        try
        {
            // The meter keeps only its newest fights: start where the oldest one kept last time began, unless the logs changed.
            DateTime? start = AppSettings.GetHistoricalStart(HistoricalDaysLimit, now);
            string key = BackfillKey(paths);
            bool rebuild = !_encounterListRebuilt;
            if (rebuild)
            {
                ImportStatus = $"Rebuilding the encounter list from {LogsText(paths.Count)} (once, so every raid boss fight is kept)…";
            }
            else if (_backfillFrom is { } cached && _backfillKey == key && (start is null || cached > start))
            {
                start = cached;
            }

            // Older raid boss fights: only the minutes around each recorded fight are read again.
            DateTime? historyStart = AppSettings.GetHistoricalStart(HistoricalDaysLimit, now);
            IReadOnlyList<(DateTime Start, DateTime End)> windows = RaidBossWindows()
                .Where(w => historyStart is null || w.End >= historyStart) // still within Historical Days to Load
                .ToList();
            HistoricalImportResult result = await _pipeline.BackfillLiveAsync(paths, start, now, progress, cts.Token, windows);
            if (rebuild)
            {
                _encounterListRebuilt = true;
            }

            RememberBackfillStart(key);
            ImportProgress = 100;
            ImportStatus = $"Loaded {result.LinesRead:N0} lines from {LogsText(result.Logs)} in {result.Elapsed.TotalSeconds:0.0}s: " +
                           $"{_pipeline.LiveDps.Snapshot.All.Count():N0} encounters.";
        }
        catch (OperationCanceledException)
        {
            ImportStatus = "Loading the logs was cancelled.";
        }
        catch (Exception ex)
        {
            ImportStatus = $"Could not load the logs: {ex.Message}";
        }
        finally
        {
            _importCts = null;
            IsImporting = false;
        }
    }

    /// <summary>Reads every log's history (<paramref name="days"/>; 0 for all of it) into the history list and Mob History.</summary>
    private async Task LoadAllLogsAsync(int days)
    {
        int sequence = Interlocked.Increment(ref _autoImportSequence);
        IReadOnlyList<string> paths = ExistingLogPaths();
        if (paths.Count == 0)
        {
            ImportStatus = Logs.Count == 0 ? "Add your characters' logs on the Characters tab first." : "None of the watched log files were found.";
            return;
        }

        // A newer request replaces a running import: cancel it and wait (briefly) until the pipeline is free again.
        if (IsImporting)
        {
            CancelImport();
            for (int i = 0; i < 500 && IsImporting; i++)
            {
                await Task.Delay(10);
            }
        }

        if (sequence != Volatile.Read(ref _autoImportSequence))
        {
            return; // an even newer request is on its way
        }

        DateTime? start = AppSettings.GetHistoricalStart(days, _clock());
        string window = AppSettings.DescribeHistoricalDays(days);
        await RunImportAsync(
            paths, start, null,
            startStatus: $"Loading {window} of history from {LogsText(paths.Count)}…",
            successSuffix: string.Empty);
    }

    private static string LogsText(int count) => count == 1 ? "1 log" : $"{count} logs";
}
