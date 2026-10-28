using System.Threading.Channels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.Core.Pipeline;

/// <summary>
/// The consumer half of the producer/consumer pipeline. Drains <see cref="LogEvent"/>s off the channel, so regex
/// parsing and state updates never run on a file-reading thread. Routing is decided by <see cref="LogEvent.Routes"/>:
/// a null sink (e.g. no trigger sink for a historical import) means that route is skipped outright.
/// </summary>
public static class LogEventProcessor
{
    /// <summary>
    /// Runs until the channel is completed (or <paramref name="cancellationToken"/> fires).
    /// Exceptions thrown by sinks are reported via <paramref name="onError"/> and do not stop the pipeline.
    /// </summary>
    public static async Task RunAsync(
        ChannelReader<LogEvent> reader,
        ITriggerLineSink? triggerSink,
        IDpsEventSink? dpsSink,
        Action<Exception>? onError,
        CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out LogEvent evt))
            {
                Dispatch(in evt, triggerSink, dpsSink, onError);
            }
        }
    }

    private static void Dispatch(in LogEvent evt, ITriggerLineSink? triggerSink, IDpsEventSink? dpsSink, Action<Exception>? onError)
    {
        if (triggerSink is not null && (evt.Routes & LogRoute.Triggers) != 0)
        {
            try
            {
                // No timestamp parse here: triggers evaluate the message span directly for minimum latency.
                triggerSink.OnLine(evt.CharacterName, evt.Message, evt.ReceivedUtc);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }

        if (dpsSink is not null && (evt.Routes & LogRoute.Dps) != 0)
        {
            try
            {
                DateTime timestamp = evt.LogTimestamp;
                if (timestamp == default && !LogLineFormat.TryParseTimestamp(evt.Line, out timestamp))
                {
                    return;
                }

                if (CombatLineParser.TryParse(evt.Line, timestamp, evt.CharacterName, out CombatEvent combatEvent))
                {
                    dpsSink.OnCombatEvent(in combatEvent);
                }
                else
                {
                    dpsSink.OnLogClock(timestamp);
                    if (evt.Line.Length > LogLineFormat.MessageOffset)
                    {
                        dpsSink.OnNonCombatMessage(evt.Line.AsSpan(LogLineFormat.MessageOffset));
                    }
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        }
    }
}
