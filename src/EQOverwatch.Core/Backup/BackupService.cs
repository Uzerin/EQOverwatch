using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace EQOverwatch.Core.Backup;

/// <summary>
/// Saves the app's data folder (settings, triggers, loot, attendance, character data, boss history) into one zip file and puts it
/// back. A restore is staged first and applied the next time the app starts, because the running app writes its settings again
/// when it closes and would otherwise undo it. The pairing code is never written to a backup.
/// </summary>
public static class BackupService
{
    public const string PendingFolder = "restore-pending";
    public const string InfoFile = "backup-info.txt";
    private const string SettingsFile = "settings.json";

    private static readonly string[] SkippedFolders = { "remote", PendingFolder };

    /// <summary>Writes the zip. Returns how many files it holds.</summary>
    public static int Export(string dataDirectory, string zipPath, string appVersion)
    {
        if (!Directory.Exists(dataDirectory))
        {
            throw new DirectoryNotFoundException("The app has no saved data to back up yet.");
        }

        string temp = zipPath + ".tmp";
        int count = 0;
        using (FileStream output = File.Create(temp))
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(dataDirectory, file).Replace('\\', '/');
                if (IsSkipped(relative))
                {
                    continue;
                }

                ZipArchiveEntry entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                using Stream target = entry.Open();
                try
                {
                    using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (relative.Equals(SettingsFile, StringComparison.OrdinalIgnoreCase))
                    {
                        using var reader = new StreamReader(source, Encoding.UTF8);
                        byte[] scrubbed = Encoding.UTF8.GetBytes(ScrubSecrets(reader.ReadToEnd()));
                        target.Write(scrubbed, 0, scrubbed.Length);
                    }
                    else
                    {
                        source.CopyTo(target);
                    }
                }
                catch (IOException)
                {
                    continue; // a file the app is writing right now: the backup is still good without it
                }

                count++;
            }

            ZipArchiveEntry info = zip.CreateEntry(InfoFile);
            using var writer = new StreamWriter(info.Open(), Encoding.UTF8);
            writer.WriteLine($"EQ Overwatch {appVersion} backup, made {DateTime.Now:yyyy-MM-dd HH:mm}");
        }

        File.Move(temp, zipPath, overwrite: true);
        return count;
    }

    /// <summary>Checks the zip and unpacks it into the pending-restore folder. Returns how many files are waiting.</summary>
    public static int StageRestore(string zipPath, string dataDirectory)
    {
        string pending = Path.Combine(dataDirectory, PendingFolder);
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        List<ZipArchiveEntry> entries = zip.Entries
            .Where(e => e.Name.Length > 0 && !e.FullName.Equals(InfoFile, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!entries.Any(e => e.FullName.Equals(SettingsFile, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("This file is not an EQ Overwatch backup (it has no settings in it).");
        }

        if (Directory.Exists(pending))
        {
            Directory.Delete(pending, recursive: true);
        }

        Directory.CreateDirectory(pending);
        string root = Path.GetFullPath(pending) + Path.DirectorySeparatorChar;
        int count = 0;
        foreach (ZipArchiveEntry entry in entries)
        {
            string destination = Path.GetFullPath(Path.Combine(pending, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) || IsSkipped(entry.FullName))
            {
                continue; // a name that points outside the folder is never written
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>True when a staged restore is waiting to be applied.</summary>
    public static bool HasPendingRestore(string dataDirectory) => Directory.Exists(Path.Combine(dataDirectory, PendingFolder));

    /// <summary>
    /// Moves the staged files into the data folder, replacing what is there. Run before anything is loaded. The current pairing
    /// code is kept, since backups do not carry one. Returns how many files were restored.
    /// </summary>
    public static int ApplyPendingRestore(string dataDirectory)
    {
        string pending = Path.Combine(dataDirectory, PendingFolder);
        if (!Directory.Exists(pending))
        {
            return 0;
        }

        string? currentCode = ReadSyncCode(Path.Combine(dataDirectory, SettingsFile));
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(pending, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(pending, file);
            string destination = Path.Combine(dataDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file, destination, overwrite: true);
            count++;
        }

        if (!string.IsNullOrEmpty(currentCode))
        {
            RestoreSyncCode(Path.Combine(dataDirectory, SettingsFile), currentCode);
        }

        Directory.Delete(pending, recursive: true);
        return count;
    }

    /// <summary>The settings JSON with the pairing code blanked.</summary>
    public static string ScrubSecrets(string settingsJson) => SetSyncCode(settingsJson, string.Empty) ?? settingsJson;

    private static bool IsSkipped(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        return normalized.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || SkippedFolders.Any(f => normalized.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadSyncCode(string settingsPath)
    {
        try
        {
            if (File.Exists(settingsPath) && JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject obj)
            {
                foreach (KeyValuePair<string, JsonNode?> pair in obj)
                {
                    if (pair.Key.Equals("SyncCode", StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value?.GetValue<string>();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }

        return null;
    }

    private static void RestoreSyncCode(string settingsPath, string code)
    {
        try
        {
            string? updated = SetSyncCode(File.ReadAllText(settingsPath), code);
            if (updated is not null)
            {
                File.WriteAllText(settingsPath, updated);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string? SetSyncCode(string json, string code)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj)
            {
                return null;
            }

            string? name = obj.Select(p => p.Key).FirstOrDefault(k => k.Equals("SyncCode", StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return json;
            }

            obj[name] = code;
            return obj.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
