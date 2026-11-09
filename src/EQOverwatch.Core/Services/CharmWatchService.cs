using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Watches your characters' own charms. "You begin casting X." (or singing) for a charm spell, followed by X's landing text on a mob
/// ("a rock golem has been charmed."), starts a countdown bar "Character: X, mob" on the Debuffs overlay for the spell's longest
/// duration. "Your X spell has worn off of mob." (or "Your charm spell has worn off.") removes the bar, shows a red "CHARM BROKE"
/// banner for <see cref="BrokeBannerSeconds"/> seconds and says "Charm broke". Only the logs of your characters report these lines, so
/// only your own charms are watched.
/// </summary>
public sealed class CharmWatchService : ITriggerLineSink
{
    /// <summary>Source id the bars and banners are registered under.</summary>
    public static readonly Guid TimerSourceId = new("3c0f6d2e-8a51-4b9e-b7d4-2e61a9c05f37");

    public const string TimerSourceName = "Charm watch";

    /// <summary>The effect number the spell file uses for charm.</summary>
    public const int CharmEffect = 22;

    public const double BrokeBannerSeconds = 10;

    private const string YouBeginCasting = "You begin casting ";
    private const string YouBeginSinging = "You begin singing ";
    private const string YourPrefix = "Your ";
    private const string WornOffOf = " spell has worn off of ";
    private const string CharmWornOff = "Your charm spell has worn off.";

    private sealed record Pending(SpellInfo Spell, DateTime ExpiresUtc);

    private sealed record Charmed(SpellInfo Spell, string Mob, string Label);

    private readonly SpellDatabaseService _spells;
    private readonly TriggerEngineService _timers;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Charmed> _charmed = new(StringComparer.OrdinalIgnoreCase);

    public CharmWatchService(SpellDatabaseService spells, TriggerEngineService timers)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _timers = timers ?? throw new ArgumentNullException(nameof(timers));
    }

    /// <summary>On by default.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>True when the spell charms: its effects include charm, or (older files without effects) it lands with "has been charmed".</summary>
    public static bool IsCharm(SpellInfo spell) =>
        spell.EffectIds is { Count: > 0 } effects
            ? effects.Contains(CharmEffect)
            : spell.CastOnOtherSuffix.Equals("has been charmed.", StringComparison.Ordinal);

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (!IsEnabled || message.Length < 8)
        {
            return;
        }

        if (message.StartsWith(YouBeginCasting, StringComparison.Ordinal) || message.StartsWith(YouBeginSinging, StringComparison.Ordinal))
        {
            OnOwnCast(characterName, message, receivedUtc);
            return;
        }

        if (message.StartsWith(YourPrefix, StringComparison.Ordinal))
        {
            if (message.Equals(CharmWornOff, StringComparison.Ordinal))
            {
                Broke(characterName, mob: null, receivedUtc);
                return;
            }

            int wornOff = message.IndexOf(WornOffOf, StringComparison.Ordinal);
            if (wornOff > YourPrefix.Length && message[^1] == '.')
            {
                string spell = message[YourPrefix.Length..wornOff].ToString();
                string mob = message[(wornOff + WornOffOf.Length)..^1].ToString();
                lock (_gate)
                {
                    if (!_charmed.TryGetValue(characterName, out Charmed? current) || !current.Spell.Name.Equals(spell, StringComparison.OrdinalIgnoreCase))
                    {
                        if (_spells.Database.FindByName(spell) is not { } info || !IsCharm(info))
                        {
                            return;
                        }
                    }
                }

                Broke(characterName, mob, receivedUtc);
            }

            return;
        }

        OnPossibleLanding(characterName, message, receivedUtc);
    }

    private void OnOwnCast(string character, ReadOnlySpan<char> message, DateTime now)
    {
        if (message[^1] != '.')
        {
            return;
        }

        string name = message[YouBeginCasting.Length..^1].ToString(); // both prefixes have the same length
        lock (_gate)
        {
            if (_spells.Database.FindByName(name) is { } spell && IsCharm(spell))
            {
                _pending[character] = new Pending(spell, now.AddMilliseconds(spell.CastTimeMs) + BuffTrackerService.PendingCastGrace);
            }
            else
            {
                _pending.Remove(character);
            }
        }
    }

    private void OnPossibleLanding(string character, ReadOnlySpan<char> message, DateTime now)
    {
        Charmed charmed;
        Charmed? previous;
        lock (_gate)
        {
            if (!_pending.TryGetValue(character, out Pending? pending))
            {
                return;
            }

            if (pending.ExpiresUtc < now)
            {
                _pending.Remove(character);
                return;
            }

            string suffix = pending.Spell.CastOnOtherSuffix;
            if (suffix.Length == 0 || message.Length <= suffix.Length || !message.EndsWith(suffix, StringComparison.Ordinal))
            {
                return;
            }

            string mob = message[..^suffix.Length].TrimEnd().ToString();
            if (mob.Length == 0 || mob.Equals(character, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _pending.Remove(character);
            _charmed.TryGetValue(character, out previous);
            charmed = new Charmed(pending.Spell, mob, $"{character}: {pending.Spell.Name}, {mob}");
            _charmed[character] = charmed;
        }

        if (previous is not null)
        {
            _timers.CancelTimer(TimerSourceId, character, previous.Label);
        }

        _timers.StartTimer(TimerSourceId, TimerSourceName, character, charmed.Label, charmed.Spell.BaseDurationSeconds, now, TimerOverlayCategory.Debuffs);
    }

    private void Broke(string character, string? mob, DateTime now)
    {
        Charmed? current;
        lock (_gate)
        {
            _charmed.Remove(character, out current);
        }

        if (current is not null)
        {
            _timers.CancelTimer(TimerSourceId, character, current.Label);
            mob ??= current.Mob;
        }

        string banner = string.IsNullOrEmpty(mob) ? $"CHARM BROKE: {character}" : $"CHARM BROKE: {character}, {mob}";
        _timers.StartAlert(TimerSourceId, TimerSourceName, character, banner, BrokeBannerSeconds, now, TimerOverlayCategory.Debuffs);
        _timers.PublishAudio(TimerSourceId, TimerSourceName, character, "Charm broke", now);
    }
}
