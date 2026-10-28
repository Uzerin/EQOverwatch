using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Parsing;

/// <summary>Extracts character and server from <c>eqlog_&lt;Character&gt;_&lt;Server&gt;.txt</c>.</summary>
public static partial class LogFileName
{
    // Character names never contain '_', so the first underscore after the prefix ends the name.
    [GeneratedRegex(@"^eqlog_(?<character>[^_]+)_(?<server>.+)\.txt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNameRegex();

    public static bool TryParse(string pathOrFileName, out string character, out string server)
    {
        character = string.Empty;
        server = string.Empty;

        string fileName = Path.GetFileName(pathOrFileName);
        Match match = FileNameRegex().Match(fileName);
        if (!match.Success)
        {
            return false;
        }

        character = match.Groups["character"].Value;
        server = match.Groups["server"].Value;
        return true;
    }

    /// <summary>Character name from the file name, or the bare file name when it doesn't follow the EQ convention.</summary>
    public static string GetCharacterName(string pathOrFileName) =>
        TryParse(pathOrFileName, out string character, out _)
            ? character
            : Path.GetFileNameWithoutExtension(pathOrFileName);
}
