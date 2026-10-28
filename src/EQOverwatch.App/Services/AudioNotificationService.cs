using System.IO;
using System.Media;
using System.Speech.Synthesis;
using System.Threading.Channels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.Services;

/// <summary>
/// Plays the Audio action of triggers. <see cref="TriggerEngineService.TriggerFired"/> only enqueues the event
/// (non-blocking, bounded, drop-oldest), and a background worker does the playback, so audio can never stall the
/// pipeline's consumer thread. A <c>.wav</c> file plays through <see cref="SoundPlayer"/> (one at a time, in order);
/// the rendered <see cref="TriggerFiredEvent.AudioText"/> is spoken with <see cref="SpeechSynthesizer.SpeakAsync(string)"/>.
/// </summary>
public sealed class AudioNotificationService : IAudioControl
{
    private readonly TriggerEngineService _engine;
    private readonly Channel<TriggerFiredEvent> _queue = Channel.CreateBounded<TriggerFiredEvent>(
        new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private SpeechSynthesizer? _synthesizer;
    private volatile bool _isEnabled = true;
    private int _disposed;

    public AudioNotificationService(TriggerEngineService engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _engine.TriggerFired += OnTriggerFired;
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Raised on the worker thread when a sound or speech request fails.</summary>
    public event Action<Exception>? Error;

    /// <summary>Mute switch. Events that arrive while disabled are dropped, not queued.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>Speech volume, 0..100.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Speech rate, -10 (slow) to 10 (fast).</summary>
    public int Rate { get; set; }

    private void OnTriggerFired(TriggerFiredEvent e)
    {
        if (_isEnabled && (e.Actions & TriggerActions.Audio) != 0)
        {
            _queue.Writer.TryWrite(e);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TriggerFiredEvent e in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Play(e);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Error?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void Play(TriggerFiredEvent e)
    {
        if (!string.IsNullOrWhiteSpace(e.AudioFilePath))
        {
            if (!File.Exists(e.AudioFilePath))
            {
                throw new FileNotFoundException("Trigger sound file not found.", e.AudioFilePath);
            }

            using var player = new SoundPlayer(e.AudioFilePath);
            player.PlaySync(); // serializes sounds on the worker thread; SoundPlayer supports .wav only
        }

        if (!string.IsNullOrWhiteSpace(e.AudioText))
        {
            _synthesizer ??= new SpeechSynthesizer();
            _synthesizer.Volume = Math.Clamp(Volume, 0, 100);
            _synthesizer.Rate = Math.Clamp(Rate, -10, 10);
            _synthesizer.SpeakAsync(e.AudioText); // the synthesizer queues prompts itself
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _engine.TriggerFired -= OnTriggerFired;
        _queue.Writer.TryComplete();
        _cts.Cancel();

        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // worker already reported its errors through Error
        }

        if (_synthesizer is not null)
        {
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.Dispose();
        }

        _cts.Dispose();
    }
}
