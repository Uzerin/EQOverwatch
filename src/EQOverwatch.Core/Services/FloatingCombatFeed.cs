using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Which lane a number goes to: the Damage overlay shows Dealt (left) and Taken (right), the Heals overlay HealDone (left) and Healed
/// (right).
/// </summary>
public enum FloatingPanel : byte
{
    Dealt,
    Healed,
    Taken,
    HealDone,
}

/// <summary>What kind of number it is, which picks its color.</summary>
public enum FloatingStyle : byte
{
    Melee,
    Ranged,
    Spell,
    DamageShield,
    Heal,
    Taken,
}

/// <summary>
/// One number for a floating combat text overlay: whose it is, how much, and whether it was a critical. A miss or resist has no amount
/// and a <paramref name="Word"/> instead ("Dodge", "Resisted"). <paramref name="Target"/> is who was hit or healed (for merging hits).
/// </summary>
public readonly record struct FloatingNumber(
    FloatingPanel Panel, string Character, int Amount, FloatingStyle Style, bool IsCritical, string Target = "", string? Word = null)
{
    /// <summary>True for the Heals overlay, false for the Damage overlay.</summary>
    public bool IsHeal => Panel is FloatingPanel.Healed or FloatingPanel.HealDone;

    /// <summary>True for the right lane (what your character takes or receives).</summary>
    public bool IsIncoming => Panel is FloatingPanel.Taken or FloatingPanel.Healed;
}

/// <summary>
/// Picks the numbers for the floating combat text overlays out of the live combat lines: damage your characters (and, with pet merging,
/// their pets) deal, heals they receive, and damage they take. Each line counts only in its own character's log, so a hit seen in two
/// logs shows once. The overlays take the numbers with <see cref="TakeAll"/>.
/// </summary>
public sealed partial class FloatingCombatFeed : IDpsEventSink
{
    private const int MaxQueued = 200;

    /// <summary>A heal from a reverse damage shield: every such spell is named "Mark of ..." (Naltron's Mark is a buff, not one).</summary>
    public static bool IsReverseDamageShield(ReadOnlySpan<char> spell) => spell.StartsWith("Mark of ", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(?<healer>.+?) healed (?<target>.+?) (?:over time )?for (?<actual>\d+)(?: \(\d+\))? hit points? by (?<spell>.+?)\.(?: \((?<tag>.+)\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex HealRegex();

    private readonly ConcurrentQueue<FloatingNumber> _queue = new();
    private readonly Func<string, bool> _isOwn;
    private readonly Func<string, string?> _petOwner;
    private string _owner = string.Empty;
    private (string Source, int Amount, DateTime Time) _lastCrit;

    /// <param name="isOwn">True for your own characters.</param>
    /// <param name="petOwner">The owner a pet's damage is credited to (when pet merging is on), or null.</param>
    public FloatingCombatFeed(Func<string, bool> isOwn, Func<string, string?>? petOwner = null)
    {
        _isOwn = isOwn ?? throw new ArgumentNullException(nameof(isOwn));
        _petOwner = petOwner ?? (_ => null);
    }

    /// <summary>False stops collecting (all three overlays off).</summary>
    public bool Enabled { get; set; } = true;

    public void BeginBatch()
    {
    }

    public void EndBatch()
    {
    }

    public void OnLogOwner(string characterName) => _owner = characterName;

    public void OnCombatEvent(in CombatEvent e)
    {
        if (!Enabled || _owner.Length == 0)
        {
            return;
        }

        if (e.Kind == CombatEventKind.CriticalHit)
        {
            _lastCrit = (e.Source, e.Amount, e.Timestamp);
            return;
        }

        if (e.Kind == CombatEventKind.Miss)
        {
            OnMiss(e);
            return;
        }

        if (e.Kind != CombatEventKind.Damage || e.Amount <= 0)
        {
            return;
        }

        bool crit = e.IsCritical
                    || (_lastCrit.Amount == e.Amount && _lastCrit.Source.Equals(e.Source, StringComparison.OrdinalIgnoreCase)
                        && (e.Timestamp - _lastCrit.Time).Duration() <= TimeSpan.FromSeconds(1));

        string dealer = e.Source.Equals(_owner, StringComparison.OrdinalIgnoreCase) ? _owner
            : _petOwner(e.Source) is { } owner && owner.Equals(_owner, StringComparison.OrdinalIgnoreCase) ? _owner
            : string.Empty;
        if (dealer.Length > 0 && !e.Target.Equals(_owner, StringComparison.OrdinalIgnoreCase) && _isOwn(_owner))
        {
            Add(new FloatingNumber(FloatingPanel.Dealt, dealer, e.Amount, StyleOf(e), crit, e.Target));
            return;
        }

        if (e.Target.Equals(_owner, StringComparison.OrdinalIgnoreCase) && _isOwn(_owner))
        {
            Add(new FloatingNumber(FloatingPanel.Taken, _owner, e.Amount, FloatingStyle.Taken, crit, e.Source));
        }
    }

    /// <summary>When on, misses and resists show as small words ("Dodge", "Resisted").</summary>
    public bool ShowMisses { get; set; } = true;

    private void OnMiss(in CombatEvent e)
    {
        if (!ShowMisses || !_isOwn(_owner))
        {
            return;
        }

        string word = e.Avoid switch
        {
            AvoidKind.Dodge => "Dodge",
            AvoidKind.Parry => "Parry",
            AvoidKind.Block => "Block",
            AvoidKind.Riposte => "Riposte",
            AvoidKind.Other => "Absorb",
            _ => "Miss",
        };
        bool mine = e.Source.Equals(_owner, StringComparison.OrdinalIgnoreCase)
                    || (_petOwner(e.Source) is { } owner && owner.Equals(_owner, StringComparison.OrdinalIgnoreCase));
        if (mine && !e.Target.Equals(_owner, StringComparison.OrdinalIgnoreCase))
        {
            Add(new FloatingNumber(FloatingPanel.Dealt, _owner, 0, FloatingStyle.Melee, false, e.Target, word));
        }
        else if (e.Target.Equals(_owner, StringComparison.OrdinalIgnoreCase))
        {
            Add(new FloatingNumber(FloatingPanel.Taken, _owner, 0, FloatingStyle.Taken, false, e.Source, word));
        }
    }

    public void OnNonCombatMessage(ReadOnlySpan<char> message)
    {
        if (!Enabled || _owner.Length == 0)
        {
            return;
        }

        if (ShowMisses && _isOwn(_owner))
        {
            // "A bandit resisted your Lull!" and "You resist the ghoul lord's Ghoul Root!"
            int resisted = message.IndexOf(" resisted your ", StringComparison.Ordinal);
            if (resisted > 0 && message.EndsWith("!"))
            {
                Add(new FloatingNumber(FloatingPanel.Dealt, _owner, 0, FloatingStyle.Spell, false, message[..resisted].Trim().ToString(), "Resisted"));
                return;
            }

            if (message.StartsWith("You resist the ", StringComparison.Ordinal) && message.EndsWith("!"))
            {
                Add(new FloatingNumber(FloatingPanel.Taken, _owner, 0, FloatingStyle.Taken, false, string.Empty, "Resisted"));
                return;
            }
        }

        if (message.IndexOf(" healed ", StringComparison.Ordinal) < 0)
        {
            return;
        }

        Match match = HealRegex().Match(message.ToString());
        if (!match.Success || !int.TryParse(match.Groups["actual"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
        {
            return;
        }

        // Reverse damage shields (Mark of Karn, Mark of Darkness, ...) heal whoever hits the marked target: they stay in the
        // parse but would flood the Heals window.
        if (IsReverseDamageShield(match.Groups["spell"].ValueSpan))
        {
            return;
        }

        string target = match.Groups["target"].Value;
        bool toMe = target.Equals("you", StringComparison.OrdinalIgnoreCase) || target.Equals("yourself", StringComparison.OrdinalIgnoreCase)
                    || target.Equals(_owner, StringComparison.OrdinalIgnoreCase);
        if (!_isOwn(_owner))
        {
            return;
        }

        bool critical = match.Groups["tag"] is { Success: true } tag && tag.ValueSpan.IndexOf("Critical", StringComparison.OrdinalIgnoreCase) >= 0;
        string healer = match.Groups["healer"].Value;
        if (toMe)
        {
            Add(new FloatingNumber(FloatingPanel.Healed, _owner, amount, FloatingStyle.Heal, critical, healer));
        }
        else if (healer.Equals("You", StringComparison.OrdinalIgnoreCase) || healer.Equals(_owner, StringComparison.OrdinalIgnoreCase))
        {
            Add(new FloatingNumber(FloatingPanel.HealDone, _owner, amount, FloatingStyle.Heal, critical, target));
        }
    }

    private static FloatingStyle StyleOf(in CombatEvent e) => e.DamageKind switch
    {
        DamageKind.DamageShield => FloatingStyle.DamageShield,
        DamageKind.NonMelee or DamageKind.DamageOverTime => FloatingStyle.Spell,
        _ => e.Detail is "shoot" or "shoots" ? FloatingStyle.Ranged : FloatingStyle.Melee,
    };

    private void Add(FloatingNumber number)
    {
        _queue.Enqueue(number);
        while (_queue.Count > MaxQueued && _queue.TryDequeue(out _))
        {
        }
    }

    /// <summary>The numbers since the last call, oldest first.</summary>
    public IReadOnlyList<FloatingNumber> TakeAll()
    {
        var list = new List<FloatingNumber>();
        while (_queue.TryDequeue(out FloatingNumber n))
        {
            list.Add(n);
        }

        return list;
    }
}

/// <summary>Sends every event to several DPS sinks in turn.</summary>
public sealed class FanOutDpsSink : IDpsEventSink
{
    private readonly IDpsEventSink[] _sinks;

    public FanOutDpsSink(params IDpsEventSink[] sinks) => _sinks = sinks;

    public void BeginBatch()
    {
        foreach (IDpsEventSink s in _sinks)
        {
            s.BeginBatch();
        }
    }

    public void EndBatch()
    {
        foreach (IDpsEventSink s in _sinks)
        {
            s.EndBatch();
        }
    }

    public void OnCombatEvent(in CombatEvent combatEvent)
    {
        foreach (IDpsEventSink s in _sinks)
        {
            s.OnCombatEvent(in combatEvent);
        }
    }

    public void OnLogClock(DateTime logTimestamp)
    {
        foreach (IDpsEventSink s in _sinks)
        {
            s.OnLogClock(logTimestamp);
        }
    }

    public void OnLogOwner(string characterName)
    {
        foreach (IDpsEventSink s in _sinks)
        {
            s.OnLogOwner(characterName);
        }
    }

    public void OnNonCombatMessage(ReadOnlySpan<char> message)
    {
        foreach (IDpsEventSink s in _sinks)
        {
            s.OnNonCombatMessage(message);
        }
    }

    public void OnLogServer(string serverName)
    {
        foreach (IDpsEventSink s in _sinks)
        {
            s.OnLogServer(serverName);
        }
    }
}
