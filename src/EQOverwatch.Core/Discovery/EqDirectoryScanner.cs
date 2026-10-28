using EQOverwatch.Core.Parsing;

namespace EQOverwatch.Core.Discovery;

/// <summary>A character log found in <c>&lt;EverQuest&gt;\Logs\eqlog_&lt;Character&gt;_&lt;Server&gt;.txt</c>.</summary>
public sealed record DiscoveredLog(string FilePath, string CharacterName, string ServerName);

/// <summary>Finds the EverQuest folder's character logs and its spell file.</summary>
public static class EqDirectoryScanner
{
    public const string LogsFolderName = "Logs";
    public const string SpellsFileName = "spells_us.txt";

    /// <summary>
    /// Cleans up a folder the user picked: surrounding whitespace and trailing separators are removed, and a path that ends in
    /// <c>Logs</c> is replaced by its parent, so choosing either the game folder or its Logs folder gives the game folder.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = Path.TrimEndingDirectorySeparator(path.Trim());
        if (string.Equals(Path.GetFileName(trimmed), LogsFolderName, StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(trimmed);
            if (!string.IsNullOrEmpty(parent))
            {
                return parent;
            }
        }

        return trimmed;
    }

    public static string GetLogsFolder(string eqFolder) => Path.Combine(eqFolder, LogsFolderName);

    public static string GetSpellsFilePath(string eqFolder) => Path.Combine(eqFolder, SpellsFileName);

    /// <summary>True when <c>spells_us.txt</c> exists directly in <paramref name="eqFolder"/>.</summary>
    public static bool HasSpellsFile(string? eqFolder) =>
        !string.IsNullOrWhiteSpace(eqFolder) && File.Exists(GetSpellsFilePath(eqFolder));

    /// <summary>
    /// Every <c>eqlog_&lt;Character&gt;_&lt;Server&gt;.txt</c> in the Logs folder, sorted by server (A-Z) and then character (A-Z).
    /// A missing or unreadable folder gives an empty list.
    /// </summary>
    public static IReadOnlyList<DiscoveredLog> Discover(string? eqFolder)
    {
        if (string.IsNullOrWhiteSpace(eqFolder))
        {
            return Array.Empty<DiscoveredLog>();
        }

        string logsFolder = GetLogsFolder(eqFolder);
        var found = new Dictionary<string, DiscoveredLog>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(logsFolder))
            {
                return Array.Empty<DiscoveredLog>();
            }

            foreach (string file in Directory.EnumerateFiles(logsFolder, "eqlog_*.txt"))
            {
                if (LogFileName.TryParse(file, out string character, out string server))
                {
                    found[file] = new DiscoveredLog(Path.GetFullPath(file), character, server);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<DiscoveredLog>();
        }

        var result = found.Values.ToList();
        result.Sort(Compare);
        return result;
    }

    /// <summary>Server ascending, then character ascending (both case-insensitive).</summary>
    public static int Compare(DiscoveredLog? a, DiscoveredLog? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        return Compare(a.ServerName, a.CharacterName, b.ServerName, b.CharacterName);
    }

    /// <summary>The same ordering for any (server, character) pair.</summary>
    public static int Compare(string serverA, string characterA, string serverB, string characterB)
    {
        int byServer = StringComparer.OrdinalIgnoreCase.Compare(serverA, serverB);
        return byServer != 0 ? byServer : StringComparer.OrdinalIgnoreCase.Compare(characterA, characterB);
    }
}
