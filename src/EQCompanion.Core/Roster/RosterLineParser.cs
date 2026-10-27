using System.Text.RegularExpressions;

namespace EQCompanion.Core.Roster;

public enum RosterObservationKind
{
    /// <summary>A /who line: a player, usually with a class.</summary>
    Player,

    /// <summary>A pet's answer to /pet leader.</summary>
    Pet,
}

/// <param name="Name">The player (for <see cref="RosterObservationKind.Player"/>) or the pet.</param>
/// <param name="Class">The class name, or empty when the line did not carry one.</param>
/// <param name="Owner">The pet's owner (pets only).</param>
public readonly record struct RosterObservation(RosterObservationKind Kind, string Name, string Class, string Owner);

/// <summary>
/// Recognises the two log lines that tell the app who is a real player and which pet belongs to whom:
/// <list type="bullet">
/// <item><description>/who: <c>[65 Archmage (Magician)] Oodles (Gnome) &lt;Guild&gt; ZONE: nro</c> or <c>[ANONYMOUS] Oodles</c>.</description></item>
/// <item><description>/pet leader: <c>Gabnik says, 'My leader is Oodles.'</c></description></item>
/// </list>
/// Cheap ordinal checks run first, so ordinary chat and combat lines never reach a regex.
/// </summary>
public static partial class RosterLineParser
{
    public static bool TryParse(ReadOnlySpan<char> message, out RosterObservation observation)
    {
        observation = default;
        if (message.Length < 12)
        {
            return false;
        }

        if (message[0] == '[')
        {
            if (message.IndexOf("] ", StringComparison.Ordinal) < 0
                || !(char.IsDigit(message[1]) || message[1..].StartsWith("ANON", StringComparison.Ordinal)))
            {
                return false;
            }

            Match who = WhoRegex().Match(message.ToString());
            if (!who.Success)
            {
                return false;
            }

            string name = who.Groups["name"].Value;
            if (!Services.PlayerNameHeuristic.CouldBePlayer(name))
            {
                return false;
            }

            observation = new RosterObservation(RosterObservationKind.Player, name, ClassOf(who.Groups["detail"].Value), string.Empty);
            return true;
        }

        if (message[^1] == '\'' && (message.IndexOf("My leader is ", StringComparison.Ordinal) > 0
                                    || message.IndexOf("I belong to ", StringComparison.Ordinal) > 0))
        {
            Match pet = PetLeaderRegex().Match(message.ToString());
            if (!pet.Success)
            {
                return false;
            }

            string owner = pet.Groups["owner"].Value;
            string petName = pet.Groups["pet"].Value.Trim();
            if (petName.Length == 0 || string.Equals(petName, owner, StringComparison.Ordinal)
                || !Services.PlayerNameHeuristic.CouldBePlayer(owner))
            {
                return false;
            }

            observation = new RosterObservation(RosterObservationKind.Pet, petName, string.Empty, owner);
            return true;
        }

        return false;
    }

    /// <summary>"Archmage (Magician)" gives Magician; a bare "Warrior" gives Warrior; a title alone gives nothing.</summary>
    private static string ClassOf(string detail)
    {
        detail = detail.Trim();
        if (detail.Length == 0)
        {
            return string.Empty;
        }

        int open = detail.LastIndexOf('(');
        int close = detail.LastIndexOf(')');
        string candidate = open >= 0 && close > open ? detail[(open + 1)..close] : detail;
        return EqClass.TryNormalize(candidate, out string className) ? className : string.Empty;
    }

    [GeneratedRegex(
        @"^\[(?:(?<level>\d{1,3})(?: (?<detail>[^\]]*))?|ANON(?:YMOUS)?)\] (?<name>[A-Za-z]{3,15})(?=[ (<]|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex WhoRegex();

    [GeneratedRegex(
        @"^(?<pet>[^\s'][^']{0,38}?) says,? '(?:My leader is|I belong to) (?<owner>[A-Za-z]{3,15})\.?'$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PetLeaderRegex();
}
