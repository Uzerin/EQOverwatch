using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Attendance;

/// <summary>One person on the raid roster file.</summary>
public sealed record RaidMember(int Group, string Name, int Level, string Class, string Role);

/// <summary>The raid as it stood when the Raid window's Dump button wrote <c>RaidRoster_server-20261029-180139</c>.</summary>
public sealed class RaidAttendance
{
    public string Server { get; set; } = string.Empty;

    /// <summary>Local date and time from the file name.</summary>
    public DateTime Taken { get; set; }

    public string FileName { get; set; } = string.Empty;
    public List<RaidMember> Members { get; set; } = new();
}

/// <summary>How often each person was at the raids in a set of records.</summary>
public sealed record AttendanceTotal(string Name, string Class, int Present, int Raids, double Percent, DateTime LastSeen);

/// <summary>
/// Reads the raid roster dumps in the game folder (<c>RaidRoster_server-yyyyMMdd-HHmmss</c>, optionally ending in .txt) and keeps
/// one JSON record per dump in <c>directory</c>, so the attendance survives the files being deleted. A dump is only read once.
/// </summary>
public sealed partial class RaidRosterStore
{
    [GeneratedRegex(@"^RaidRoster_(?<server>[A-Za-z0-9]+)-(?<stamp>\d{8}-\d{6})(?:\.txt)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string? _directory;
    private readonly object _gate = new();
    private readonly Dictionary<string, RaidAttendance> _records = new(StringComparer.OrdinalIgnoreCase);

    public RaidRosterStore(string? directory = null)
    {
        _directory = directory;
        LoadSaved();
    }

    public event Action? Changed;

    /// <summary>The dumps read so far, newest first.</summary>
    public IReadOnlyList<RaidAttendance> Records()
    {
        lock (_gate)
        {
            return _records.Values.OrderByDescending(r => r.Taken).ToList();
        }
    }

    /// <summary>"RaidRoster_frostreaver-20261029-180139" gives the server and the time; false for any other name.</summary>
    public static bool TryParseFileName(string fileNameOrPath, out string server, out DateTime taken)
    {
        server = string.Empty;
        taken = default;
        Match match = FileNameRegex().Match(Path.GetFileName(fileNameOrPath));
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out taken))
        {
            return false;
        }

        server = match.Groups["server"].Value;
        return true;
    }

    /// <summary>
    /// Tab-separated lines: group, name, level, class, then the role ("Raid Leader", "Group Leader") when there is one. Lines that
    /// do not start with a number and a name are skipped.
    /// </summary>
    public static List<RaidMember> ParseMembers(IEnumerable<string> lines)
    {
        var members = new List<RaidMember>();
        foreach (string line in lines)
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 4
                || !int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int group)
                || string.IsNullOrWhiteSpace(parts[1])
                || !int.TryParse(parts[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int level))
            {
                continue;
            }

            string role = parts.Length > 4 ? parts[4].Trim() : string.Empty;
            members.Add(new RaidMember(group, parts[1].Trim(), level, parts[3].Trim(), role));
        }

        return members;
    }

    /// <summary>Reads every roster dump in the game folder that has not been read yet. Returns how many were added.</summary>
    public int ScanAll(string? eqFolder)
    {
        if (string.IsNullOrWhiteSpace(eqFolder) || !Directory.Exists(eqFolder))
        {
            return 0;
        }

        int added = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(eqFolder, "RaidRoster_*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (string file in files)
        {
            if (!TryParseFileName(file, out string server, out DateTime taken))
            {
                continue;
            }

            string key = Path.GetFileNameWithoutExtension(file);
            lock (_gate)
            {
                if (_records.ContainsKey(key))
                {
                    continue;
                }
            }

            try
            {
                string[] lines;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
                }

                List<RaidMember> members = ParseMembers(lines);
                if (members.Count == 0)
                {
                    continue;
                }

                var record = new RaidAttendance { Server = server, Taken = taken, FileName = key, Members = members };
                lock (_gate)
                {
                    _records[key] = record;
                    Save(record);
                }

                added++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the game may still be writing it; the next scan tries again
            }
        }

        if (added > 0)
        {
            Changed?.Invoke();
        }

        return added;
    }

    /// <summary>Removes one record (and its saved file).</summary>
    public void Remove(RaidAttendance record)
    {
        lock (_gate)
        {
            _records.Remove(record.FileName);
            if (_directory is not null)
            {
                try
                {
                    File.Delete(Path.Combine(_directory, record.FileName + ".json"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // already gone or locked
                }
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Who was present at the given raids, with how many of those raids each person was at.</summary>
    public static IReadOnlyList<AttendanceTotal> Totals(IReadOnlyList<RaidAttendance> raids)
    {
        var byName = new Dictionary<string, (string Class, int Present, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
        foreach (RaidAttendance raid in raids)
        {
            foreach (RaidMember member in raid.Members.DistinctBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                byName.TryGetValue(member.Name, out (string Class, int Present, DateTime Last) current);
                byName[member.Name] = (member.Class, current.Present + 1, raid.Taken > current.Last ? raid.Taken : current.Last);
            }
        }

        int total = raids.Count;
        return byName
            .Select(p => new AttendanceTotal(p.Key, p.Value.Class, p.Value.Present, total, total == 0 ? 0 : 100.0 * p.Value.Present / total, p.Value.Last))
            .OrderByDescending(t => t.Present)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void LoadSaved()
    {
        if (_directory is null || !Directory.Exists(_directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(_directory, "RaidRoster_*.json"))
        {
            try
            {
                RaidAttendance? record = JsonSerializer.Deserialize<RaidAttendance>(File.ReadAllText(file), JsonOptions);
                if (record is not null && record.FileName.Length > 0)
                {
                    _records[record.FileName] = record;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a damaged record is skipped
            }
        }
    }

    private void Save(RaidAttendance record)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, record.FileName + ".json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, JsonOptions));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory only
        }
    }
}
