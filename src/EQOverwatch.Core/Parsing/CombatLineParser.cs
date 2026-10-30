using System.Globalization;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.Parsing;

/// <summary>
/// Turns a raw log line into a <see cref="CombatEvent"/>.
/// <para>
/// Cost model: every line first goes through cheap ordinal span checks (<c>IndexOf</c> on the message slice).
/// Only lines that look like combat reach a source-generated regex, so the vast majority of chat/spell/loot lines
/// never allocate. The regex runs over the original line string with an offset window (<c>Match(line, 27, len)</c>),
/// so no substring is created for the message either; only matched group values are materialized.
/// </para>
/// </summary>
public static partial class CombatLineParser
{
    /// <param name="line">The full line including the timestamp wrapper.</param>
    /// <param name="timestamp">Already-parsed log timestamp for this line.</param>
    /// <param name="selfName">Character that owns the log; "You"/"YOU"/"Your" are normalized to this name.</param>
    public static bool TryParse(string line, DateTime timestamp, string selfName, out CombatEvent combatEvent)
    {
        combatEvent = default;

        const int start = LogLineFormat.MessageOffset;
        if (line.Length <= start)
        {
            return false;
        }

        ReadOnlySpan<char> message = line.AsSpan(start);
        int length = line.Length - start;
        Match match;

        if (Contains(message, " damage"))
        {
            // "a frost giant was hit by non-melee for 450 points of damage."  (your own spell damage)
            if (Contains(message, " was hit by non-melee for "))
            {
                match = OwnSpellDamageRegex().Match(line, start, length);
                if (!match.Success || !TryAmount(match, out int spellAmount))
                {
                    return false;
                }

                combatEvent = new CombatEvent(timestamp, CombatEventKind.Damage, selfName,
                    match.Groups["target"].Value, spellAmount, DamageKind.NonMelee, null, IsCriticalTag(match));
                return true;
            }

            // DoTs: "X has taken 100 damage from your Spell." / "... from Spell by Caster."
            if (Contains(message, " has taken ") && Contains(message, " damage from "))
            {
                match = OwnDotRegex().Match(line, start, length);
                if (match.Success && TryAmount(match, out int ownDot))
                {
                    combatEvent = new CombatEvent(timestamp, CombatEventKind.Damage, selfName,
                        match.Groups["target"].Value, ownDot, DamageKind.DamageOverTime, match.Groups["spell"].Value, IsCriticalTag(match));
                    return true;
                }

                match = OtherDotRegex().Match(line, start, length);
                if (match.Success && TryAmount(match, out int otherDot))
                {
                    combatEvent = new CombatEvent(timestamp, CombatEventKind.Damage,
                        Resolve(match.Groups["source"].Value, selfName),
                        match.Groups["target"].Value, otherDot, DamageKind.DamageOverTime, match.Groups["spell"].Value, IsCriticalTag(match));
                    return true;
                }

                return false;
            }

            // Damage shield: "a frost giant is pierced by Fullfrontal's thorns for 34 points of non-melee damage."
            if (Contains(message, " is pierced by ") && Contains(message, " thorns for "))
            {
                match = ThornsRegex().Match(line, start, length);
                if (!match.Success || !TryAmount(match, out int thornsAmount))
                {
                    return false;
                }

                string owner = match.Groups["owner"].Success ? match.Groups["owner"].Value : selfName;
                combatEvent = new CombatEvent(timestamp, CombatEventKind.Damage, owner,
                    Resolve(match.Groups["target"].Value, selfName), thornsAmount, DamageKind.NonMelee, "thorns");
                return true;
            }

            // A spell landing: "a Praetorian Medicus hit you for 497 points of magic damage by Judgment."
            if (Contains(message, " damage by ") && Contains(message, " points of "))
            {
                match = SpellHitRegex().Match(line, start, length);
                if (match.Success && TryAmount(match, out int spellHit))
                {
                    combatEvent = new CombatEvent(timestamp, CombatEventKind.Damage,
                        Resolve(match.Groups["source"].Value, selfName),
                        Resolve(match.Groups["target"].Value, selfName),
                        spellHit, DamageKind.NonMelee, match.Groups["spell"].Value, IsCriticalTag(match));
                    return true;
                }
            }

            // Melee and "non-melee" direct hits.
            if (Contains(message, " points of "))
            {
                match = DirectDamageRegex().Match(line, start, length);
                if (!match.Success || !TryAmount(match, out int amount))
                {
                    return false;
                }

                combatEvent = new CombatEvent(timestamp, CombatEventKind.Damage,
                    Resolve(match.Groups["source"].Value, selfName),
                    Resolve(match.Groups["target"].Value, selfName),
                    amount,
                    match.Groups["nonmelee"].Success ? DamageKind.NonMelee : DamageKind.Melee,
                    match.Groups["verb"].Value,
                    IsCriticalTag(match));
                return true;
            }

            return false;
        }

        // "Ream scores a critical hit! (1234)" / "You deliver a critical blast! (900)": announced on the line BEFORE the damage line.
        if (message[^1] == ')' && message.IndexOf(" critical ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            bool blast = message.IndexOf("critical blast!", StringComparison.OrdinalIgnoreCase) >= 0;
            match = (blast ? CritBlastRegex() : CritHitRegex()).Match(line, start, length);
            if (!match.Success || !TryAmount(match, "damage", out int critDamage))
            {
                return false;
            }

            combatEvent = new CombatEvent(timestamp, CombatEventKind.CriticalHit,
                Resolve(match.Groups["attacker"].Value, selfName), string.Empty,
                critDamage, blast ? DamageKind.NonMelee : DamageKind.Melee, null);
            return true;
        }

        if (Contains(message, ", but ") && (Contains(message, " try to ") || Contains(message, " tries to ")))
        {
            // Swings at a boss that cannot be hurt yet are not misses: "Regnier tries to slash Seru, but Seru is INVULNERABLE!"
            if (Contains(message, " is INVULNERABLE!"))
            {
                return false;
            }

            match = MissRegex().Match(line, start, length);
            if (!match.Success)
            {
                return false;
            }

            combatEvent = new CombatEvent(timestamp, CombatEventKind.Miss,
                Resolve(match.Groups["source"].Value, selfName),
                Resolve(match.Groups["target"].Value, selfName),
                0, DamageKind.None, match.Groups["verb"].Value);
            return true;
        }

        if (Contains(message, "slain"))
        {
            match = SlainByRegex().Match(line, start, length);
            if (match.Success)
            {
                combatEvent = new CombatEvent(timestamp, CombatEventKind.Death,
                    Resolve(match.Groups["killer"].Value, selfName),
                    Resolve(match.Groups["target"].Value, selfName),
                    0, DamageKind.None, null);
                return true;
            }

            match = YouSlainRegex().Match(line, start, length);
            if (match.Success)
            {
                combatEvent = new CombatEvent(timestamp, CombatEventKind.Death,
                    selfName, match.Groups["target"].Value, 0, DamageKind.None, null);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the damage line ends in a tag naming a critical hit: "(Critical)", "(Lucky Critical)", "(Critical Double Bow Shot)".
    /// Other tags such as "(Crippling Blow)", "(Flurry)" or "(Riposte)" are not crits.
    /// </summary>
    private static bool IsCriticalTag(Match match) =>
        match.Groups["tag"] is { Success: true } tag && tag.ValueSpan.IndexOf("Critical", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool Contains(ReadOnlySpan<char> span, string value) =>
        span.IndexOf(value, StringComparison.Ordinal) >= 0;

    private static bool TryAmount(Match match, out int amount) => TryAmount(match, "amount", out amount);

    private static bool TryAmount(Match match, string group, out int amount) =>
        int.TryParse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out amount);

    /// <summary>Normalizes "You"/"YOU"/"Your" to the log owner's name.</summary>
    private static string Resolve(string name, string selfName) =>
        name.Equals("You", StringComparison.OrdinalIgnoreCase) || name.Equals("Your", StringComparison.OrdinalIgnoreCase)
            ? selfName
            : name;

    // "You slash a frost giant for 142 points of damage."  /  "Oodles hit a frost giant for 450 points of non-melee damage."
    // The verb list is explicit on purpose: NPC names contain spaces, so "<attacker> <verb> <target>" is only
    // unambiguous when the verb set is known. Trailing client tags such as " (Critical)" are tolerated.
    [GeneratedRegex(
        @"^(?<source>.+?) (?<verb>hit|hits|slash|slashes|crush|crushes|pierce|pierces|bash|bashes|kick|kicks|punch|punches|backstab|backstabs|strike|strikes|claw|claws|bite|bites|maul|mauls|gore|gores|slice|slices|rend|rends|slam|slams|smash|smashes|sting|stings|shoot|shoots|frenzy on|frenzies on) (?<target>.+?) for (?<amount>\d+) points? of (?<nonmelee>non-melee )?damage\.(?: \((?<tag>.+)\))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DirectDamageRegex();

    // "a Praetorian Medicus hit you for 497 points of magic damage by Judgment."
    [GeneratedRegex(
        @"^(?<source>.+?) hit (?<target>.+?) for (?<amount>\d+) points? of (?<type>[a-z]+) damage by (?<spell>.+?)\.(?: \((?<tag>.+)\))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SpellHitRegex();

    [GeneratedRegex(
        @"^(?<target>.+?) was hit by non-melee for (?<amount>\d+) points? of damage\.(?: \((?<tag>.+)\))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex OwnSpellDamageRegex();

    [GeneratedRegex(
        @"^(?<target>.+?) has taken (?<amount>\d+) damage from your (?<spell>.+?)\.(?: \((?<tag>.+)\))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex OwnDotRegex();

    [GeneratedRegex(
        @"^(?<target>.+?) has taken (?<amount>\d+) damage from (?<spell>.+?) by (?<source>.+?)\.(?: \((?<tag>.+)\))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex OtherDotRegex();

    // "a frost giant is pierced by Fullfrontal's thorns for 34 points of non-melee damage."  /  "... by your thorns for ..."
    [GeneratedRegex(
        @"^(?<target>.+?) is pierced by (?:(?<owner>.+?)'s|[Yy]our) thorns for (?<amount>\d+) points? of non-melee damage\.$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ThornsRegex();

    // "You try to slash a frost giant, but miss!"  /  "a frost giant tries to hit YOU, but YOU dodge!"
    [GeneratedRegex(
        @"^(?<source>.+?) tr(?:y|ies) to (?<verb>\w+(?: on)?) (?<target>.+?), but .+[!.]$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MissRegex();

    // "Ream scores a critical hit! (1234)"  /  "You score a critical hit! (1234)". The game client capitalizes the verb
    // ("Ream Scores a critical hit!(1234)") and may leave out the space before the number, so both are accepted, as is "Lucky".
    [GeneratedRegex(
        @"^(?<attacker>.+?) scores? a (?:lucky )?critical hit!\s*\((?<damage>\d+)\)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CritHitRegex();

    // "Ream delivers a critical blast! (1234)"  /  "You deliver a critical blast! (1234)"  (same tolerance as above)
    [GeneratedRegex(
        @"^(?<attacker>.+?) delivers? a (?:lucky )?critical blast!\s*\((?<damage>\d+)\)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CritBlastRegex();

    // "a frost giant has been slain by Stackprime!"  /  "You have been slain by a frost giant!"
    [GeneratedRegex(
        @"^(?<target>.+?) (?:has|have) been slain by (?<killer>.+?)!$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SlainByRegex();

    // "You have slain a frost giant!"
    [GeneratedRegex(
        @"^You have slain (?<target>.+?)!$",
        RegexOptions.CultureInvariant)]
    private static partial Regex YouSlainRegex();
}
