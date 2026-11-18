using System.Text.RegularExpressions;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Spoken alerts about your own characters' spells, read from their own logs:
/// <list type="bullet">
/// <item><description>a buff that did not take hold ("Your X spell did not take hold. (Blocked by Y.)"), at most once a minute per spell;</description></item>
/// <item><description>a spell resisted <see cref="ResistStreak"/> times in a row by the same mob;</description></item>
/// <item><description>a failed feign death ("Name has fallen to the ground." in Name's own log);</description></item>
/// <item><description>your root or mez wearing off or breaking ("Your X spell has worn off of mob.").</description></item>
/// </list>
/// Each alert shows a short banner and is spoken; each has its own switch.
/// </summary>
public sealed partial class SpellAlertService : ITriggerLineSink
{
    public static readonly Guid SourceId = new("5a8e2c1d-3f74-4b09-a6d2-9c1e7b4f0d38");

    public const string SourceName = "Spell alerts";

    /// <summary>How many resists in a row on the same mob before the warning.</summary>
    public const int ResistStreak = 3;

    /// <summary>A spell's "did not take hold" alert is not repeated sooner than this.</summary>
    public static readonly TimeSpan TakeHoldQuiet = TimeSpan.FromSeconds(60);

    public const int RootEffect = 99;
    public const int MezEffect = 31;

    private const double BannerSeconds = 6;
    private const string YouBeginCasting = "You begin casting ";
    private const string FallenToTheGround = " has fallen to the ground.";

    [GeneratedRegex(@"^Your (?<spell>.+?) spell did not take hold(?: on (?<target>.+?))?\. \(Blocked by (?<by>.+?)\.\)$", RegexOptions.CultureInvariant)]
    private static partial Regex TakeHoldRegex();

    [GeneratedRegex(@"^(?<mob>.+?) resisted your (?<spell>.+?)!$", RegexOptions.CultureInvariant)]
    private static partial Regex ResistRegex();

    [GeneratedRegex(@"^Your (?<spell>.+?) spell has worn off of (?<mob>.+?)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex WornOffRegex();

    private readonly SpellDatabaseService _spells;
    private readonly TriggerEngineService _alerts;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastTakeHold = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _streaks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SpellInfo Spell, DateTime Until)> _pending = new(StringComparer.OrdinalIgnoreCase);

    public SpellAlertService(SpellDatabaseService spells, TriggerEngineService alerts)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
    }

    public bool TakeHoldAlert { get; set; } = true;

    public bool ResistAlert { get; set; } = true;

    public bool FeignDeathAlert { get; set; } = true;

    public bool RootMezAlert { get; set; } = true;

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.Length < 12 || string.IsNullOrEmpty(characterName))
        {
            return;
        }

        if (message.StartsWith(YouBeginCasting, StringComparison.Ordinal))
        {
            OnCast(characterName, message, receivedUtc);
            return;
        }

        if (message.EndsWith(FallenToTheGround, StringComparison.Ordinal))
        {
            if (FeignDeathAlert && message[..^FallenToTheGround.Length].Equals(characterName, StringComparison.OrdinalIgnoreCase))
            {
                Alert(characterName, $"{characterName} feign death failed", receivedUtc);
            }

            return;
        }

        char last = message[^1];
        if (last == '!')
        {
            OnPossibleResist(characterName, message.ToString(), receivedUtc);
            return;
        }

        if (last == ')' && message.StartsWith("Your ", StringComparison.Ordinal))
        {
            OnPossibleTakeHold(characterName, message.ToString(), receivedUtc);
            return;
        }

        if (last == '.' && message.StartsWith("Your ", StringComparison.Ordinal) && message.Contains(" has worn off of ", StringComparison.Ordinal))
        {
            OnWornOff(characterName, message.ToString(), receivedUtc);
            return;
        }

        if (last == '.')
        {
            OnPossibleLanding(characterName, message, receivedUtc);
        }
    }

    private void OnCast(string character, ReadOnlySpan<char> message, DateTime now)
    {
        if (message[^1] != '.')
        {
            return;
        }

        string name = message[YouBeginCasting.Length..^1].ToString();
        lock (_gate)
        {
            if (_spells.Database.FindByName(name) is { } spell && spell.CastOnOtherSuffix.Length > 0)
            {
                _pending[character] = (spell, now.AddMilliseconds(spell.CastTimeMs) + BuffTrackerService.PendingCastGrace);
            }
            else
            {
                _pending.Remove(character);
            }
        }
    }

    /// <summary>The spell being cast landed on a mob: its resist streak on that mob starts again.</summary>
    private void OnPossibleLanding(string character, ReadOnlySpan<char> message, DateTime now)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(character, out (SpellInfo Spell, DateTime Until) pending) || pending.Until < now)
            {
                return;
            }

            string suffix = pending.Spell.CastOnOtherSuffix;
            if (message.Length <= suffix.Length || !message.EndsWith(suffix, StringComparison.Ordinal))
            {
                return;
            }

            string mob = message[..^suffix.Length].Trim().ToString();
            _pending.Remove(character);
            _streaks.Remove(StreakKey(character, pending.Spell.Name, mob));
        }
    }

    private void OnPossibleResist(string character, string message, DateTime now)
    {
        Match match = ResistRegex().Match(message);
        if (!match.Success || !ResistAlert)
        {
            return;
        }

        string mob = match.Groups["mob"].Value.Trim();
        string spell = match.Groups["spell"].Value;
        lock (_gate)
        {
            _pending.Remove(character);
            string key = StreakKey(character, spell, mob);
            int count = _streaks.GetValueOrDefault(key) + 1;
            if (count < ResistStreak)
            {
                _streaks[key] = count;
                return;
            }

            _streaks.Remove(key);
        }

        Alert(character, $"{spell} resisted {ResistStreak} times", now);
    }

    private void OnPossibleTakeHold(string character, string message, DateTime now)
    {
        Match match = TakeHoldRegex().Match(message);
        if (!match.Success || !TakeHoldAlert)
        {
            return;
        }

        string spell = match.Groups["spell"].Value;
        lock (_gate)
        {
            string key = character + "|" + spell;
            if (_lastTakeHold.TryGetValue(key, out DateTime last) && now >= last && now - last < TakeHoldQuiet)
            {
                return;
            }

            _lastTakeHold[key] = now;
        }

        Alert(character, $"{spell} blocked by {match.Groups["by"].Value}", now);
    }

    private void OnWornOff(string character, string message, DateTime now)
    {
        if (!RootMezAlert || WornOffRegex().Match(message) is not { Success: true } match)
        {
            return;
        }

        if (_spells.Database.FindByName(match.Groups["spell"].Value) is not { EffectIds: { Count: > 0 } effects })
        {
            return;
        }

        string? kind = effects.Contains(MezEffect) ? "Mez" : effects.Contains(RootEffect) ? "Root" : null;
        if (kind is not null)
        {
            Alert(character, $"{kind} broke on {match.Groups["mob"].Value}", now);
        }
    }

    private void Alert(string character, string text, DateTime now)
    {
        _alerts.StartAlert(SourceId, SourceName, character, text, BannerSeconds, now, TimerOverlayCategory.Debuffs);
        _alerts.PublishAudio(SourceId, SourceName, character, text, now);
    }

    private static string StreakKey(string character, string spell, string mob) => character + "|" + spell + "|" + mob;
}
