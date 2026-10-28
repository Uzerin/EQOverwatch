using System.Globalization;

namespace EQOverwatch.Core.Parsing;

/// <summary>
/// Zero-allocation handling of the fixed EverQuest line wrapper: <c>[ddd MMM dd HH:mm:ss yyyy] message</c>.
/// Characters 0..25 are the bracketed timestamp (26 chars), index 26 is a space, the message starts at 27.
/// </summary>
public static class LogLineFormat
{
    public const int TimestampWidth = 26;
    public const int MessageOffset = 27;
    public const string TimestampFormat = "ddd MMM dd HH:mm:ss yyyy";

    /// <summary>
    /// Parses the timestamp wrapper by slicing the first 26 characters as a span (no substring allocation).
    /// The result is local log time (<see cref="DateTimeKind.Unspecified"/>); the day-of-week must match the date.
    /// </summary>
    public static bool TryParseTimestamp(ReadOnlySpan<char> line, out DateTime timestamp)
    {
        timestamp = default;

        if (line.Length < TimestampWidth)
        {
            return false;
        }

        ReadOnlySpan<char> wrapper = line[..TimestampWidth];
        if (wrapper[0] != '[' || wrapper[TimestampWidth - 1] != ']')
        {
            return false;
        }

        return DateTime.TryParseExact(
            wrapper.Slice(1, TimestampWidth - 2),
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out timestamp);
    }

    /// <summary>Returns the message portion of a line (empty if the line is just a wrapper).</summary>
    public static ReadOnlySpan<char> GetMessage(ReadOnlySpan<char> line) =>
        line.Length > MessageOffset ? line[MessageOffset..] : ReadOnlySpan<char>.Empty;
}
