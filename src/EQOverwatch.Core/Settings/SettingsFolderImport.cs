namespace EQOverwatch.Core.Settings;

/// <summary>Copies another edition's settings folder the first time this edition starts, so it opens with the same setup.</summary>
public static class SettingsFolderImport
{
    /// <summary>
    /// Copies every file under <paramref name="source"/> into <paramref name="target"/> when the target folder does not exist yet.
    /// Files that cannot be read are skipped. Returns true when a copy was made.
    /// </summary>
    public static bool CopyIfMissing(string source, string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (Directory.Exists(target) || !Directory.Exists(source))
        {
            return false;
        }

        string staging = target + ".importing";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string copy = Path.Combine(staging, Path.GetRelativePath(source, file));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a file held open by the other edition is skipped
            }
        }

        Directory.CreateDirectory(staging);
        Directory.Move(staging, target); // all at once, so a copy cut short is redone next time
        return true;
    }
}
