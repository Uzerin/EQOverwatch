using System.Globalization;
using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Attendance;

/// <summary>One member of a guild roster dump: name, level, class, rank, whether an alt, and the last day they were on.</summary>
public sealed record GuildMember(string Name, int Level, string Class, string Rank, bool IsAlt, DateTime? LastOn);

/// <summary>
/// Reads the guild roster dump (<c>/outputfile guild</c>, "Guild Name_frostreaver-20261118-221837.txt"): tab-separated rows of name,
/// level, class, rank, alt flag and last-on date (MM/dd/yy), then notes. The newest dump per server is kept, read from the game folder.
/// </summary>
public static partial class GuildRoster
{
    [GeneratedRegex(@"^(?<guild>[A-Za-z][A-Za-z0-9 '\-]*)_(?<server>[A-Za-z0-9]+)-(?<stamp>\d{8}-\d{6})(?:\.txt)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();

    /// <summary>"Leadership Optional_frostreaver-20261118-221837.txt" gives the guild, server and time; raid dumps are not guild dumps.</summary>
    public static bool TryParseFileName(string fileNameOrPath, out string guild, out string server, out DateTime taken)
    {
        guild = server = string.Empty;
        taken = default;
        Match match = FileNameRegex().Match(Path.GetFileName(fileNameOrPath ?? string.Empty));
        if (!match.Success || match.Groups["guild"].Value.Equals("RaidRoster", StringComparison.OrdinalIgnoreCase)
            || !DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out taken))
        {
            return false;
        }

        guild = match.Groups["guild"].Value.Trim();
        server = match.Groups["server"].Value.ToLowerInvariant();
        return true;
    }

    public static List<GuildMember> Parse(IEnumerable<string> lines)
    {
        var result = new List<GuildMember>();
        foreach (string line in lines)
        {
            string[] c = line.Split('\t');
            if (c.Length < 6 || c[0].Trim().Length == 0 || !c[0].Trim().All(char.IsLetter)
                || !int.TryParse(c[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level))
            {
                continue;
            }

            DateTime? lastOn = DateTime.TryParseExact(c[5].Trim(), "MM/dd/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? d : null;
            result.Add(new GuildMember(c[0].Trim(), level, c[2].Trim(), c[3].Trim(), c[4].Trim().Equals("A", StringComparison.OrdinalIgnoreCase), lastOn));
        }

        return result;
    }

    /// <summary>The newest guild dump for <paramref name="server"/> in the game folder, or null.</summary>
    public static (string Guild, DateTime Taken, List<GuildMember> Members)? ReadNewest(string? eqFolder, string server)
    {
        if (string.IsNullOrWhiteSpace(eqFolder) || !Directory.Exists(eqFolder))
        {
            return null;
        }

        try
        {
            (string Path, string Guild, DateTime Taken)? best = null;
            foreach (string file in Directory.EnumerateFiles(eqFolder, "*_*-*-*.txt"))
            {
                if (TryParseFileName(file, out string guild, out string s, out DateTime taken)
                    && s.Equals(server, StringComparison.OrdinalIgnoreCase) && (best is null || taken > best.Value.Taken))
                {
                    best = (file, guild, taken);
                }
            }

            return best is { } b ? (b.Guild, b.Taken, Parse(File.ReadLines(b.Path))) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Puts the class and level from guild and raid roster dumps into the player roster, so the Class column and labels know them.</summary>
public static class DumpRosterImport
{
    /// <summary>Learns the newest raid dump of each server, then the newest guild dump of each server. Returns how many entries changed.</summary>
    public static int Learn(EQOverwatch.Core.Roster.PlayerRoster roster, string? eqFolder, IEnumerable<RaidAttendance> raids)
    {
        int changed = 0;
        foreach (RaidAttendance raid in raids.Where(r => r.Server.Length > 0).GroupBy(r => r.Server, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.OrderByDescending(r => r.Taken).First()))
        {
            changed += roster.LearnFromDump(raid.Members.Select(m => (m.Name, m.Class, m.Level)), raid.Server);
        }

        foreach (string server in GuildServers(eqFolder))
        {
            if (GuildRoster.ReadNewest(eqFolder, server) is { } guild)
            {
                changed += roster.LearnFromDump(guild.Members.Select(m => (m.Name, m.Class, m.Level)), server);
            }
        }

        return changed;
    }

    private static IEnumerable<string> GuildServers(string? eqFolder)
    {
        if (string.IsNullOrWhiteSpace(eqFolder) || !Directory.Exists(eqFolder))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(eqFolder, "*_*-*-*.txt")
                .Select(f => GuildRoster.TryParseFileName(f, out _, out string server, out _) ? server : string.Empty)
                .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>One raid group: its number, members (name and class) and whether it has a healer (cleric, druid or shaman).</summary>
public sealed record RaidGroup(int Group, IReadOnlyList<RaidMember> Members, bool HasHealer);

public static class RaidGroups
{
    private static readonly string[] Healers = { "Cleric", "Druid", "Shaman" };

    public static bool IsHealer(string className) => Healers.Any(h => className.Equals(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>The raid's groups in order (group 0, the ungrouped, last).</summary>
    public static IReadOnlyList<RaidGroup> Of(IEnumerable<RaidMember> members) =>
        members.GroupBy(m => m.Group)
            .OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key)
            .Select(g => new RaidGroup(g.Key, g.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList(), g.Any(m => IsHealer(m.Class))))
            .ToList();

    /// <summary>
    /// Guild members who were not in the raid: not alts, at least the level of the raid's lowest member, and on within
    /// <paramref name="activeDays"/> days before it. Newest-seen first.
    /// </summary>
    public static IReadOnlyList<GuildMember> Missed(RaidAttendance raid, IEnumerable<GuildMember> guild, int activeDays = 30)
    {
        var there = new HashSet<string>(raid.Members.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        DateTime since = raid.Taken.Date.AddDays(-activeDays);
        int minLevel = raid.Members.Where(m => m.Level > 0).Select(m => m.Level).DefaultIfEmpty(1).Min();
        return guild.Where(g => !g.IsAlt && g.Level >= minLevel && !there.Contains(g.Name) && g.LastOn is { } on && on >= since)
            .OrderByDescending(g => g.LastOn).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
