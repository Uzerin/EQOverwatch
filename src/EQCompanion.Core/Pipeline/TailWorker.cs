using System.Text;
using System.Threading.Channels;
using EQCompanion.Core.Models;

namespace EQCompanion.Core.Pipeline;

/// <summary>
/// Tails one log file. Not thread-safe: <see cref="LogTailService"/> polls every worker from a single loop.
/// The file is opened <c>FileMode.Open / FileAccess.Read / FileShare.ReadWrite</c> so EverQuest keeps writing freely.
/// </summary>
internal sealed class TailWorker : IDisposable
{
    private const int InitialBufferSize = 64 * 1024;
    private const long ReopenIntervalMs = 1000;

    private readonly WatchedLogConfig _config;

    private FileStream? _stream;
    private bool _seekToEndOnOpen;
    private long _lastPosition;
    private long _nextOpenAttemptTicks;

    // Byte buffer holding [_consumed, _filled) = bytes read but not yet turned into complete lines.
    private byte[] _buffer = new byte[InitialBufferSize];
    private int _consumed;
    private int _filled;

    public TailWorker(WatchedLogConfig config)
    {
        _config = config;
        // A file that already exists is tailed from its end; one that appears later is read from the start.
        _seekToEndOnOpen = File.Exists(config.FilePath);
    }

    public WatchedLogConfig Config => _config;

    /// <summary>Opens (and seeks to the end of) an existing file immediately, so nothing written after this call is missed.</summary>
    public void Initialize() => TryOpen();

    public async ValueTask PollAsync(ChannelWriter<LogEvent> writer, CancellationToken cancellationToken)
    {
        if (_stream is null && !TryOpen())
        {
            return;
        }

        FileStream stream = _stream!;

        // Log truncated or replaced (e.g. /log reset): start over from the top.
        if (stream.Length < stream.Position)
        {
            stream.Position = 0;
            _consumed = _filled = 0;
        }

        if (stream.Length == stream.Position)
        {
            return;
        }

        // Nothing is listening to this log right now: skip the bytes without decoding them.
        if (GetRoutes() == LogRoute.None)
        {
            stream.Seek(0, SeekOrigin.End);
            _consumed = _filled = 0;
            return;
        }

        while (true)
        {
            if (_filled == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2); // a single line longer than the buffer
            }

            int read = stream.Read(_buffer, _filled, _buffer.Length - _filled);
            if (read <= 0)
            {
                break;
            }

            _filled += read;

            while (TryNextLine(out int lineStart, out int lineLength))
            {
                if (lineLength == 0)
                {
                    continue;
                }

                LogRoute routes = GetRoutes(); // re-read per line so UI toggles apply immediately
                if (routes == LogRoute.None)
                {
                    continue;
                }

                string line = Encoding.Latin1.GetString(_buffer, lineStart, lineLength);
                var evt = new LogEvent(_config.CharacterName, line, DateTime.UtcNow, routes);

                if (!writer.TryWrite(evt))
                {
                    await writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
                }
            }

            Compact();
        }
    }

    /// <summary>
    /// Drops the handle after an I/O error. The next poll reopens at the start of any unterminated line,
    /// so no partial line is ever emitted.
    /// </summary>
    public void Reset()
    {
        CloseStream();
        _nextOpenAttemptTicks = Environment.TickCount64 + ReopenIntervalMs;
    }

    public void Dispose() => CloseStream();

    private LogRoute GetRoutes()
    {
        LogRoute routes = LogRoute.None;
        if (_config.IsTriggerEnabled)
        {
            routes |= LogRoute.Triggers;
        }

        if (_config.IsPrimaryDps)
        {
            routes |= LogRoute.Dps;
        }

        return routes;
    }

    private bool TryOpen()
    {
        long now = Environment.TickCount64;
        if (now < _nextOpenAttemptTicks)
        {
            return false;
        }

        try
        {
            // bufferSize 1 disables FileStream's own buffer; we manage a 64KB buffer ourselves.
            var stream = new FileStream(
                _config.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, FileOptions.None);

            if (_seekToEndOnOpen)
            {
                stream.Seek(0, SeekOrigin.End);
                _seekToEndOnOpen = false;
            }
            else
            {
                stream.Position = _lastPosition <= stream.Length ? _lastPosition : 0;
            }

            _consumed = _filled = 0;
            _stream = stream;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Expected while EQ hasn't created the file yet or another process briefly holds it exclusively.
            _nextOpenAttemptTicks = now + ReopenIntervalMs;
            return false;
        }
    }

    private void CloseStream()
    {
        if (_stream is not null)
        {
            // Rewind over any unterminated line so it is re-read whole after reopening.
            _lastPosition = Math.Max(0, _stream.Position - (_filled - _consumed));
            _stream.Dispose();
            _stream = null;
        }

        _consumed = _filled = 0;
    }

    /// <summary>Finds the next '\n'-terminated line in the buffer (a trailing '\r' is excluded).</summary>
    private bool TryNextLine(out int lineStart, out int lineLength)
    {
        int index = _buffer.AsSpan(_consumed, _filled - _consumed).IndexOf((byte)'\n');
        if (index < 0)
        {
            lineStart = 0;
            lineLength = 0;
            return false;
        }

        int newline = _consumed + index;
        lineStart = _consumed;
        lineLength = newline - _consumed;
        _consumed = newline + 1;

        if (lineLength > 0 && _buffer[newline - 1] == (byte)'\r')
        {
            lineLength--;
        }

        return true;
    }

    /// <summary>Moves the unterminated tail of the buffer to the front so the next read appends to it.</summary>
    private void Compact()
    {
        int remaining = _filled - _consumed;
        if (remaining > 0 && _consumed > 0)
        {
            Buffer.BlockCopy(_buffer, _consumed, _buffer, 0, remaining);
        }

        _consumed = 0;
        _filled = remaining;
    }
}
