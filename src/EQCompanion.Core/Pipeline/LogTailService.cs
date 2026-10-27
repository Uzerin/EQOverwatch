using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Threading.Channels;
using EQCompanion.Core.Models;

namespace EQCompanion.Core.Pipeline;

/// <summary>
/// Live mode producer. Concurrently tails every watched log from one loop driven by a 50ms <see cref="PeriodicTimer"/>,
/// seeking to the end of each file on startup and pushing newly flushed lines into the channel with their routing flags
/// (<see cref="LogRoute.Triggers"/> for trigger-enabled logs, <see cref="LogRoute.Dps"/> only for the single primary log).
/// </summary>
public sealed class LogTailService : IAsyncDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly ChannelWriter<LogEvent> _writer;
    private readonly TimeSpan _pollInterval;

    private readonly object _gate = new();
    private readonly Dictionary<WatchedLogConfig, TailWorker> _workers = new();
    private readonly ConcurrentQueue<TailWorker> _retired = new();
    private TailWorker[] _snapshot = Array.Empty<TailWorker>(); // copy-on-write; read lock-free by the poll loop

    private WatchedLogCollection? _boundLogs;
    private NotifyCollectionChangedEventHandler? _collectionHandler;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LogTailService(ChannelWriter<LogEvent> writer, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>Raised on a thread-pool thread when reading a log fails unexpectedly; the log is reopened automatically.</summary>
    public event Action<WatchedLogConfig, Exception>? TailError;

    /// <summary>Starts the polling loop. Add logs with <see cref="AddLog"/> or <see cref="Bind"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                throw new InvalidOperationException("The tail service is already running.");
            }

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    /// <summary>Starts tailing a log (idempotent). An existing file is positioned at its end immediately.</summary>
    public void AddLog(WatchedLogConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            AddLocked(config);
            PublishSnapshot();
        }
    }

    public void RemoveLog(WatchedLogConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            RemoveLocked(config);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// Keeps the set of tailed files in sync with a <see cref="WatchedLogCollection"/> (adds, removes, clears).
    /// Call on the UI thread, which is where the collection raises its change events.
    /// </summary>
    public void Bind(WatchedLogCollection logs)
    {
        ArgumentNullException.ThrowIfNull(logs);

        lock (_gate)
        {
            if (_boundLogs is not null)
            {
                throw new InvalidOperationException("A collection is already bound.");
            }

            _boundLogs = logs;
            _collectionHandler = (_, _) => Reconcile(logs);
            logs.CollectionChanged += _collectionHandler;
        }

        Reconcile(logs);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;

        lock (_gate)
        {
            if (_boundLogs is not null && _collectionHandler is not null)
            {
                _boundLogs.CollectionChanged -= _collectionHandler;
            }

            _boundLogs = null;
            _collectionHandler = null;

            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
            try
            {
                if (loop is not null)
                {
                    await loop.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            finally
            {
                cts.Dispose();
            }
        }

        lock (_gate)
        {
            foreach (TailWorker worker in _workers.Values)
            {
                worker.Dispose();
            }

            _workers.Clear();
            PublishSnapshot();
        }

        while (_retired.TryDequeue(out TailWorker? dead))
        {
            dead.Dispose();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            // Retired workers are disposed here, on the only thread that polls, so a close never races a read.
            while (_retired.TryDequeue(out TailWorker? dead))
            {
                dead.Dispose();
            }

            foreach (TailWorker worker in Volatile.Read(ref _snapshot))
            {
                try
                {
                    await worker.PollAsync(_writer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    worker.Reset();
                    TailError?.Invoke(worker.Config, ex);
                }
            }
        }
    }

    private void Reconcile(IEnumerable<WatchedLogConfig> desired)
    {
        HashSet<WatchedLogConfig> wanted = desired.ToHashSet();

        lock (_gate)
        {
            foreach (WatchedLogConfig existing in _workers.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                RemoveLocked(existing);
            }

            foreach (WatchedLogConfig config in wanted)
            {
                AddLocked(config);
            }

            PublishSnapshot();
        }
    }

    private void AddLocked(WatchedLogConfig config)
    {
        if (_workers.ContainsKey(config))
        {
            return;
        }

        var worker = new TailWorker(config);
        worker.Initialize();
        _workers.Add(config, worker);
    }

    private void RemoveLocked(WatchedLogConfig config)
    {
        if (_workers.Remove(config, out TailWorker? worker))
        {
            _retired.Enqueue(worker);
        }
    }

    private void PublishSnapshot() => Volatile.Write(ref _snapshot, _workers.Values.ToArray());
}
