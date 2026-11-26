using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Discord;

/// <summary>A raid chat line: who said it ("You" for the log's owner) and what.</summary>
public sealed record RaidSay(string Speaker, string Text);

/// <summary>An item the loot window gave to a player ("A Head of the Inquisitor was given to Tirze."); <see cref="Player"/> is "You" for the log's owner.</summary>
public sealed record GivenItem(string Item, int Count, string Player);

/// <summary>An item a player looted from a corpse; <see cref="Player"/> is "You" for the log's owner.</summary>
public sealed record LootedItem(string Item, int Count, string Player, string Corpse);

/// <summary>The loot and raid lines the Discord posts are made from.</summary>
public static partial class RaidLootLines
{
    [GeneratedRegex(@"^(?<who>[A-Za-z]+) tells the raid,\s+'(?<text>.*)'$", RegexOptions.CultureInvariant)]
    private static partial Regex RaidTell();

    [GeneratedRegex(@"^You tell your raid,\s+'(?<text>.*)'$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnRaidTell();

    [GeneratedRegex(@"^(?:(?:An?|Some|The) |(?<n>\d+) )?(?<item>.+?) (?:was|were) given to (?<who>you|[A-Z][a-z]+)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex Given();

    [GeneratedRegex(@"^--(?<who>You|[A-Z][a-z]+) (?:has|have) looted (?:(?:an?|some|the) |(?<n>\d+) )?(?<item>.+?) from (?<corpse>.+?)'s corpse\.--$", RegexOptions.CultureInvariant)]
    private static partial Regex Looted();

    [GeneratedRegex(@"^No one was interested in the (?<n>\d+) item\(s\): (?<item>.+?)\. These items can be randomed again", RegexOptions.CultureInvariant)]
    private static partial Regex NoOneInterested();

    [GeneratedRegex(@"^\s*(?<item>.+?)(?:\s+x(?<n>\d{1,3}))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ListedItem();

    [GeneratedRegex(@"\s*/{1,2}\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ItemSeparator();

    [GeneratedRegex(@"\s+\|\s+", RegexOptions.CultureInvariant)]
    private static partial Regex PipeSeparator();

    public static bool TryRaidSay(ReadOnlySpan<char> message, out RaidSay say)
    {
        say = null!;
        if (!message.Contains(" raid, ", StringComparison.Ordinal) || message.Length > 600)
        {
            return false;
        }

        string line = message.ToString();
        if (OwnRaidTell().Match(line) is { Success: true } own)
        {
            say = new RaidSay("You", own.Groups["text"].Value);
            return true;
        }

        if (RaidTell().Match(line) is { Success: true } other)
        {
            say = new RaidSay(other.Groups["who"].Value, other.Groups["text"].Value);
            return true;
        }

        return false;
    }

    public static bool TryGiven(ReadOnlySpan<char> message, out GivenItem given)
    {
        given = null!;
        if (!message.Contains(" given to ", StringComparison.Ordinal))
        {
            return false;
        }

        Match m = Given().Match(message.ToString());
        if (!m.Success)
        {
            return false;
        }

        string who = m.Groups["who"].Value;
        given = new GivenItem(m.Groups["item"].Value, Count(m), who == "you" ? "You" : who);
        return true;
    }

    public static bool TryLooted(ReadOnlySpan<char> message, out LootedItem looted)
    {
        looted = null!;
        if (!message.StartsWith("--", StringComparison.Ordinal) || !message.Contains(" looted ", StringComparison.Ordinal))
        {
            return false;
        }

        Match m = Looted().Match(message.ToString());
        if (!m.Success)
        {
            return false;
        }

        looted = new LootedItem(m.Groups["item"].Value, Count(m), m.Groups["who"].Value, m.Groups["corpse"].Value);
        return true;
    }

    /// <summary>"No one was interested in the 1 item(s): Head of the Inquisitor. ...": an item left on the corpse.</summary>
    public static bool TryNoOneInterested(ReadOnlySpan<char> message, out string item, out int count)
    {
        item = string.Empty;
        count = 0;
        if (!message.StartsWith("No one was interested", StringComparison.Ordinal))
        {
            return false;
        }

        Match m = NoOneInterested().Match(message.ToString());
        if (!m.Success)
        {
            return false;
        }

        item = m.Groups["item"].Value;
        count = Count(m);
        return true;
    }

    /// <summary>True for the lines that end a character's raid ("You were removed from the raid.", "Your raid was disbanded.").</summary>
    public static bool IsRaidLeft(ReadOnlySpan<char> message) =>
        message.SequenceEqual("You were removed from the raid.") || message.SequenceEqual("Your raid was disbanded.")
        || message.SequenceEqual("You have left the raid.");

    /// <summary>
    /// The items of a loot list linked to raid chat ("Great Shadow Platemail // Thorny Chain Helm x2 // ..." or the loot window's
    /// "Undead Shissar Scales | Shabby Rough Spell Scroll"), or none when the line is not one. A list needs at least two items split by
    /// "//" (a lone "/" also splits once a "//" was seen) or by " | ". A " | " line with "=" or "@" is a DPS parse, not loot.
    /// </summary>
    public static IReadOnlyList<(string Item, int Count)> LootList(string text)
    {
        string[] parts;
        if (text.Contains("//", StringComparison.Ordinal))
        {
            parts = ItemSeparator().Split(text);
        }
        else if (text.Contains(" | ", StringComparison.Ordinal) && text.IndexOfAny(new[] { '=', '@' }) < 0)
        {
            parts = PipeSeparator().Split(text);
        }
        else
        {
            return Array.Empty<(string, int)>();
        }

        var items = new List<(string, int)>();
        foreach (string part in parts)
        {
            if (part.Trim().Length == 0)
            {
                continue;
            }

            Match m = ListedItem().Match(part);
            string item = m.Groups["item"].Value.Trim();
            if (item.Length is 0 or > 80)
            {
                return Array.Empty<(string, int)>();
            }

            items.Add((item, m.Groups["n"].Success ? Math.Max(1, int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)) : 1));
        }

        return items.Count >= 2 ? items : Array.Empty<(string, int)>();
    }

    private static int Count(Match m) =>
        m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
            ? Math.Max(1, n)
            : 1;
}

/// <summary>"Lord Inquisitor Seru has been defeated by a group of hardy adventurers! ...": the server's kill announcement for a raid boss.</summary>
public static class RaidBossAnnouncementParser
{
    private const string Marker = " has been defeated by a group of hardy adventurers!";

    public static bool TryParse(ReadOnlySpan<char> message, out string boss)
    {
        int at = message.IndexOf(Marker, StringComparison.Ordinal);
        boss = at > 0 ? message[..at].Trim().ToString() : string.Empty;
        return boss.Length is > 0 and <= 64;
    }
}
