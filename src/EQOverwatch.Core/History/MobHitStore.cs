using System.Text.Json;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.History;

/// <summary>
/// The biggest single hit each mob has landed on one of your characters, from the live logs, kept between runs. The mob card shows it
/// ("Hits you for up to 1,250"). Each line counts only in its own character's log.
/// </summary>
public sealed class MobHitStore : IDpsEventSink
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _max = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, bool> _isOwn;
    private string _owner = string.Empty;
    private string? _path;
    private bool _dirty;

    public MobHitStore(Func<string, bool> isOwn) => _isOwn = isOwn ?? throw new ArgumentNullException(nameof(isOwn));

    public void SetStoragePath(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) is { } saved)
            {
                lock (_gate)
                {
                    foreach ((string mob, int hit) in saved)
                    {
                        _max[mob] = Math.Max(hit, _max.GetValueOrDefault(mob));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file starts over.
        }
    }

    /// <summary>The biggest hit the mob has landed on your characters, or 0 when none was seen.</summary>
    public int MaxHit(string mob)
    {
        lock (_gate)
        {
            return _max.GetValueOrDefault(mob?.Trim() ?? string.Empty);
        }
    }

    /// <summary>Notes one hit (also used by tests).</summary>
    public void Record(string mob, int amount)
    {
        if (string.IsNullOrWhiteSpace(mob) || amount <= 0)
        {
            return;
        }

        lock (_gate)
        {
            string key = mob.Trim();
            if (amount > _max.GetValueOrDefault(key))
            {
                _max[key] = amount;
                _dirty = true;
            }
        }
    }

    public void Flush()
    {
        string? path = _path;
        string json;
        lock (_gate)
        {
            if (!_dirty || path is null)
            {
                return;
            }

            json = JsonSerializer.Serialize(_max);
            _dirty = false;
        }

        try
        {
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    public void BeginBatch()
    {
    }

    public void EndBatch()
    {
    }

    public void OnLogOwner(string characterName) => _owner = characterName;

    public void OnLogClock(DateTime logTimestamp)
    {
    }

    public void OnNonCombatMessage(ReadOnlySpan<char> message)
    {
    }

    public void OnCombatEvent(in CombatEvent e)
    {
        if (e.Kind == CombatEventKind.Damage && e.Amount > 0 && _owner.Length > 0
            && e.Target.Equals(_owner, StringComparison.OrdinalIgnoreCase) && !_isOwn(e.Source) && _isOwn(_owner)
            && e.DamageKind != DamageKind.DamageShield)
        {
            Record(e.Source, e.Amount);
        }
    }
}
