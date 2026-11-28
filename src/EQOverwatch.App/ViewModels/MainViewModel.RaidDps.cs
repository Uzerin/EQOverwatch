using System.IO;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.ViewModels;

// Raid boss DPS by week on the character profile: each of your characters' DPS in every raid boss fight, kept on disk, and the older
// recorded raid boss fights read back from the logs once (only the minutes around each fight).
public partial class MainViewModel
{
    /// <summary>The read-back of older raid boss fights starts this long after the app opens (after the start-up catch-up).</summary>
    public static readonly TimeSpan RaidDpsReadBackDelay = TimeSpan.FromSeconds(60);

    private readonly RaidDpsStore _raidDps = new();
    private readonly DateTime _raidDpsCreated = DateTime.UtcNow;
    private Task? _raidDpsReadBack;

    /// <summary>The raid boss DPS history (for tests).</summary>
    public RaidDpsStore RaidDps => _raidDps;

    private void InitializeRaidDps()
    {
        if (_dataDirectory is { } folder)
        {
            _raidDps.SetStoragePath(Path.Combine(folder, "raiddps.json"));
        }
    }

    /// <summary>Files your characters' DPS from finished raid boss fights; starts the one-time read-back when the app has settled.</summary>
    private void RecordRaidDps(IEnumerable<EncounterSnapshot> completed)
    {
        IReadOnlyDictionary<string, (string Server, bool Tank)> own = _ownCharacters;
        _raidDps.Record(completed, IsRaidBoss, own.ContainsKey);
    }

    private void TickRaidDps()
    {
        _raidDps.Flush();
        if (_raidDpsReadBack is null && _dataDirectory is not null && !IsImporting && AutoImportTask.IsCompleted
            && DateTime.UtcNow - _raidDpsCreated >= RaidDpsReadBackDelay && _ownCharacters.Count > 0)
        {
            var logs = Logs.Where(l => File.Exists(l.FilePath)).Select(l => (l.FilePath, l.ServerName)).ToList();
            _raidDpsReadBack = Task.Run(() => ReadBackRaidDpsAsync(logs));
        }
    }

    /// <summary>The character's raid boss DPS by week for the profile (null with fewer than 2 weeks of fights).</summary>
    public RaidDpsTrend? RaidDpsTrendFor(string character, string server) =>
        RaidDpsTrend.For(_raidDps.Entries(), character, _clock(), server);

    /// <summary>
    /// Reads back the recorded raid boss fights of the last 12 weeks that were not read for this yet: a minute either side of each fight,
    /// from the logs of its server, into a tracker of its own. Each fight is read once.
    /// </summary>
    internal async Task ReadBackRaidDpsAsync(IReadOnlyList<(string Path, string Server)> logs, CancellationToken cancellationToken = default)
    {
        try
        {
            DateTime from = RaidDpsTrend.WeekOf(_clock()).AddDays(-7 * (RaidDpsTrend.WeeksShown - 1));
            List<BossFight> fights = _bossHistory.Fights()
                .Where(f => f.Start >= from && IsRaidBoss(f.Boss) && !_raidDps.WasReadBack(f))
                .OrderBy(f => f.Start)
                .ToList();
            if (fights.Count == 0 || logs.Count == 0)
            {
                return;
            }

            var tracker = new LiveDpsTracker(roster: _pipeline.Roster);
            var importer = new HistoricalLogImporter(tracker);
            IReadOnlyDictionary<string, (string Server, bool Tank)> own = _ownCharacters;
            foreach (BossFight fight in fights)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] paths = logs
                    .Where(l => string.IsNullOrEmpty(fight.Server) || l.Server.Equals(fight.Server, StringComparison.OrdinalIgnoreCase))
                    .Select(l => l.Path)
                    .ToArray();
                if (paths.Length > 0)
                {
                    tracker.Reset();
                    DateTime start = fight.Start.AddMinutes(-1);
                    DateTime end = fight.Start.AddSeconds(Math.Max(0, fight.Seconds)).AddMinutes(1);
                    await importer.ImportAsync(paths, start, end, null, cancellationToken).ConfigureAwait(false);
                    _raidDps.Record(tracker.Snapshot.Completed, IsRaidBoss, own.ContainsKey);
                }

                _raidDps.MarkReadBack(fight);
            }

            _raidDps.Flush();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A log that can't be read now is tried again at the next start (fights not marked as read stay to do).
        }
    }
}
