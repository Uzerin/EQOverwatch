using EQCompanion.Core.Parsing;

namespace EQCompanion.Core.Models;

/// <summary>Which downstream consumers a log line must be delivered to.</summary>
[Flags]
public enum LogRoute : byte
{
    None = 0,
    /// <summary>Deliver to the trigger/timer engine (wall-clock driven).</summary>
    Triggers = 1,
    /// <summary>Deliver to the DPS tracker (log-clock driven).</summary>
    Dps = 2,
}

/// <summary>
/// One raw log line travelling through the producer/consumer channel.
/// The line is kept as the original string; <see cref="Message"/> is a zero-allocation slice of it.
/// </summary>
public readonly struct LogEvent
{
    public LogEvent(string characterName, string line, DateTime receivedUtc, LogRoute routes, DateTime logTimestamp = default)
    {
        CharacterName = characterName;
        Line = line;
        ReceivedUtc = receivedUtc;
        Routes = routes;
        LogTimestamp = logTimestamp;
    }

    /// <summary>Character that owns the log file this line came from.</summary>
    public string CharacterName { get; }

    /// <summary>Full line including the 26-character <c>[ddd MMM dd HH:mm:ss yyyy]</c> wrapper.</summary>
    public string Line { get; }

    /// <summary>
    /// Wall-clock instant the line was read from disk (live mode only; <c>default</c> for historical).
    /// Used exclusively by the trigger/timer engine.
    /// </summary>
    public DateTime ReceivedUtc { get; }

    public LogRoute Routes { get; }

    /// <summary>
    /// Timestamp parsed from the line, or <c>default</c> when it has not been parsed yet.
    /// The DPS path parses lazily; the historical importer fills this in when it already parsed it for filtering.
    /// </summary>
    public DateTime LogTimestamp { get; }

    public bool HasLogTimestamp => LogTimestamp != default;

    /// <summary>The text after the timestamp wrapper, without allocating.</summary>
    public ReadOnlySpan<char> Message => LogLineFormat.GetMessage(Line);
}
