using System.Globalization;

namespace EQOverwatch.Core.Parsing;

/// <summary>
/// Reads the result of <c>/consider</c> on a server that adds the target's level, such as
/// <c>A Praetorian Pius scowls at you, ready to attack -- looks like he would wipe the floor with you! (Lvl: 62)</c>.
/// The name is the text before the consider phrase, which depends on how the mob feels about you.
/// </summary>
public static class ConLineParser
{
    private const string LevelMarker = "(Lvl: ";

    // The standard consider phrases, in the order they appear in the game (ally down to "ready to attack").
    private static readonly string[] Phrases =
    {
        " regards you as an ally",
        " looks upon you warmly",
        " kindly considers you",
        " judges you amiably",
        " regards you indifferently",
        " looks your way apprehensively",
        " glowers at you dubiously",
        " glares at you threateningly",
        " scowls at you",
    };

    /// <summary>
    /// True for a consider line that carries a level. <paramref name="mob"/> is the name with a leading "A ", "An " or "The "
    /// lowercased, the way fights are named; <paramref name="level"/> is the number after "Lvl:".
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> message, out string mob, out int level)
    {
        mob = string.Empty;
        level = 0;

        // Cheap checks first: this runs on every line that is not a combat event.
        if (message.Length < 20 || message[^1] != ')')
        {
            return false;
        }

        int marker = message.LastIndexOf(LevelMarker.AsSpan(), StringComparison.Ordinal);
        if (marker <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> number = message[(marker + LevelMarker.Length)..^1];
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out level) || level <= 0 || level > 255)
        {
            level = 0;
            return false;
        }

        if (message[..marker].IndexOf(" -- ".AsSpan(), StringComparison.Ordinal) < 0)
        {
            level = 0;
            return false;
        }

        int cut = -1;
        foreach (string phrase in Phrases)
        {
            int at = message.IndexOf(phrase.AsSpan(), StringComparison.Ordinal);
            if (at > 0 && (cut < 0 || at < cut))
            {
                cut = at;
            }
        }

        if (cut <= 0)
        {
            level = 0;
            return false;
        }

        string name = message[..cut].ToString().Trim();
        if (name.Length == 0 || name.Equals("You", StringComparison.OrdinalIgnoreCase))
        {
            level = 0;
            return false;
        }

        mob = LowerArticle(name);
        return true;
    }

    /// <summary>"A Praetorian Pius" becomes "a Praetorian Pius"; names without a leading article are unchanged.</summary>
    public static string LowerArticle(string name)
    {
        foreach (string article in new[] { "A ", "An ", "The " })
        {
            if (name.StartsWith(article, StringComparison.Ordinal))
            {
                return char.ToLowerInvariant(name[0]) + name[1..];
            }
        }

        return name;
    }
}
