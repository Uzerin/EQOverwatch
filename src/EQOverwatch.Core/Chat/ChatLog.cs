using System.Text;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Chat;

/// <summary>The kinds of player chat.</summary>
public enum ChatChannel : byte
{
    Tell,
    Say,
    Group,
    Raid,
    Guild,
    Ooc,
    Shout,
    Auction,
    Channel,
}

/// <summary>One chat line: when, which of your characters' logs saw it, the channel ("General:1" for chat channels), who spoke and what.</summary>
public sealed record ChatLine(DateTime Utc, string Character, ChatChannel Channel, string ChannelName, string Speaker, string Text, bool IsYou);

/// <summary>
/// Player chat from your characters' logs: tells, say, group, raid, guild, out of character, shout, auction and chat channels. The same line
/// seen by several of your characters counts once. Lines that look like a mob or NPC talking (a name of more than one word) are left out.
/// Keeps the last <see cref="MaxLines"/> lines; <see cref="ScanRecent"/> reads a log's last day when the app starts.
/// </summary>
public sealed partial class ChatLog : ITriggerLineSink
{
    public const int MaxLines = 20000;
    private static readonly TimeSpan SameLineWindow = TimeSpan.FromSeconds(3);

    // "Ream tells the raid, 'x'" / "Ream told you, 'x'" / "Ream tells General:1, 'x'" / "Ream says out of character, 'x'"
    [GeneratedRegex(@"^(?<who>[A-Za-z]+) (?<how>tells you|told you|says out of character|says|tells the group|tells the raid|tells the guild|shouts|auctions|tells (?<chan>[A-Za-z0-9]+:\d+)), '(?<text>.*)'$", RegexOptions.CultureInvariant)]
    private static partial Regex OtherRegex();

    // "You say, 'x'" / "You told Ream, 'x'" / "You tell your raid, 'x'" / "You say to your guild, 'x'" / "You tell General:1, 'x'"
    [GeneratedRegex(@"^You (?<how>say out of character|say to your guild|say|tell your party|tell your raid|shout|auction|told (?<to>[A-Za-z]+)|tell (?<chan>[A-Za-z0-9]+:\d+)), '(?<text>.*)'$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnRegex();

    private readonly object _gate = new();
    private readonly LinkedList<ChatLine> _lines = new();
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.Length < 8 || message[^1] != '\'' || message.IndexOf(", '", StringComparison.Ordinal) < 0)
        {
            return;
        }

        if (Parse(characterName, message.ToString(), receivedUtc) is { } line)
        {
            Add(line);
        }
    }

    /// <summary>Reads a chat line (the text after the timestamp), or null.</summary>
    public static ChatLine? Parse(string character, string message, DateTime utc)
    {
        if (message.StartsWith("You ", StringComparison.Ordinal))
        {
            Match own = OwnRegex().Match(message);
            if (!own.Success)
            {
                return null;
            }

            string how = own.Groups["how"].Value;
            (ChatChannel channel, string name) = how switch
            {
                "say out of character" => (ChatChannel.Ooc, string.Empty),
                "say to your guild" => (ChatChannel.Guild, string.Empty),
                "say" => (ChatChannel.Say, string.Empty),
                "tell your party" => (ChatChannel.Group, string.Empty),
                "tell your raid" => (ChatChannel.Raid, string.Empty),
                "shout" => (ChatChannel.Shout, string.Empty),
                "auction" => (ChatChannel.Auction, string.Empty),
                _ when own.Groups["to"].Success => (ChatChannel.Tell, "to " + own.Groups["to"].Value),
                _ => (ChatChannel.Channel, own.Groups["chan"].Value),
            };
            return new ChatLine(utc, character, channel, name, character, own.Groups["text"].Value, true);
        }

        Match other = OtherRegex().Match(message);
        if (!other.Success)
        {
            return null;
        }

        string verb = other.Groups["how"].Value;
        (ChatChannel ch, string chName) = verb switch
        {
            "tells you" or "told you" => (ChatChannel.Tell, "to " + character),
            "says out of character" => (ChatChannel.Ooc, string.Empty),
            "says" => (ChatChannel.Say, string.Empty),
            "tells the group" => (ChatChannel.Group, string.Empty),
            "tells the raid" => (ChatChannel.Raid, string.Empty),
            "tells the guild" => (ChatChannel.Guild, string.Empty),
            "shouts" => (ChatChannel.Shout, string.Empty),
            "auctions" => (ChatChannel.Auction, string.Empty),
            _ => (ChatChannel.Channel, other.Groups["chan"].Value),
        };
        string who = other.Groups["who"].Value;
        return new ChatLine(utc, character, ch, chName, who, other.Groups["text"].Value, who.Equals(character, StringComparison.OrdinalIgnoreCase));
    }

    private void Add(ChatLine line)
    {
        lock (_gate)
        {
            // Another of your characters already logged the same line (tells are each character's own).
            if (line.Channel != ChatChannel.Tell)
            {
                for (LinkedListNode<ChatLine>? node = _lines.Last; node is not null && (line.Utc - node.Value.Utc).Duration() <= SameLineWindow * 4; node = node.Previous)
                {
                    ChatLine seen = node.Value;
                    if (seen.Channel == line.Channel && seen.Speaker.Equals(line.Speaker, StringComparison.OrdinalIgnoreCase)
                        && seen.Text == line.Text && (line.Utc - seen.Utc).Duration() <= SameLineWindow)
                    {
                        return;
                    }
                }
            }

            // Keep time order: a line read late from history goes in its place.
            LinkedListNode<ChatLine>? after = _lines.Last;
            while (after is not null && after.Value.Utc > line.Utc)
            {
                after = after.Previous;
            }

            if (after is null)
            {
                _lines.AddFirst(line);
            }
            else
            {
                _lines.AddAfter(after, line);
            }

            while (_lines.Count > MaxLines)
            {
                _lines.RemoveFirst();
            }

            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Every line kept, oldest first.</summary>
    public IReadOnlyList<ChatLine> All()
    {
        lock (_gate)
        {
            return _lines.ToList();
        }
    }

    /// <summary>Reads a log's chat from <paramref name="sinceLocal"/> on (at start, the last day).</summary>
    public void ScanRecent(string path, string character, DateTime sinceLocal, CancellationToken cancellation = default)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = LogSeek.FindOffset(stream, sinceLocal);
            using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
            int count = 0;
            while (reader.ReadLine() is { } line)
            {
                if ((++count & 0x3FFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                if (line.Length <= LogLineFormat.MessageOffset + 6 || line[^1] != '\'' || !LogLineFormat.TryParseTimestamp(line, out DateTime stamp) || stamp < sinceLocal)
                {
                    continue;
                }

                if (Parse(character, line[LogLineFormat.MessageOffset..], stamp.ToUniversalTime()) is { } chat)
                {
                    Add(chat);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a log that cannot be read adds nothing
        }
    }
}
