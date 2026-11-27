using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.Core.Pipeline;

/// <summary>What an import read. <paramref name="CharacterName"/> is the log's character, or "N logs" when several were read together.</summary>
public sealed record HistoricalImportResult(string CharacterName, long LinesRead, long LinesQueued, TimeSpan Elapsed, int Logs = 1);

/// <summary>
/// Historical mode: streams one log file, or several merged in time order, and feeds the DPS sink only. There is deliberately no
/// trigger sink parameter, so triggers cannot fire during an import. The sink is bracketed by
/// <see cref="IDpsEventSink.BeginBatch"/> / <see cref="IDpsEventSink.EndBatch"/> so it can suppress per-event UI redraws
/// until the import completes.
/// <para>
/// "You"/"Your" are normalized to the character parsed from each file's name. With a start time, each file is searched for it first
/// (logs are in time order), so only the part wanted is read.
/// </para>
/// </summary>
public sealed class HistoricalLogImporter
{
    public const int ReadBufferSize = 64 * 1024;

    private const int ChannelCapacity = 16 * 1024;
    private const int ProgressLineInterval = 8192; // power of two: masked, not divided
    private const double ProgressMinStep = 0.005;

    // Logs are chronological, so once a line is past the end filter we can stop reading. The slack keeps a
    // daylight-saving "fall back" (clock repeats an hour) from ending the import early.
    private static readonly TimeSpan EarlyExitSlack = TimeSpan.FromHours(2);

    private readonly IDpsEventSink _dpsSink;
    private readonly Action<Exception>? _onError;

    public HistoricalLogImporter(IDpsEventSink dpsSink, Action<Exception>? onError = null)
    {
        _dpsSink = dpsSink ?? throw new ArgumentNullException(nameof(dpsSink));
        _onError = onError;
    }

    /// <param name="startTime">Inclusive lower bound on the line timestamp (log clock), or null.</param>
    /// <param name="endTime">Inclusive upper bound on the line timestamp (log clock), or null.</param>
    /// <param name="progress">Receives 0..1 based on bytes consumed. Reports are throttled.</param>
    public Task<HistoricalImportResult> ImportAsync(
        string filePath,
        DateTime? startTime = null,
        DateTime? endTime = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return ImportAsync(new[] { filePath }, startTime, endTime, progress, cancellationToken);
    }

    /// <summary>
    /// Reads several characters' logs as one, line by line in time order (lines with the same second keep the order of
    /// <paramref name="filePaths"/>). A line several logs saw is counted once by the DPS tracker.
    /// </summary>
    public async Task<HistoricalImportResult> ImportAsync(
        IReadOnlyList<string> filePaths,
        DateTime? startTime = null,
        DateTime? endTime = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0 || filePaths.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one log file is needed.", nameof(filePaths));
        }

        if (startTime.HasValue && endTime.HasValue && startTime.Value > endTime.Value)
        {
            throw new ArgumentException("startTime must not be later than endTime.", nameof(startTime));
        }

        string[] characters = filePaths.Select(LogFileName.GetCharacterName).ToArray();
        string characterName = characters.Length == 1 ? characters[0] : $"{characters.Length} logs";
        var stopwatch = Stopwatch.StartNew();
        var counters = new ImportCounters();

        // Bounded + Wait gives backpressure: a 500MB file cannot flood memory faster than the parser drains it.
        var channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = linked.Token;

        _dpsSink.BeginBatch();
        try
        {
            _dpsSink.OnLogOwner(characters[0]);

            Task producer = Task.Run(
                () => ProduceAsync(filePaths, startTime, endTime, channel.Writer, progress, counters, token),
                CancellationToken.None);

            Task consumer = Task.Run(
                () => LogEventProcessor.RunAsync(channel.Reader, triggerSink: null, _dpsSink, _onError, token),
                CancellationToken.None);

            // If the consumer ends first it faulted or was cancelled; unblock a producer waiting on a full channel.
            Task first = await Task.WhenAny(producer, consumer).ConfigureAwait(false);
            if (ReferenceEquals(first, consumer))
            {
                linked.Cancel();
            }

            await Task.WhenAll(producer, consumer).ConfigureAwait(false);
        }
        finally
        {
            _dpsSink.EndBatch();
        }

        progress?.Report(1.0);
        stopwatch.Stop();
        return new HistoricalImportResult(characterName, counters.Read, counters.Queued, stopwatch.Elapsed, filePaths.Count);
    }

    /// <summary>One log being read: its reader, and its next line with that line's time.</summary>
    private sealed class Source : IDisposable
    {
        public required FileStream Stream { get; init; }

        public required StreamReader Reader { get; init; }

        public required string Character { get; init; }

        public required string Server { get; init; }

        public long StartOffset { get; init; }

        public string? Line { get; set; }

        public DateTime Time { get; set; }

        public bool Done { get; set; }

        public void Dispose()
        {
            Reader.Dispose();
            Stream.Dispose();
        }
    }

    private static async Task ProduceAsync(
        IReadOnlyList<string> filePaths,
        DateTime? startTime,
        DateTime? endTime,
        ChannelWriter<LogEvent> writer,
        IProgress<double>? progress,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        Exception? error = null;
        var sources = new List<Source>();
        var zoneLines = new List<LogEvent>(); // each log's zone when the read starts, so fights get the right zone
        try
        {
            foreach (string path in filePaths)
            {
                // bufferSize 1 disables FileStream's buffer; the StreamReader supplies the 64KB read buffer.
                var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1,
                    FileOptions.SequentialScan | FileOptions.Asynchronous);
                long offset = 0;
                if (startTime.HasValue && startTime.Value > DateTime.MinValue + EarlyExitSlack)
                {
                    // The slack keeps lines just before a daylight-saving change; the start filter below drops them.
                    offset = LogSeek.FindOffset(stream, startTime.Value - EarlyExitSlack);
                    if (LogSeek.LastZoneLineBefore(stream, offset) is { } zoneLine
                        && LogLineFormat.TryParseTimestamp(zoneLine, out DateTime zoneTime))
                    {
                        zoneLines.Add(new LogEvent(
                            LogFileName.GetCharacterName(path), zoneLine, default, LogRoute.Dps, zoneTime,
                            LogFileName.TryParse(path, out _, out string zoneServer) ? zoneServer : string.Empty));
                    }

                    stream.Position = offset;
                }

                sources.Add(new Source
                {
                    Stream = stream,
                    Reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, ReadBufferSize),
                    Character = LogFileName.GetCharacterName(path),
                    Server = LogFileName.TryParse(path, out _, out string parsedServer) ? parsedServer : string.Empty,
                    StartOffset = offset,
                });
            }

            long totalBytes = sources.Sum(s => Math.Max(0, s.Stream.Length - s.StartOffset));
            DateTime earlyExitAfter = endTime.HasValue && endTime.Value < DateTime.MaxValue - EarlyExitSlack
                ? endTime.Value + EarlyExitSlack
                : DateTime.MaxValue;
            double lastReported = 0;
            foreach (LogEvent zoneLine in zoneLines)
            {
                await writer.WriteAsync(zoneLine, cancellationToken).ConfigureAwait(false);
            }

            foreach (Source source in sources)
            {
                await AdvanceAsync(source, earlyExitAfter, counters, cancellationToken).ConfigureAwait(false);
            }

            while (true)
            {
                // The next line in time: the earliest of each log's next line (the first log listed wins a tie).
                Source? next = null;
                foreach (Source source in sources)
                {
                    if (!source.Done && (next is null || source.Time < next.Time))
                    {
                        next = source;
                    }
                }

                if (next is null)
                {
                    break;
                }

                string line = next.Line!;
                DateTime timestamp = next.Time;
                bool queue = timestamp != default
                             && !(startTime.HasValue && timestamp < startTime.Value && !IsZoneLine(line))
                             && !(endTime.HasValue && timestamp > endTime.Value);
                if (queue)
                {
                    var evt = new LogEvent(next.Character, line, default, LogRoute.Dps, timestamp, next.Server);
                    if (!writer.TryWrite(evt))
                    {
                        await writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
                    }

                    counters.Queued++;
                }

                await AdvanceAsync(next, earlyExitAfter, counters, cancellationToken).ConfigureAwait(false);

                if (progress is not null && totalBytes > 0 && (counters.Read & (ProgressLineInterval - 1)) == 0)
                {
                    long done = sources.Sum(s => Math.Max(0, (s.Done ? s.Stream.Length : s.Stream.Position) - s.StartOffset));
                    double fraction = Math.Min(1.0, (double)done / totalBytes);
                    if (fraction - lastReported >= ProgressMinStep)
                    {
                        lastReported = fraction;
                        progress.Report(fraction);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            foreach (Source source in sources)
            {
                source.Dispose();
            }

            writer.TryComplete(error);
        }
    }

    /// <summary>"You have entered X." lines before the start are still read: they say which zone the first fights are in.</summary>
    private static bool IsZoneLine(string line) =>
        line.AsSpan(LogLineFormat.MessageOffset).StartsWith("You have entered ", StringComparison.Ordinal);

    /// <summary>Reads a log's next line that has a timestamp (other lines are skipped). Past the end filter the log is done.</summary>
    private static async Task AdvanceAsync(Source source, DateTime earlyExitAfter, ImportCounters counters, CancellationToken cancellationToken)
    {
        while (await source.Reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            counters.Read++;
            if (line.Length <= LogLineFormat.MessageOffset || !LogLineFormat.TryParseTimestamp(line, out DateTime timestamp))
            {
                continue;
            }

            if (timestamp > earlyExitAfter)
            {
                break;
            }

            source.Line = line;
            source.Time = timestamp;
            return;
        }

        source.Done = true;
        source.Line = null;
    }

    private sealed class ImportCounters
    {
        public long Read;
        public long Queued;
    }
}
