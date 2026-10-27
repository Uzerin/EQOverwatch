using EQCompanion.Core.Models;

namespace EQCompanion.Core.Pipeline;

/// <summary>
/// Receives lines routed to the trigger/timer engine (implemented by <c>TriggerEngineService</c> in Phase 2).
/// Called on the pipeline's consumer thread, never on the UI thread. The message is a zero-allocation span over the
/// original line: implementations should pre-filter on the span and only materialize a string for candidate matches.
/// </summary>
public interface ITriggerLineSink
{
    /// <param name="characterName">Owner of the log file the line came from (drives the <c>${Character}</c> token).</param>
    /// <param name="message">Line text after the timestamp wrapper.</param>
    /// <param name="receivedUtc">Wall-clock instant the line was read (drives countdown timers).</param>
    void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc);
}

/// <summary>
/// Receives parsed combat events (implemented by <c>DpsTrackerService</c> in Phase 2).
/// Called on the pipeline's consumer thread, never on the UI thread, so implementations must marshal to the UI themselves.
/// </summary>
public interface IDpsEventSink
{
    /// <summary>
    /// A batch (historical import) is starting: suppress per-event UI notifications.
    /// Live mode does not use batches.
    /// </summary>
    void BeginBatch();

    /// <summary>Delivers one event. <see cref="CombatEvent.Timestamp"/> is the log clock; do not consult wall time.</summary>
    void OnCombatEvent(in CombatEvent combatEvent);

    /// <summary>
    /// The batch has been fully delivered: finalize open encounters and publish the result once.
    /// Always called after <see cref="BeginBatch"/>, even if the import failed or was cancelled.
    /// </summary>
    void EndBatch();

    /// <summary>
    /// Called for every DPS-routed line that carries a valid timestamp but is not a combat event (chat, spells, loot...).
    /// It advances the log clock so inactivity timeouts fire even when combat lines stop, without ever consulting
    /// wall time. Default implementation does nothing.
    /// </summary>
    void OnLogClock(DateTime logTimestamp)
    {
    }

    /// <summary>
    /// Tells the sink which character owns the log being delivered, so it can treat that name as a confirmed player.
    /// The historical importer calls this right after <see cref="BeginBatch"/>. Default implementation does nothing.
    /// </summary>
    void OnLogOwner(string characterName)
    {
    }
}
