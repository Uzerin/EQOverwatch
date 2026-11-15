using System.Text;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.Core.Progress;

/// <summary>Reads a whole EverQuest log once, handing the lines a reader wants to it with their time (unix seconds, UTC).</summary>
public static class LogHistoryReader
{
    /// <summary>
    /// Calls <paramref name="onLine"/> for every line whose message <paramref name="wanted"/> accepts, oldest first. Returns the time of the
    /// last line read (0 when none), or null when the file could not be read.
    /// </summary>
    public static long? Read(string path, Func<string, bool> wanted, Action<string, long> onLine, CancellationToken cancellation = default)
    {
        long last = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
            long count = 0;
            while (reader.ReadLine() is { } line)
            {
                if ((++count & 0xFFFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                if (line.Length <= LogLineFormat.MessageOffset)
                {
                    continue;
                }

                string message = line[LogLineFormat.MessageOffset..];
                if (!wanted(message) || !LogLineFormat.TryParseTimestamp(line, out DateTime stamp))
                {
                    continue;
                }

                last = new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Local).ToUniversalTime()).ToUnixTimeSeconds();
                onLine(message, last);
            }

            return last;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }

    public static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
}
