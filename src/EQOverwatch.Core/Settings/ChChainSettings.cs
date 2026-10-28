using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.Settings;

/// <summary>How the Complete Heal chain is announced and cued in raid chat.</summary>
public sealed class ChChainSettings
{
    /// <summary>The token in <see cref="PositionTagFormat"/> that stands for the cleric's position number.</summary>
    public const string PositionToken = "{N}";

    public const string DefaultPositionTagFormat = "{N}-{N}-{N}";
    public const string DefaultCueKeyword = "CAST NOW";
    public const int DefaultTotalClerics = 4;
    public const double DefaultLateThresholdSeconds = 1.0;
    public const double MinLateThresholdSeconds = 0.1;
    public const double MaxLateThresholdSeconds = 9.0;
    public const int MinTotalClerics = 1;
    public const int MaxTotalClerics = WatchedLogConfig.MaxChChainPosition;

    /// <summary>Raid-say prefix, e.g. "{N}-{N}-{N}" gives "3-3-3". Every {N} is the same number.</summary>
    public string PositionTagFormat { get; set; } = DefaultPositionTagFormat;

    /// <summary>The words that follow a position tag when a cleric is told to cast, e.g. "CAST NOW".</summary>
    public string CueKeyword { get; set; } = DefaultCueKeyword;

    /// <summary>How many clerics are in the chain (1-12). Used to validate positions.</summary>
    public int TotalClerics { get; set; } = DefaultTotalClerics;

    /// <summary>How long after a cue the assigned cleric may take to start casting before the alarm sounds.</summary>
    public double LateThresholdSeconds { get; set; } = DefaultLateThresholdSeconds;

    public ChChainSettings Clone() => (ChChainSettings)MemberwiseClone();

    /// <summary>True when the format has at least one {N} and nothing that would make a chat line unmatchable (line breaks).</summary>
    public static bool IsValidTagFormat(string? format) =>
        !string.IsNullOrWhiteSpace(format)
        && format.Contains(PositionToken, StringComparison.Ordinal)
        && format.IndexOfAny(new[] { '\r', '\n', '\'' }) < 0;

    /// <summary>A copy with every value forced into its valid range (bad formats and blank cues fall back to the defaults).</summary>
    public ChChainSettings Sanitized() => new()
    {
        PositionTagFormat = IsValidTagFormat(PositionTagFormat) ? PositionTagFormat.Trim() : DefaultPositionTagFormat,
        CueKeyword = string.IsNullOrWhiteSpace(CueKeyword) || CueKeyword.Contains('\'') ? DefaultCueKeyword : CueKeyword.Trim(),
        TotalClerics = Math.Clamp(TotalClerics, MinTotalClerics, MaxTotalClerics),
        LateThresholdSeconds = double.IsFinite(LateThresholdSeconds)
            ? Math.Clamp(LateThresholdSeconds, MinLateThresholdSeconds, MaxLateThresholdSeconds)
            : DefaultLateThresholdSeconds,
    };
}
