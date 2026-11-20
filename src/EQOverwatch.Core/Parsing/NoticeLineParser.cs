using System.Globalization;

namespace EQOverwatch.Core.Parsing;

/// <summary>The kinds of log lines the app tells you about: a follow that broke, a player gone linkdead, a /random roll, a pet buff fading, a level.</summary>
public enum NoticeKind
{
    None,
    FollowBroke,
    Linkdead,
    Roll,
    PetBuffFaded,
    LevelUp,
}

/// <summary>A parsed notice line: the name it is about (the followed player, the linkdead player, the roller or the pet's spell) and numbers.</summary>
public readonly record struct NoticeLine(NoticeKind Kind, string Name, int Value = 0, int Low = 0, int High = 0);

/// <summary>Reads the lines behind the box alerts and Tells overlay notices.</summary>
public static class NoticeLineParser
{
    private const string FollowPrefix = "You are no longer auto-following ";
    private const string LinkdeadSuffix = " has gone Linkdead.";
    private const string RollPrefix = "**A Magic Die is rolled by ";
    private const string RollRange = ". It could have been any number from ";
    private const string RollTurned = ", but this time it turned up a ";
    private const string PetPrefix = "Your pet's ";
    private const string PetSuffix = " spell has worn off.";
    private const string LevelPrefix = "You have gained a level! Welcome to level ";

    public static NoticeLine Parse(ReadOnlySpan<char> message)
    {
        if (message.StartsWith(FollowPrefix, StringComparison.Ordinal) && message.EndsWith(".", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> name = message[FollowPrefix.Length..^1];
            return name.Length > 0 ? new NoticeLine(NoticeKind.FollowBroke, name.ToString()) : default;
        }

        if (message.EndsWith(LinkdeadSuffix, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> name = message[..^LinkdeadSuffix.Length];
            return name.Length is > 0 and <= 64 && name.IndexOf(' ') < 0 ? new NoticeLine(NoticeKind.Linkdead, name.ToString()) : default;
        }

        if (message.StartsWith(RollPrefix, StringComparison.Ordinal))
        {
            return ParseRoll(message[RollPrefix.Length..]);
        }

        if (message.StartsWith(PetPrefix, StringComparison.Ordinal) && message.EndsWith(PetSuffix, StringComparison.Ordinal)
            && message.Length > PetPrefix.Length + PetSuffix.Length)
        {
            return new NoticeLine(NoticeKind.PetBuffFaded, message[PetPrefix.Length..^PetSuffix.Length].ToString());
        }

        if (message.StartsWith(LevelPrefix, StringComparison.Ordinal) && message.EndsWith("!", StringComparison.Ordinal)
            && int.TryParse(message[LevelPrefix.Length..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int level))
        {
            return new NoticeLine(NoticeKind.LevelUp, string.Empty, level);
        }

        return default;
    }

    // "Honshu. It could have been any number from 0 to 100, but this time it turned up a 66."
    private static NoticeLine ParseRoll(ReadOnlySpan<char> rest)
    {
        int range = rest.IndexOf(RollRange, StringComparison.Ordinal);
        if (range <= 0)
        {
            return default;
        }

        string name = rest[..range].ToString();
        ReadOnlySpan<char> numbers = rest[(range + RollRange.Length)..];
        int to = numbers.IndexOf(" to ", StringComparison.Ordinal);
        int turned = numbers.IndexOf(RollTurned, StringComparison.Ordinal);
        if (to <= 0 || turned <= to)
        {
            return default;
        }

        ReadOnlySpan<char> value = numbers[(turned + RollTurned.Length)..].TrimEnd('.');
        return int.TryParse(numbers[..to], NumberStyles.None, CultureInfo.InvariantCulture, out int low)
               && int.TryParse(numbers[(to + 4)..turned], NumberStyles.None, CultureInfo.InvariantCulture, out int high)
               && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int rolled)
            ? new NoticeLine(NoticeKind.Roll, name, rolled, low, high)
            : default;
    }
}
