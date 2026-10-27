using EQCompanion.Core.Models;
using EQCompanion.Core.Spells;

namespace EQCompanion.Core.Settings;

/// <summary>
/// The old single-announcement setting. Only read from settings files and share tokens written before the start and
/// 10-second announcements were separated; <see cref="RaidBossTimerRule.ApplyLegacyTiming"/> converts it.
/// </summary>
public enum RaidWarningTiming
{
    /// <summary>The moment the timer starts (the ability has just been cast).</summary>
    AtStart,

    /// <summary>When the warning lead time remains (the next cast is about to happen).</summary>
    FiveSecondsRemaining,

    /// <summary>Both.</summary>
    Both,
}

/// <summary>
/// A recast countdown for one boss ability. Different bosses cast the same spell on different intervals (Tormax Dragon Roar
/// every 30 s, Velketor every 48 s), so the interval belongs to the rule, not to the spell.
/// </summary>
public sealed class RaidBossTimerRule
{
    /// <summary>How long before the next cast the second announcement (<see cref="WarningTtsText"/>) is spoken.</summary>
    public const int WarningLeadSeconds = 10;

    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsEnabled { get; set; } = true;

    /// <summary>The boss. Empty means any boss.</summary>
    public string MobName { get; set; } = string.Empty;

    public int SpellId { get; set; }
    public string SpellName { get; set; } = string.Empty;

    /// <summary>Text to look for in a log line (case-sensitive substring). Filled in from the spell; can be edited by hand.</summary>
    public string MatchText { get; set; } = string.Empty;

    public int RecastSeconds { get; set; } = 30;
    public TimerOverlayCategory OverlayCategory { get; set; } = TimerOverlayCategory.RaidTriggers;

    /// <summary>Optional text to speak the moment the trigger starts. May use ${MobName} and ${SpellName}.</summary>
    public string StartTtsText { get; set; } = string.Empty;

    /// <summary>
    /// Optional text to speak when <see cref="WarningLeadSeconds"/> seconds remain before the next cast. May use ${MobName} and
    /// ${SpellName}. Not spoken when the recast time is that short or shorter.
    /// </summary>
    public string WarningTtsText { get; set; } = string.Empty;

    /// <summary>Legacy only (null for current rules): see <see cref="RaidWarningTiming"/> and <see cref="ApplyLegacyTiming"/>.</summary>
    public RaidWarningTiming? WarningTiming { get; set; }

    /// <summary>The bar's label: "Tormax: Dragon Roar", or just the spell name when the rule applies to any boss.</summary>
    public string Label => string.IsNullOrWhiteSpace(MobName) ? SpellName : $"{MobName.Trim()}: {SpellName}";

    public RaidBossTimerRule Clone() => (RaidBossTimerRule)MemberwiseClone();

    /// <summary>
    /// What to match for a spell: its cast-on-you message when it has one, otherwise its cast-on-other message without the
    /// leading space or apostrophe-s spacing. Empty when the spell has neither.
    /// </summary>
    public static string SuggestMatchText(SpellDefinition spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.CastOnYou.Length > 0 ? spell.CastOnYou : spell.CastOnOther.Trim();
    }

    /// <summary>
    /// Converts a rule written with the old single text plus timing setting into separate start and warning texts: at start only
    /// becomes a start text, at the lead time only stays a warning, both becomes both. Does nothing for current rules, and a rule
    /// that already has a start text keeps its texts as they are.
    /// </summary>
    public void ApplyLegacyTiming()
    {
        if (WarningTiming is not { } timing)
        {
            return;
        }

        WarningTiming = null;
        if (!string.IsNullOrWhiteSpace(StartTtsText) || string.IsNullOrWhiteSpace(WarningTtsText))
        {
            return;
        }

        switch (timing)
        {
            case RaidWarningTiming.AtStart:
                StartTtsText = WarningTtsText;
                WarningTtsText = string.Empty;
                break;
            case RaidWarningTiming.Both:
                StartTtsText = WarningTtsText;
                break;
        }
    }

    /// <summary>The start announcement with its tokens filled in.</summary>
    public string RenderStart() => Render(StartTtsText);

    /// <summary>The warning announcement with its tokens filled in.</summary>
    public string RenderWarning() => Render(WarningTtsText);

    private string Render(string text) => text
        .Replace("${MobName}", string.IsNullOrWhiteSpace(MobName) ? "boss" : MobName.Trim(), StringComparison.OrdinalIgnoreCase)
        .Replace("${SpellName}", SpellName, StringComparison.OrdinalIgnoreCase);
}
