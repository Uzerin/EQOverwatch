using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.Core.Services;

/// <summary>
/// Starts a recast countdown when a raid boss ability shows up in a watched log. A rule fires when its
/// <see cref="RaidBossTimerRule.MatchText"/> is in the line AND the rule applies to the fight:
/// the rule has no <c>MobName</c>, or the line contains the mob's name, or the live DPS tracker has an active encounter whose
/// target is that mob (case-insensitive). The bar is labelled "Mob: Spell", lasts <c>RecastSeconds</c>, shows in the rule's
/// overlay category and is reset each time the ability is seen again.
/// <para>
/// Warning speech (<c>WarningTtsText</c>) is published through the trigger engine's audio event when the timer starts
/// (<see cref="RaidBossTimerRule.StartTtsText"/>) and, separately, when 10 seconds remain. The 10-second warnings are due-times checked by
/// <see cref="Tick"/>, which an internal timer calls about four times a second.
/// </para>
/// Two watched characters seeing the same boss line within <see cref="DuplicateWindow"/> count as one cast.
/// </summary>
public sealed class RaidBossTimerService : ITriggerLineSink, IDisposable
{
    public const string TimerSourceName = "Raid boss timers";

    /// <summary>The same rule firing again this soon is the same cast seen by another log.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private sealed record PendingWarning(DateTime DueUtc, string Text, string Subject);

    private readonly TriggerEngineService _timers;
    private readonly Func<DpsSnapshot> _liveDps;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTime> _lastFired = new();
    private readonly Dictionary<Guid, PendingWarning> _warnings = new();
    private readonly Timer? _tickTimer;
    private RaidBossTimerRule[] _rules = Array.Empty<RaidBossTimerRule>();

    /// <param name="liveDps">Reads the live DPS tracker's snapshot (to scope rules to the current target).</param>
    /// <param name="autoTick">False in tests, which call <see cref="Tick"/> themselves.</param>
    public RaidBossTimerService(TriggerEngineService timers, Func<DpsSnapshot> liveDps, bool autoTick = true)
    {
        _timers = timers ?? throw new ArgumentNullException(nameof(timers));
        _liveDps = liveDps ?? throw new ArgumentNullException(nameof(liveDps));

        if (autoTick)
        {
            _tickTimer = new Timer(_ => Tick(DateTime.UtcNow), null, TickInterval, TickInterval);
        }
    }

    public IReadOnlyList<RaidBossTimerRule> Rules => Volatile.Read(ref _rules);

    /// <summary>
    /// Replaces the rule set. Disabled rules and rules without match text, spell name or a positive recast time are ignored.
    /// Warnings still waiting for a removed rule are dropped.
    /// </summary>
    public void SetRules(IEnumerable<RaidBossTimerRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        RaidBossTimerRule[] usable = rules
            .Where(r => r.IsEnabled
                        && !string.IsNullOrEmpty(r.MatchText)
                        && !string.IsNullOrWhiteSpace(r.SpellName)
                        && r.RecastSeconds > 0)
            .Select(r =>
            {
                RaidBossTimerRule copy = r.Clone();
                copy.ApplyLegacyTiming();
                copy.AlternateMatchText = RaidBossTimerRule.AlternateCastForm(copy.MatchText);
                return copy;
            })
            .ToArray();

        lock (_gate)
        {
            Volatile.Write(ref _rules, usable);

            var ids = new HashSet<Guid>(usable.Select(r => r.Id));
            foreach (Guid stale in _warnings.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _warnings.Remove(stale);
            }

            foreach (Guid stale in _lastFired.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                _lastFired.Remove(stale);
            }
        }
    }

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        RaidBossTimerRule[] rules = Volatile.Read(ref _rules);
        if (rules.Length == 0 || message.IsEmpty)
        {
            return;
        }

        HashSet<string>? activeTargets = null; // read the tracker lazily, only if some rule needs it

        foreach (RaidBossTimerRule rule in rules)
        {
            if (message.IndexOf(rule.MatchText, StringComparison.Ordinal) < 0
                && (rule.AlternateMatchText is null || message.IndexOf(rule.AlternateMatchText, StringComparison.Ordinal) < 0))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rule.MobName))
            {
                string mob = rule.MobName.Trim();
                bool lineNamesMob = message.IndexOf(mob, StringComparison.OrdinalIgnoreCase) >= 0;
                if (!lineNamesMob)
                {
                    activeTargets ??= GetActiveTargets();
                    if (!activeTargets.Contains(mob))
                    {
                        continue;
                    }
                }
            }

            Fire(rule, receivedUtc);
        }
    }

    /// <summary>Speaks the warnings whose time has come. Called by the internal timer; public so tests can drive it.</summary>
    public void Tick(DateTime nowUtc)
    {
        List<PendingWarning>? due = null;

        lock (_gate)
        {
            if (_warnings.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<Guid, PendingWarning> pair in _warnings.Where(p => p.Value.DueUtc <= nowUtc).ToList())
            {
                (due ??= new List<PendingWarning>()).Add(pair.Value);
                _warnings.Remove(pair.Key);
            }
        }

        if (due is null)
        {
            return;
        }

        foreach (PendingWarning warning in due.OrderBy(w => w.DueUtc))
        {
            _timers.PublishAudio(Guid.Empty, TimerSourceName, warning.Subject, warning.Text, nowUtc);
        }
    }

    public void Dispose() => _tickTimer?.Dispose();

    private void Fire(RaidBossTimerRule rule, DateTime nowUtc)
    {
        string? speakNow = null;

        lock (_gate)
        {
            if (_lastFired.TryGetValue(rule.Id, out DateTime last) && nowUtc - last < DuplicateWindow && nowUtc >= last)
            {
                return; // the same cast, seen by another watched log
            }

            _lastFired[rule.Id] = nowUtc;
            _warnings.Remove(rule.Id); // a re-cast replaces the pending warning

            string subject = string.IsNullOrWhiteSpace(rule.MobName) ? "Raid" : rule.MobName.Trim();

            if (!string.IsNullOrWhiteSpace(rule.StartTtsText))
            {
                speakNow = rule.RenderStart();
            }

            if (!string.IsNullOrWhiteSpace(rule.WarningTtsText) && rule.RecastSeconds > RaidBossTimerRule.WarningLeadSeconds)
            {
                _warnings[rule.Id] = new PendingWarning(
                    nowUtc.AddSeconds(rule.RecastSeconds - RaidBossTimerRule.WarningLeadSeconds), rule.RenderWarning(), subject);
            }
        }

        string subjectName = string.IsNullOrWhiteSpace(rule.MobName) ? "Raid" : rule.MobName.Trim();
        _timers.StartTimer(rule.Id, TimerSourceName, subjectName, rule.Label, rule.RecastSeconds, nowUtc, rule.OverlayCategory);

        if (speakNow is not null)
        {
            _timers.PublishAudio(rule.Id, TimerSourceName, subjectName, speakNow, nowUtc);
        }
    }

    private HashSet<string> GetActiveTargets() =>
        new(_liveDps().Active.Select(e => e.TargetName), StringComparer.OrdinalIgnoreCase);
}
