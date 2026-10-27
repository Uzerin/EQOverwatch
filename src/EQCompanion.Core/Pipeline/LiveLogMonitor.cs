using System.Threading.Channels;
using EQCompanion.Core.Models;

namespace EQCompanion.Core.Pipeline;

/// <summary>
/// Wires live mode end to end: <see cref="LogTailService"/> (producer) → bounded channel →
/// <see cref="LogEventProcessor"/> (consumer) → trigger and DPS sinks. This is the object the UI starts and stops.
/// </summary>
public sealed class LiveLogMonitor : IAsyncDisposable
{
    public const int ChannelCapacity = 8192;

    private readonly WatchedLogCollection _logs;
    private readonly ITriggerLineSink _triggerSink;
    private readonly IDpsEventSink _dpsSink;
    private readonly Action<Exception>? _onError;

    private LogTailService? _tail;
    private Channel<LogEvent>? _channel;
    private Task? _consumer;

    public LiveLogMonitor(
        WatchedLogCollection logs,
        ITriggerLineSink triggerSink,
        IDpsEventSink dpsSink,
        Action<Exception>? onError = null)
    {
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _triggerSink = triggerSink ?? throw new ArgumentNullException(nameof(triggerSink));
        _dpsSink = dpsSink ?? throw new ArgumentNullException(nameof(dpsSink));
        _onError = onError;
    }

    public event Action<WatchedLogConfig, Exception>? TailError;

    public bool IsRunning => _tail is not null;

    /// <summary>Call on the UI thread (it binds to the collection's change events).</summary>
    public void Start()
    {
        if (_tail is not null)
        {
            throw new InvalidOperationException("The monitor is already running.");
        }

        _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false, // one producer loop today, but AddLog can race with it
            FullMode = BoundedChannelFullMode.Wait,
        });

        var tail = new LogTailService(_channel.Writer);
        tail.TailError += (config, ex) => TailError?.Invoke(config, ex);
        tail.Bind(_logs);

        ChannelReader<LogEvent> reader = _channel.Reader;
        _consumer = Task.Run(
            () => LogEventProcessor.RunAsync(reader, _triggerSink, _dpsSink, _onError, CancellationToken.None),
            CancellationToken.None);

        tail.Start();
        _tail = tail;
    }

    public async Task StopAsync()
    {
        LogTailService? tail = _tail;
        if (tail is null)
        {
            return;
        }

        _tail = null;

        await tail.StopAsync().ConfigureAwait(false);   // no more producers
        _channel!.Writer.TryComplete();                  // let the consumer drain what is queued
        await _consumer!.ConfigureAwait(false);

        _consumer = null;
        _channel = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
