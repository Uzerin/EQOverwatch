using EQCompanion.Core.Models;
using EQCompanion.Core.Pipeline;

namespace EQCompanion.Core.Services;

/// <summary>One built-in class cooldown, as listed on the Raid Triggers tab.</summary>
public sealed record CooldownRuleInfo(string Label, int CooldownSeconds, string Detection)
{
    /// <summary>"15:00", "72:00".</summary>
    public string CooldownText => $"{CooldownSeconds / 60}:{CooldownSeconds % 60:00}";
}

/// <summary>
/// Starts cooldown bars for class abilities whose log message does not say which ability it was. Today that is the Cleric's
/// Divine Aura: the <i>spell</i> (15 minute recast), the <i>AA</i> Divine Aura and the AA <i>Bestowed Divine Aura</i> (72 minutes each)
/// all end with <c>A divine aura surrounds you.</c>. The line before it tells them apart:
/// <list type="number">
/// <item><description><c>You begin casting Divine Aura.</c> within 2.5 s: "Spell: Divine Aura (15:00 CD)".</description></item>
/// <item><description><c>You activate Bestowed Divine Aura.</c> within 2.5 s: "AA: Bestowed Divine Aura (72:00 CD)".</description></item>
/// <item><description>Neither: it can only be an AA. Bestowed Divine Aura is assumed unless its bar is still running, in which case
/// it is "AA: Divine Aura (72:00 CD)".</description></item>
/// </list>
/// The bar is shown for the character whose log saw the landing message. When both announcements precede the message, the later one wins.
/// </summary>
public sealed class CooldownTrackerService : ITriggerLineSink
{
    public static readonly Guid TimerSourceId = new("a41f6c0e-92b7-4d35-8e1a-6b3d5f7c2e90");

    public const string TimerSourceName = "Class cooldowns";

    public const string BeginCastingDivineAura = "You begin casting Divine Aura.";
    public const string ActivateBestowedDivineAura = "You activate Bestowed Divine Aura.";
    public const string DivineAuraLanded = "A divine aura surrounds you.";

    public const string SpellDivineAuraLabel = "Spell: Divine Aura (15:00 CD)";
    public const string BestowedDivineAuraLabel = "AA: Bestowed Divine Aura (72:00 CD)";
    public const string AaDivineAuraLabel = "AA: Divine Aura (72:00 CD)";

    public const int SpellDivineAuraSeconds = 15 * 60;
    public const int AaDivineAuraSeconds = 72 * 60;

    /// <summary>How long before the landing message an announcement still counts as its cause.</summary>
    public static readonly TimeSpan AnnouncementWindow = TimeSpan.FromMilliseconds(2500);

    /// <summary>The abilities this service recognizes, for display.</summary>
    public static IReadOnlyList<CooldownRuleInfo> BuiltInRules { get; } = new[]
    {
        new CooldownRuleInfo(SpellDivineAuraLabel, SpellDivineAuraSeconds,
            "\"You begin casting Divine Aura.\" then \"A divine aura surrounds you.\""),
        new CooldownRuleInfo(BestowedDivineAuraLabel, AaDivineAuraSeconds,
            "\"You activate Bestowed Divine Aura.\" then \"A divine aura surrounds you.\""),
        new CooldownRuleInfo(AaDivineAuraLabel, AaDivineAuraSeconds,
            "\"A divine aura surrounds you.\" with no announcement (used while Bestowed Divine Aura is on cooldown)"),
    };

    private sealed class CharacterState
    {
        public DateTime? SpellCastUtc;
        public DateTime? BestowedUtc;
        public DateTime BestowedCooldownEndsUtc;
    }

    private readonly TriggerEngineService _engine;
    private readonly object _gate = new();
    private readonly Dictionary<string, CharacterState> _states = new(StringComparer.OrdinalIgnoreCase);

    public CooldownTrackerService(TriggerEngineService engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>False ignores every line.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>The overlay the cooldown bars appear in.</summary>
    public TimerOverlayCategory Category { get; set; } = TimerOverlayCategory.RaidTriggers;

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (!IsEnabled || message.IsEmpty)
        {
            return;
        }

        // Only these three exact lines matter; compare spans so ordinary lines cost nothing.
        if (message.SequenceEqual(BeginCastingDivineAura))
        {
            lock (_gate)
            {
                GetStateLocked(characterName).SpellCastUtc = receivedUtc;
            }
        }
        else if (message.SequenceEqual(ActivateBestowedDivineAura))
        {
            lock (_gate)
            {
                GetStateLocked(characterName).BestowedUtc = receivedUtc;
            }
        }
        else if (message.SequenceEqual(DivineAuraLanded))
        {
            HandleLanding(characterName, receivedUtc);
        }
    }

    private void HandleLanding(string characterName, DateTime now)
    {
        string label;
        int seconds;

        lock (_gate)
        {
            CharacterState state = GetStateLocked(characterName);

            bool spellRecent = IsRecent(state.SpellCastUtc, now);
            bool bestowedRecent = IsRecent(state.BestowedUtc, now);

            // A recent announcement is used up by the landing it caused.
            if (spellRecent && (!bestowedRecent || state.SpellCastUtc >= state.BestowedUtc))
            {
                label = SpellDivineAuraLabel;
                seconds = SpellDivineAuraSeconds;
            }
            else if (bestowedRecent)
            {
                label = BestowedDivineAuraLabel;
                seconds = AaDivineAuraSeconds;
                state.BestowedCooldownEndsUtc = now.AddSeconds(seconds);
            }
            else if (state.BestowedCooldownEndsUtc > now)
            {
                label = AaDivineAuraLabel;
                seconds = AaDivineAuraSeconds;
            }
            else
            {
                label = BestowedDivineAuraLabel;
                seconds = AaDivineAuraSeconds;
                state.BestowedCooldownEndsUtc = now.AddSeconds(seconds);
            }

            state.SpellCastUtc = null;
            state.BestowedUtc = null;
        }

        _engine.StartTimer(TimerSourceId, TimerSourceName, characterName, label, seconds, now, Category);
    }

    private static bool IsRecent(DateTime? announcedUtc, DateTime now) =>
        announcedUtc is { } at && now >= at && now - at <= AnnouncementWindow;

    private CharacterState GetStateLocked(string characterName)
    {
        if (!_states.TryGetValue(characterName, out CharacterState? state))
        {
            state = new CharacterState();
            _states[characterName] = state;
        }

        return state;
    }
}
