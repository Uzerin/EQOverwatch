using System.Text;
using EQOverwatch.Core.Parsing;

namespace EQOverwatch.Core.Search;

/// <summary>The chat channels the Log search tab can filter on.</summary>
[Flags]
public enum ChatChannel
{
    None = 0,
    Tells = 1,
    Guild = 2,
    Raid = 4,
    Group = 8,
    Say = 16,
    OocShoutAuction = 32,
    ChatChannels = 64,
    Other = 128,
    All = Tells | Guild | Raid | Group | Say | OocShoutAuction | ChatChannels | Other,
}

/// <summary>One line the search found.</summary>
public sealed record LogSearchHit(DateTime Time, string Character, ChatChannel Channel, string Message);

/// <summary>What a search looked through and found.</summary>
public sealed record LogSearchResult(IReadOnlyList<LogSearchHit> Hits, int FilesSearched, long BytesSearched, bool HitLimit, TimeSpan Elapsed);

/// <summary>
/// Searches EverQuest logs for chat lines. Every word of the query has to be in the line (capitals ignored); "a phrase in quotes" stays
/// together. Lines can be limited to some channels and to lines from a date on: the files are sorted by time, so the search jumps to that
/// date instead of reading the whole file.
/// </summary>
public static class LogSearchService
{
    public const int DefaultMaxHits = 1000;

    /// <summary>Splits a query into its words and quoted phrases (lower case).</summary>
    public static IReadOnlyList<string> ParseQuery(string? query)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return terms;
        }

        int i = 0;
        while (i < query.Length)
        {
            if (char.IsWhiteSpace(query[i]))
            {
                i++;
                continue;
            }

            if (query[i] == '"')
            {
                int end = query.IndexOf('"', i + 1);
                string phrase = end < 0 ? query[(i + 1)..] : query[(i + 1)..end];
                if (phrase.Trim().Length > 0)
                {
                    terms.Add(phrase.Trim());
                }

                i = end < 0 ? query.Length : end + 1;
                continue;
            }

            int stop = i;
            while (stop < query.Length && !char.IsWhiteSpace(query[stop]) && query[stop] != '"')
            {
                stop++;
            }

            terms.Add(query[i..stop]);
            i = stop;
        }

        return terms;
    }

    /// <summary>The channel of a message (the text after the timestamp).</summary>
    public static ChatChannel Classify(ReadOnlySpan<char> message)
    {
        int quote = message.IndexOf(", '", StringComparison.Ordinal);
        int doubled = message.IndexOf(",  '", StringComparison.Ordinal); // raid lines can have two spaces
        if (doubled >= 0 && (quote < 0 || doubled < quote))
        {
            quote = doubled;
        }

        if (quote < 0 || message[^1] != '\'')
        {
            return ChatChannel.Other;
        }

        ReadOnlySpan<char> head = message[..quote];
        if (head.EndsWith(" tells you", StringComparison.Ordinal) || head.StartsWith("You told ", StringComparison.Ordinal))
        {
            return ChatChannel.Tells;
        }

        if (head.EndsWith(" tells the guild", StringComparison.Ordinal) || head.SequenceEqual("You say to your guild"))
        {
            return ChatChannel.Guild;
        }

        if (head.EndsWith(" tells the raid", StringComparison.Ordinal) || head.SequenceEqual("You tell your raid"))
        {
            return ChatChannel.Raid;
        }

        if (head.EndsWith(" tells the group", StringComparison.Ordinal) || head.SequenceEqual("You tell your party"))
        {
            return ChatChannel.Group;
        }

        if (head.EndsWith(" says out of character", StringComparison.Ordinal) || head.SequenceEqual("You say out of character")
            || head.EndsWith(" shouts", StringComparison.Ordinal) || head.SequenceEqual("You shout")
            || head.EndsWith(" auctions", StringComparison.Ordinal) || head.SequenceEqual("You auction"))
        {
            return ChatChannel.OocShoutAuction;
        }

        if (head.EndsWith(" says", StringComparison.Ordinal) || head.SequenceEqual("You say"))
        {
            return ChatChannel.Say;
        }

        // "Trick tells general:1" / "You tell general:1"
        int colon = head.LastIndexOf(':');
        if (colon > 0 && colon < head.Length - 1 && char.IsDigit(head[colon + 1])
            && (head.Contains(" tells ", StringComparison.Ordinal) || head.StartsWith("You tell ", StringComparison.Ordinal)))
        {
            return ChatChannel.ChatChannels;
        }

        return ChatChannel.Other;
    }

    /// <summary>
    /// Searches the files (character name, path). Stops at <paramref name="maxHits"/> lines. The newest hits come first.
    /// </summary>
    public static LogSearchResult Search(
        IReadOnlyList<(string Character, string Path)> files,
        string? query,
        ChatChannel channels,
        DateTime? from,
        int maxHits = DefaultMaxHits,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<string> terms = ParseQuery(query);
        var hits = new List<LogSearchHit>();
        long bytes = 0;
        int searched = 0;
        bool limit = false;

        long totalBytes = 0;
        var sizes = new long[files.Count];
        for (int f = 0; f < files.Count; f++)
        {
            try
            {
                sizes[f] = new FileInfo(files[f].Path).Length;
                totalBytes += sizes[f];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sizes[f] = 0;
            }
        }

        long done = 0;
        for (int f = 0; f < files.Count && !limit; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string character, string path) = files[f];
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                long start = from is { } since ? SeekToTime(stream, since) : 0;
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16);
                if (start > 0)
                {
                    reader.ReadLine();
                }

                searched++;
                long lines = 0;
                while (reader.ReadLine() is { } line)
                {
                    if ((++lines & 0x3FFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (progress is not null && totalBytes > 0)
                        {
                            progress.Report(Math.Min(1.0, (done + stream.Position) / (double)totalBytes));
                        }
                    }

                    if (line.Length <= LogLineFormat.MessageOffset)
                    {
                        continue;
                    }

                    ReadOnlySpan<char> message = line.AsSpan(LogLineFormat.MessageOffset);
                    if (!Matches(message, terms))
                    {
                        continue;
                    }

                    ChatChannel channel = Classify(message);
                    if ((channels & channel) == 0 || !LogLineFormat.TryParseTimestamp(line, out DateTime time) || (from is { } min && time < min))
                    {
                        continue;
                    }

                    hits.Add(new LogSearchHit(time, character, channel, message.ToString()));
                    if (hits.Count >= maxHits)
                    {
                        limit = true;
                        break;
                    }
                }

                bytes += stream.Length - start;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a file that cannot be read is skipped
            }

            done += sizes[f];
        }

        progress?.Report(1.0);
        hits.Sort((a, b) => b.Time.CompareTo(a.Time));
        return new LogSearchResult(hits, searched, bytes, limit, watch.Elapsed);
    }

    private static bool Matches(ReadOnlySpan<char> message, IReadOnlyList<string> terms)
    {
        foreach (string term in terms)
        {
            if (message.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A position at or before the first line at or after <paramref name="since"/> (binary search on the line times).</summary>
    internal static long SeekToTime(FileStream stream, DateTime since)
    {
        long low = 0;
        long high = stream.Length;
        var buffer = new byte[4096];
        while (high - low > 64 * 1024)
        {
            long mid = low + (high - low) / 2;
            DateTime? time = FirstTimeAfter(stream, mid, buffer);
            if (time is null || time.Value >= since)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        return low;
    }

    private static DateTime? FirstTimeAfter(FileStream stream, long position, byte[] buffer)
    {
        stream.Seek(position, SeekOrigin.Begin);
        int read = stream.Read(buffer, 0, buffer.Length);
        string text = Encoding.Latin1.GetString(buffer, 0, read);
        int newline = text.IndexOf('\n');
        while (newline >= 0 && newline < text.Length - 1)
        {
            int next = text.IndexOf('\n', newline + 1);
            string line = next < 0 ? text[(newline + 1)..] : text[(newline + 1)..next];
            if (LogLineFormat.TryParseTimestamp(line.TrimEnd('\r'), out DateTime time))
            {
                return time;
            }

            newline = next;
        }

        return null;
    }
}
