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
}
