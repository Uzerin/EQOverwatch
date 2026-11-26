using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Starts the buff and debuff bars again from the logs: the bars are cleared, then the recent lines of every watched log are fed to the
/// buff tracker in time order with their own times, so each bar comes back with the time it has left and the current durations.
/// </summary>
public static class BuffBarReload
{
    /// <summary>How far back the logs are read: longer than any buff a bar would still be showing for most players.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(3);

    /// <summary>One log line: whose log, its server, the message and its time (UTC).</summary>
    public readonly record struct ReplayLine(string Character, string Server, string Message, DateTime Utc);

    /// <summary>The lines of the logs since <paramref name="sinceLocal"/> (log clock), oldest first across every log.</summary>
    public static List<ReplayLine> ReadRecent(IEnumerable<(string Path, string Character, string Server)> logs, DateTime sinceLocal, CancellationToken cancel = default)
    {
        var lines = new List<ReplayLine>();
        foreach ((string path, string character, string server) in logs)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                stream.Position = LogSeek.FindOffset(stream, sinceLocal);
                using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (LogLineFormat.TryParseTimestamp(line, out DateTime local) && local >= sinceLocal)
                    {
                        lines.Add(new ReplayLine(character, server, LogLineFormat.GetMessage(line).ToString(),
                            DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime()));
                    }
                }
            }
            catch (IOException)
            {
                // a log that cannot be read adds nothing
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // A stable sort keeps each log's own order for lines in the same second.
        return lines.Select((l, i) => (l, i)).OrderBy(x => x.l.Utc).ThenBy(x => x.i).Select(x => x.l).ToList();
    }

    /// <summary>Clears the tracker's bars and plays <paramref name="lines"/> through it. Returns how many bars are up afterwards.</summary>
    public static int Replay(BuffTrackerService tracker, TriggerEngineService engine, IEnumerable<ReplayLine> lines, DateTime nowUtc)
    {
        foreach (var bar in engine.BarsOf(BuffTrackerService.TimerSourceId))
        {
            engine.CancelTimer(BuffTrackerService.TimerSourceId, bar.CharacterName, bar.Text);
        }

        tracker.Replaying = true; // the uptime of past fights was already counted
        try
        {
            foreach (ReplayLine line in lines)
            {
                tracker.OnLine(line.Character, line.Server, line.Message, line.Utc);
            }
        }
        finally
        {
            tracker.Replaying = false;
        }

        return engine.BarsOf(BuffTrackerService.TimerSourceId).Count(b => b.ExpiresUtc > nowUtc);
    }
}
