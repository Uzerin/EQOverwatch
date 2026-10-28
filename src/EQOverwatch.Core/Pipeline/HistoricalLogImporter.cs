using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.Core.Pipeline;

public sealed record HistoricalImportResult(string CharacterName, long LinesRead, long LinesQueued, TimeSpan Elapsed);

/// <summary>
/// Historical mode: streams one log file from the beginning and feeds the DPS sink only. There is deliberately no
/// trigger sink parameter, so triggers cannot fire during an import. The sink is bracketed by
/// <see cref="IDpsEventSink.BeginBatch"/> / <see cref="IDpsEventSink.EndBatch"/> so it can suppress per-event UI redraws
/// until the import completes.
/// <para>
/// Because the file is the primary DPS source by definition, "You"/"Your" are normalized to the character parsed from
/// the file name. Timestamps are only parsed for the date filter, or lazily by the consumer when no filter is set.
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
    public async Task<HistoricalImportResult> ImportAsync(
        string filePath,
        DateTime? startTime = null,
        DateTime? endTime = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (startTime.HasValue && endTime.HasValue && startTime.Value > endTime.Value)
        {
            throw new ArgumentException("startTime must not be later than endTime.", nameof(startTime));
        }

        string characterName = LogFileName.GetCharacterName(filePath);
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
            _dpsSink.OnLogOwner(characterName);

            Task producer = Task.Run(
                () => ProduceAsync(filePath, characterName, startTime, endTime, channel.Writer, progress, counters, token),
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
        return new HistoricalImportResult(characterName, counters.Read, counters.Queued, stopwatch.Elapsed);
    }

    private static async Task ProduceAsync(
        string filePath,
        string characterName,
        DateTime? startTime,
        DateTime? endTime,
        ChannelWriter<LogEvent> writer,
        IProgress<double>? progress,
        ImportCounters counters,
        CancellationToken cancellationToken)
    {
        Exception? error = null;
        try
        {
            // bufferSize 1 disables FileStream's buffer; the StreamReader supplies the 64KB read buffer.
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, ReadBufferSize);

            long totalBytes = stream.Length;
            bool filtered = startTime.HasValue || endTime.HasValue;
            DateTime earlyExitAfter = endTime.HasValue && endTime.Value < DateTime.MaxValue - EarlyExitSlack
                ? endTime.Value + EarlyExitSlack
                : DateTime.MaxValue;
            double lastReported = 0;

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                counters.Read++;

                bool queue = line.Length > LogLineFormat.MessageOffset;
                DateTime timestamp = default;

                if (queue && filtered)
                {
                    if (!LogLineFormat.TryParseTimestamp(line, out timestamp))
                    {
                        queue = false;
                    }
                    else if (timestamp > earlyExitAfter)
                    {
                        break;
                    }
                    else if ((startTime.HasValue && timestamp < startTime.Value) ||
                             (endTime.HasValue && timestamp > endTime.Value))
                    {
                        queue = false;
                    }
                }

                if (queue)
                {
                    var evt = new LogEvent(characterName, line, default, LogRoute.Dps, timestamp);
                    if (!writer.TryWrite(evt))
                    {
                        await writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
                    }

                    counters.Queued++;
                }

                if (progress is not null && totalBytes > 0 && (counters.Read & (ProgressLineInterval - 1)) == 0)
                {
                    double fraction = Math.Min(1.0, (double)stream.Position / totalBytes);
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
            writer.TryComplete(error);
        }
    }

    private sealed class ImportCounters
    {
        public long Read;
        public long Queued;
    }
}
