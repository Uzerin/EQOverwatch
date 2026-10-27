namespace EQCompanion.Core.Models;

[Flags]
public enum TriggerActions
{
    None = 0,
    /// <summary>Show a text alert on the overlay for <see cref="TriggerDefinition.DurationSeconds"/>.</summary>
    TextOverlay = 1,
    /// <summary>Show a countdown bar for <see cref="TriggerDefinition.DurationSeconds"/>.</summary>
    CountdownTimer = 2,
    /// <summary>Ask the host to speak text (TTS) and/or play a sound file. Core never plays audio itself.</summary>
    Audio = 4,
}

/// <summary>
/// A user-defined trigger. <see cref="Pattern"/> is matched against the message text <i>after</i> the timestamp wrapper.
/// Templates (<see cref="DisplayText"/>, <see cref="AudioText"/>) may use <c>${Character}</c> (the character whose log
/// matched), <c>${Trigger}</c> (this trigger's name), and any named or numbered regex group, e.g. <c>${caster}</c>.
/// Unknown tokens are left as written.
/// </summary>
public sealed record TriggerDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public TriggerActions Actions { get; init; } = TriggerActions.TextOverlay;

    /// <summary>Alert/timer length. Values &lt;= 0 fall back to <see cref="DefaultDurationSeconds"/>.</summary>
    public double DurationSeconds { get; init; } = DefaultDurationSeconds;

    public string DisplayText { get; init; } = "${Character}: ${Trigger}";

    /// <summary>
    /// TTS template. When null, <see cref="DisplayText"/> is spoken instead, unless <see cref="AudioFilePath"/> is set
    /// (a sound-file-only trigger stays silent apart from the sound).
    /// </summary>
    public string? AudioText { get; init; }

    public string? AudioFilePath { get; init; }
    public bool IsEnabled { get; init; } = true;

    /// <summary>Which timer overlay shows this trigger's alert and countdown.</summary>
    public TimerOverlayCategory OverlayCategory { get; init; } = TimerOverlayCategory.RaidTriggers;

    /// <summary>Only fire for these characters (case-insensitive). Null or empty means every watched character.</summary>
    public IReadOnlyCollection<string>? Characters { get; init; }

    public const double DefaultDurationSeconds = 5.0;

    public double EffectiveDurationSeconds => DurationSeconds > 0 ? DurationSeconds : DefaultDurationSeconds;
}

public sealed record TriggerCompileError(TriggerDefinition Trigger, string Message);

/// <summary>Raised on the consumer thread each time a trigger matches. Hosts use it for audio and logging.</summary>
public sealed record TriggerFiredEvent(
    Guid TriggerId,
    string TriggerName,
    string CharacterName,
    TriggerActions Actions,
    string DisplayText,
    string? AudioText,
    string? AudioFilePath,
    double DurationSeconds,
    DateTime ReceivedUtc,
    TimerOverlayCategory Category = TimerOverlayCategory.RaidTriggers);
