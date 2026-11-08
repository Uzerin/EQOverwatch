namespace EQOverwatch.Core.Models;

public enum CombatEventKind : byte
{
    Damage,
    Miss,
    Death,

    /// <summary>
    /// The announcement line that precedes a critical damage line ("X scores a critical hit! (123)"). <c>Source</c> is the
    /// attacker, <c>Amount</c> the announced damage, <c>DamageKind</c> Melee for a hit and NonMelee for a blast.
    /// </summary>
    CriticalHit,
}

public enum DamageKind : byte
{
    None,
    Melee,
    NonMelee,
    DamageOverTime,

    /// <summary>A damage shield: "a frost giant is pierced by Fullfrontal's thorns for 34 points of non-melee damage." (also burned by flames, tormented by frost).</summary>
    DamageShield,
}

/// <summary>How an attack on a defender failed, read from the end of a miss line ("..., but Ugli dodges!").</summary>
public enum AvoidKind : byte
{
    /// <summary>Not a miss line (or a miss line whose reason was not read).</summary>
    None,

    /// <summary>A plain miss ("..., but misses!").</summary>
    Miss,
    Dodge,
    Parry,
    Block,

    /// <summary>The defender riposted ("..., but YOU riposte!").</summary>
    Riposte,

    /// <summary>Anything else: a rune or ward absorbing the blow, the defender being invulnerable, a spell being resisted.</summary>
    Other,
}

/// <summary>
/// A parsed combat line. Names are already normalized: "You"/"YOU"/"Your" are replaced by the owning character's name.
/// </summary>
/// <param name="Timestamp">Log-clock time (parsed from the line, never wall-clock).</param>
/// <param name="Kind">Damage, Miss or Death.</param>
/// <param name="Source">Attacker/caster. For <see cref="CombatEventKind.Death"/> this is the killer (may be empty if unknown).</param>
/// <param name="Target">Victim.</param>
/// <param name="Amount">Points of damage; 0 for misses and deaths.</param>
/// <param name="DamageKind">Melee / non-melee / DoT for damage events.</param>
/// <param name="Detail">Melee verb, spell name, or miss verb when available.</param>
/// <param name="Avoid">For a miss, how the defender avoided it (dodge, parry, block, riposte, plain miss or other).</param>
/// <param name="IsCritical">The damage line itself was tagged critical, e.g. <c>... points of damage. (Critical)</c> or <c>(Critical Double Bow Shot)</c>.</param>
public readonly record struct CombatEvent(
    DateTime Timestamp,
    CombatEventKind Kind,
    string Source,
    string Target,
    int Amount,
    DamageKind DamageKind,
    string? Detail,
    bool IsCritical = false,
    AvoidKind Avoid = AvoidKind.None);
