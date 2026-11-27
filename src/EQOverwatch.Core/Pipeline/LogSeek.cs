using System.Text;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.Core.Pipeline;

/// <summary>Finds where a time starts in a log without reading what comes before it (a log's lines are in time order).</summary>
public static class LogSeek
{
    private const int Probe = 4096;
    private const long CloseEnough = 256 * 1024;

    /// <summary>
    /// The start of a line at or shortly before the first line timestamped <paramref name="time"/> or later: every line before the
    /// offset returned is older. 0 when the file starts later or cannot be searched; the stream's position is left changed.
    /// </summary>
    public static long FindOffset(Stream stream, DateTime time)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            return 0;
        }

        long lo = 0; // a line start known to be older than time (or the file start)
        long hi = stream.Length;
        byte[] buffer = new byte[Probe];
        while (hi - lo > CloseEnough)
        {
            long mid = lo + (hi - lo) / 2;
            if (FirstLineAfter(stream, mid, buffer) is not { } found)
            {
                hi = mid; // no full line there (the end of the file)
                continue;
            }

            if (found.Time < time)
            {
                lo = found.Start;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>The first whole line that starts after <paramref name="at"/> and has a timestamp: where it starts and its time.</summary>
    private static (long Start, DateTime Time)? FirstLineAfter(Stream stream, long at, byte[] buffer)
    {
        stream.Position = at;
        int read = ReadFull(stream, buffer);
        int i = Array.IndexOf(buffer, (byte)'\n', 0, read);
        while (i >= 0 && i + 1 < read)
        {
            int start = i + 1;
            int end = Array.IndexOf(buffer, (byte)'\n', start, read - start);
            if (end < 0)
            {
                return null;
            }

            string line = Encoding.Latin1.GetString(buffer, start, end - start).TrimEnd('\r');
            if (LogLineFormat.TryParseTimestamp(line, out DateTime time))
            {
                return (at + start, time);
            }

            i = end;
        }

        return null;
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    /// <summary>How far <see cref="LastZoneLineBefore"/> looks back: a long raid night can log this much in one zone.</summary>
    public const long ZoneLookBackBytes = 256L * 1024 * 1024;

    private static readonly byte[] EnteredMarker = Encoding.Latin1.GetBytes("] You have entered ");

    /// <summary>
    /// The last real "You have entered X." line (a zone, not "an area" message) that ends before <paramref name="offset"/>, read
    /// backwards at most <paramref name="maxBack"/> bytes; null when there is none. The stream's position is left changed.
    /// </summary>
    public static string? LastZoneLineBefore(Stream stream, long offset, long maxBack = ZoneLookBackBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || offset <= 0)
        {
            return null;
        }

        offset = Math.Min(offset, stream.Length);
        const int Chunk = 1 << 20;
        byte[] buffer = new byte[Chunk + 4096];
        long end = offset;
        long limit = Math.Max(0, offset - maxBack);
        while (end > limit)
        {
            long start = Math.Max(limit, end - Chunk);
            int length = (int)(end - start);
            // Read a little past the chunk so a line that crosses its end is whole (never past offset).
            int extra = (int)Math.Min(4096, offset - end);
            stream.Position = start;
            int read = ReadFull(stream, buffer.AsSpan(0, length + extra));
            ReadOnlySpan<byte> span = buffer.AsSpan(0, read);
            int searchEnd = read; // the extra bytes are searched again: a line cut at the previous chunk's start is whole here
            while (searchEnd > 0)
            {
                int at = span[..searchEnd].LastIndexOf(EnteredMarker);
                if (at < 0)
                {
                    break;
                }

                int lineStart = span[..at].LastIndexOf((byte)10) + 1;
                int lineEnd = span[at..].IndexOf((byte)10);
                if (lineEnd >= 0 && (lineStart > 0 || start == 0))
                {
                    string line = Encoding.Latin1.GetString(span[lineStart..(at + lineEnd)]).TrimEnd((char)13);
                    if (line.Length > LogLineFormat.MessageOffset
                        && EQOverwatch.Core.Maps.ZoneTracker.ParseZone(line.AsSpan(LogLineFormat.MessageOffset)) is not null)
                    {
                        return line;
                    }
                }

                searchEnd = at;
            }

            end = start;
        }

        return null;
    }

    private static int ReadFull(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
