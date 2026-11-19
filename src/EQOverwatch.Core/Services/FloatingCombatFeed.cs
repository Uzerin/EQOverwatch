using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Services;

/// <summary>Which floating combat text overlay a number goes to.</summary>
public enum FloatingPanel : byte
{
    Dealt,
    Healed,
    Taken,
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

/// <summary>One number for a floating combat text overlay: whose it is, how much, and whether it was a critical.</summary>
public readonly record struct FloatingNumber(FloatingPanel Panel, string Character, int Amount, FloatingStyle Style, bool IsCritical);

/// <summary>
/// Picks the numbers for the floating combat text overlays out of the live combat lines: damage your characters (and, with pet merging,
/// their pets) deal, heals they receive, and damage they take. Each line counts only in its own character's log, so a hit seen in two
/// logs shows once. The overlays take the numbers with <see cref="TakeAll"/>.
/// </summary>
public sealed partial class FloatingCombatFeed : IDpsEventSink
{
    private const int MaxQueued = 200;

    [GeneratedRegex(@"^(?<healer>.+?) healed (?<target>.+?) (?:over time )?for (?<actual>\d+)(?: \(\d+\))? hit points? by .+?\.(?: \((?<tag>.+)\))?$", RegexOptions.CultureInvariant)]
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
            Add(new FloatingNumber(FloatingPanel.Dealt, dealer, e.Amount, StyleOf(e), crit));
            return;
        }

        if (e.Target.Equals(_owner, StringComparison.OrdinalIgnoreCase) && _isOwn(_owner))
        {
            Add(new FloatingNumber(FloatingPanel.Taken, _owner, e.Amount, FloatingStyle.Taken, crit));
        }
    }

    public void OnNonCombatMessage(ReadOnlySpan<char> message)
    {
        if (!Enabled || _owner.Length == 0 || message.IndexOf(" healed ", StringComparison.Ordinal) < 0)
        {
            return;
        }

        Match match = HealRegex().Match(message.ToString());
        if (!match.Success || !int.TryParse(match.Groups["actual"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int amount) || amount <= 0)
        {
            return;
        }

        string target = match.Groups["target"].Value;
        bool toMe = target.Equals("you", StringComparison.OrdinalIgnoreCase) || target.Equals("yourself", StringComparison.OrdinalIgnoreCase)
                    || target.Equals(_owner, StringComparison.OrdinalIgnoreCase);
        if (toMe && _isOwn(_owner))
        {
            bool crit = match.Groups["tag"] is { Success: true } tag && tag.ValueSpan.IndexOf("Critical", StringComparison.OrdinalIgnoreCase) >= 0;
            Add(new FloatingNumber(FloatingPanel.Healed, _owner, amount, FloatingStyle.Heal, crit));
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
