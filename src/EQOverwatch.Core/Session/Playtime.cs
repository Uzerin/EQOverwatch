using System.Text;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Session;

/// <summary>
/// Hours played per day from a log's timestamps: the time between lines counts as played unless the gap is longer than
/// <see cref="DayAccumulator.OfflineGap"/>, the same rule as the Session tab.
/// </summary>
public static class Playtime
{
    /// <summary>Played seconds per local day from <paramref name="fromLocal"/> on. An unreadable log gives nothing.</summary>
    public static Dictionary<DateTime, double> PerDay(string path, DateTime fromLocal, CancellationToken cancellation = default)
    {
        var days = new Dictionary<DateTime, double>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = LogSeek.FindOffset(stream, fromLocal);
            using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
            DateTime? last = null;
            string? line;
            int count = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                if ((++count & 0xFFFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                if (!LogLineFormat.TryParseTimestamp(line, out DateTime time) || time < fromLocal)
                {
                    continue;
                }

                if (last is { } previous && time >= previous && time - previous <= DayAccumulator.OfflineGap)
                {
                    // Split a stretch that crosses midnight between the two days.
                    DateTime midnight = time.Date;
                    if (previous < midnight)
                    {
                        days[previous.Date] = days.GetValueOrDefault(previous.Date) + (midnight - previous).TotalSeconds;
                        days[time.Date] = days.GetValueOrDefault(time.Date) + (time - midnight).TotalSeconds;
                    }
                    else
                    {
                        days[time.Date] = days.GetValueOrDefault(time.Date) + (time - previous).TotalSeconds;
                    }
                }

                last = time;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // nothing for an unreadable log
        }

        return days;
    }
}
