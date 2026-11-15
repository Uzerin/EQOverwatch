using System.Globalization;
using System.Text;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Maps;

/// <summary>Where a character is: the zone of its last "You have entered ..." line and its last /loc there (Y, X, Z as EQ prints them).</summary>
public sealed record CharacterWhereabouts(string Zone, (double Y, double X, double Z)? Loc);

/// <summary>
/// Follows each character's zone and /loc from its log. Live lines come through <see cref="OnLine(string, string, ReadOnlySpan{char}, DateTime)"/>;
/// <see cref="ReadFromLog"/> finds where a character was when its log was last written, for the time before the app started.
/// </summary>
public sealed class ZoneTracker : ITriggerLineSink
{
    private const string EnteredPrefix = "You have entered ";
    private const string LocPrefix = "Your Location is ";
    private const int ChunkSize = 1 << 20;
    private const long MaxBytesBack = 64L << 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, CharacterWhereabouts> _where = new(StringComparer.OrdinalIgnoreCase);
    private long _version;

    /// <summary>Goes up whenever a character's zone or /loc changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    public void OnLine(string characterName, ReadOnlySpan<char> message, DateTime receivedUtc) =>
        OnLine(characterName, string.Empty, message, receivedUtc);

    public void OnLine(string characterName, string serverName, ReadOnlySpan<char> message, DateTime receivedUtc)
    {
        if (message.StartsWith(EnteredPrefix, StringComparison.Ordinal))
        {
            if (ParseZone(message) is { } zone)
            {
                Set(characterName, serverName, new CharacterWhereabouts(zone, null));
            }
        }
        else if (message.StartsWith(LocPrefix, StringComparison.Ordinal) && ParseLoc(message) is { } loc)
        {
            lock (_gate)
            {
                string key = Key(characterName, serverName);
                if (_where.TryGetValue(key, out CharacterWhereabouts? where))
                {
                    _where[key] = where with { Loc = loc };
                    Interlocked.Increment(ref _version);
                }
            }
        }
    }

    /// <summary>Where the character is, or null when no zone line has been seen.</summary>
    public CharacterWhereabouts? Get(string characterName, string serverName)
    {
        lock (_gate)
        {
            return _where.GetValueOrDefault(Key(characterName, serverName));
        }
    }

    /// <summary>
    /// Reads the end of a log backwards (up to its last 64 MB) for the character's last zone line and its last /loc after it, and keeps
    /// them unless a live line has already said where the character is.
    /// </summary>
    public void ReadFromLog(string path, string characterName, string serverName)
    {
        CharacterWhereabouts? found = FindInLog(path);
        if (found is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_where.TryAdd(Key(characterName, serverName), found))
            {
                Interlocked.Increment(ref _version);
            }
        }
    }

    /// <summary>The last zone line in a log and the last /loc after it, or null when there is none in its last 64 MB.</summary>
    public static CharacterWhereabouts? FindInLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long end = stream.Length;
            long stop = Math.Max(0, end - MaxBytesBack);
            (double, double, double)? loc = null;
            byte[] buffer = new byte[ChunkSize];
            string carry = string.Empty; // the start of a line cut by the chunk boundary
            while (end > stop)
            {
                long start = Math.Max(stop, end - ChunkSize);
                int length = (int)(end - start);
                stream.Position = start;
                stream.ReadExactly(buffer, 0, length);
                string text = Encoding.UTF8.GetString(buffer, 0, length) + carry;
                string[] lines = text.Split('\n');
                int first = start > stop ? 1 : 0; // the first piece may be the end of a line from the chunk before
                carry = first == 1 ? lines[0] : string.Empty;
                for (int i = lines.Length - 1; i >= first; i--)
                {
                    ReadOnlySpan<char> message = Message(lines[i]);
                    if (loc is null && message.StartsWith(LocPrefix, StringComparison.Ordinal))
                    {
                        loc = ParseLoc(message);
                    }
                    else if (message.StartsWith(EnteredPrefix, StringComparison.Ordinal) && ParseZone(message) is { } zone)
                    {
                        return new CharacterWhereabouts(zone, loc);
                    }
                }

                end = start;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private void Set(string characterName, string serverName, CharacterWhereabouts where)
    {
        lock (_gate)
        {
            _where[Key(characterName, serverName)] = where;
            Interlocked.Increment(ref _version);
        }
    }

    private static string Key(string characterName, string serverName) => characterName + "|" + serverName;

    /// <summary>The text after "[Sat May 30 12:24:53 2026] ".</summary>
    private static ReadOnlySpan<char> Message(string line)
    {
        ReadOnlySpan<char> span = line.AsSpan().TrimEnd('\r');
        int close = span.IndexOf("] ", StringComparison.Ordinal);
        return span.Length > 0 && span[0] == '[' && close > 0 ? span[(close + 2)..] : span;
    }

    /// <summary>The zone in "You have entered The Bazaar.", or null for lines that only look like one ("You have entered an area where levitation is prohibited.").</summary>
    public static string? ParseZone(ReadOnlySpan<char> message)
    {
        ReadOnlySpan<char> name = message[EnteredPrefix.Length..].Trim();
        if (name.IsEmpty || name[^1] != '.')
        {
            return null;
        }

        name = name[..^1].Trim();
        if (name.IsEmpty || name.StartsWith("an area", StringComparison.OrdinalIgnoreCase) || name.StartsWith("a ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("an ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name.ToString();
    }

    /// <summary>The numbers in "Your Location is 1612.04, -1691.07, -80.71" (Y, X, Z).</summary>
    public static (double Y, double X, double Z)? ParseLoc(ReadOnlySpan<char> message)
    {
        string[] parts = message[LocPrefix.Length..].ToString().Split(',');
        if (parts.Length != 3)
        {
            return null;
        }

        var numbers = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return (numbers[0], numbers[1], numbers[2]);
    }
}
