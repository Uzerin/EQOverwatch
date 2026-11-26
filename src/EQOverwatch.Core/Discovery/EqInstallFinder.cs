namespace EQOverwatch.Core.Discovery;

/// <summary>An EverQuest folder found on this PC, with how many characters have a log in it.</summary>
public sealed record EqInstall(string Folder, int CharacterCount, int LogCount, bool HasGame, DateTime LastPlayedUtc = default)
{
    /// <summary>Short description shown under the folder in the first-run window.</summary>
    public string Description => CharacterCount switch
    {
        0 when HasGame => "No logs yet (type /log on in game)",
        0 => "Logs folder with no character logs",
        1 => "Logs found for 1 character",
        _ => $"Logs found for {CharacterCount} characters",
    };
}

/// <summary>Finds EverQuest installs: well-known folders, folders Windows lists as installed, and folders near each drive's root.</summary>
public static class EqInstallFinder
{
    public const string GameExeName = "eqgame.exe";

    /// <summary>Folders on a drive root that never hold a game and are slow or protected to look through.</summary>
    private static readonly HashSet<string> SkippedRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "ProgramData", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "$WinREAgent", "Config.Msi",
        "OneDriveTemp", "Intel", "AMD", "NVIDIA", "MSOCache", "Documents and Settings",
    };

    /// <summary>The usual install folders of the Daybreak launcher, older Sony installs and Steam.</summary>
    public static IEnumerable<string> WellKnownFolders(string? publicFolder, string? programFiles, string? programFilesX86)
    {
        if (!string.IsNullOrWhiteSpace(publicFolder))
        {
            yield return Path.Combine(publicFolder, "Daybreak Game Company", "Installed Games", "EverQuest");
            yield return Path.Combine(publicFolder, "Sony Online Entertainment", "Installed Games", "EverQuest");
        }

        foreach (string? root in new[] { programFilesX86, programFiles })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            yield return Path.Combine(root, "Steam", "steamapps", "common", "EverQuest");
            yield return Path.Combine(root, "Sony", "EverQuest");
            yield return Path.Combine(root, "EverQuest");
            yield return Path.Combine(root, "Daybreak Game Company", "Installed Games", "EverQuest");
        }
    }

    /// <summary>Words in a folder name that make it worth looking deeper than <see cref="FoldersUnder"/>'s first two levels.</summary>
    private static readonly string[] GameFolderWords = { "game", "daybreak", "sony", "everquest", "steam", "eq" };

    /// <summary>True for a folder name that may lead to a game install ("Games", "Daybreak Game Company", "EQ", "SteamLibrary").</summary>
    public static bool IsGameFolderName(string name) =>
        GameFolderWords.Any(word => word == "eq"
            ? name.Equals("eq", StringComparison.OrdinalIgnoreCase) || name.StartsWith("eq ", StringComparison.OrdinalIgnoreCase)
              || name.StartsWith("eq_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("eq-", StringComparison.OrdinalIgnoreCase)
            : name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Folders up to <paramref name="depth"/> levels under a drive root (system folders skipped), then on down to
    /// <paramref name="deepDepth"/> levels through folders whose names look like games (<see cref="IsGameFolderName"/>), so
    /// "D:\Games\Daybreak\EverQuest" is found too. Unreadable folders are passed over.
    /// </summary>
    public static IEnumerable<string> FoldersUnder(string root, int depth = 2, int deepDepth = 4)
    {
        var level = new List<string> { root };
        for (int i = 0; i < Math.Max(depth, deepDepth); i++)
        {
            var next = new List<string>();
            foreach (string folder in level)
            {
                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(folder).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    continue;
                }

                foreach (string child in children)
                {
                    string name = Path.GetFileName(child);
                    if (i == 0 && SkippedRootFolders.Contains(name))
                    {
                        continue;
                    }

                    if (i >= depth && !IsGameFolderName(name))
                    {
                        continue;
                    }

                    next.Add(child);
                    yield return child;
                }
            }

            level = next;
        }
    }

    /// <summary>
    /// The EverQuest folder of each Steam library listed in Steam's <c>libraryfolders.vdf</c> text (lines like
    /// <c>"path"		"D:\SteamLibrary"</c>).
    /// </summary>
    public static IEnumerable<string> SteamLibraryFolders(string? libraryFoldersVdf)
    {
        if (string.IsNullOrEmpty(libraryFoldersVdf))
        {
            yield break;
        }

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     libraryFoldersVdf, @"""path""\s+""(?<path>[^""]+)""",System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            string library = match.Groups["path"].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
            yield return Path.Combine(library, "steamapps", "common", "EverQuest");
        }
    }

    /// <summary>
    /// The candidates that are EverQuest folders (they hold <c>eqgame.exe</c> or a Logs folder with character logs), each once, the one
    /// played most recently (newest log write) first.
    /// </summary>
    public static IReadOnlyList<EqInstall> Find(IEnumerable<string?> candidates)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<EqInstall>();
        foreach (string? candidate in candidates)
        {
            string folder = EqDirectoryScanner.Normalize(candidate);
            if (folder.Length == 0)
            {
                continue;
            }

            try
            {
                folder = Path.GetFullPath(folder);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!seen.Add(folder) || Inspect(folder) is not { } install)
            {
                continue;
            }

            found.Add(install);
        }

        return found
            .OrderByDescending(i => i.LastPlayedUtc)
            .ThenByDescending(i => i.CharacterCount)
            .ThenByDescending(i => i.HasGame)
            .ThenBy(i => i.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The install in <paramref name="folder"/>, or null when it does not look like an EverQuest folder.</summary>
    public static EqInstall? Inspect(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return null;
            }

            bool hasGame = File.Exists(Path.Combine(folder, GameExeName));
            IReadOnlyList<DiscoveredLog> logs = EqDirectoryScanner.Discover(folder);
            if (!hasGame && logs.Count == 0)
            {
                return null;
            }

            int characters = logs.Select(l => l.CharacterName + "|" + l.ServerName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            DateTime lastPlayed = logs.Count == 0 ? default : logs.Max(l => File.GetLastWriteTimeUtc(l.FilePath));
            return new EqInstall(folder, characters, logs.Count, hasGame, lastPlayed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
