using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Characters;

/// <summary>What adding the Overwatch Data socials did to one character file.</summary>
public enum SocialsOutcome
{
    Added,
    AlreadyThere,
    NoRoom,
    Failed,
}

/// <summary>One character file and what happened to it; <see cref="Slots"/> reads like "page 1 button 6, page 1 button 8".</summary>
public sealed record SocialsResult(string File, SocialsOutcome Outcome, string Slots = "");

/// <summary>
/// Adds two socials, "Overwatch Data1" and "Overwatch Data2", that write the files the app reads (<c>/outputfile ...</c>) to a
/// character's EQ settings files (<c>Name_server.ini</c> and its class copies such as <c>Name_server_ROG.ini</c>). They go into the
/// first two empty social slots (page 1 button 1 to page 10 button 12): a slot with any key at all is never touched, and nothing is
/// put on a hotbar. EQ rewrites these files when the character camps, so this is only done while it is logged out.
/// </summary>
public static partial class OverwatchSocials
{
    public const string FirstName = "Overwatch Data1";
    public const string SecondName = "Overwatch Data2";
    public const int Pages = 10;
    public const int ButtonsPerPage = 12;

    public static readonly IReadOnlyList<string> FirstLines = new[]
    {
        "/outputfile inventory", "/outputfile spellbook", "/outputfile missingspells", "/outputfile achievements", "/outputfile faction",
    };

    public static readonly IReadOnlyList<string> SecondLines = new[]
    {
        "/outputfile recipes all", "/outputfile guild", "/outputfile guildbank", "/outputfile raid", "/played",
    };

    [GeneratedRegex(@"^Page(?<p>\d+)Button(?<b>\d+)(?:Name|Color|Line\d+)\s*=\s*(?<v>.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SocialKey();

    /// <summary>The character's settings files in the EQ folder: <c>Name_server.ini</c> and <c>Name_server_XXX.ini</c>.</summary>
    public static IReadOnlyList<string> FilesFor(string eqFolder, string character, string server)
    {
        if (string.IsNullOrWhiteSpace(eqFolder) || !Directory.Exists(eqFolder) || character.Length == 0 || server.Length == 0)
        {
            return Array.Empty<string>();
        }

        string stem = character + "_" + server;
        return Directory.EnumerateFiles(eqFolder, stem + "*.ini")
            .Where(f =>
            {
                string name = Path.GetFileNameWithoutExtension(f);
                return name.Equals(stem, StringComparison.OrdinalIgnoreCase)
                       || (name.Length == stem.Length + 4 && name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) && name[^3..].All(char.IsLetter));
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Adds the two socials to each file (a copy of each changed file is kept as <c>.ini.bak</c>).</summary>
    public static IReadOnlyList<SocialsResult> AddTo(IEnumerable<string> files)
    {
        var results = new List<SocialsResult>();
        foreach (string file in files)
        {
            try
            {
                string text = File.ReadAllText(file, Encoding.Latin1);
                (string? updated, SocialsResult result) = Add(text, Path.GetFileName(file));
                if (updated is not null)
                {
                    File.Copy(file, file + ".bak", overwrite: true);
                    File.WriteAllText(file, updated, Encoding.Latin1);
                }

                results.Add(result);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new SocialsResult(Path.GetFileName(file), SocialsOutcome.Failed));
            }
        }

        return results;
    }

    /// <summary>The file's text with the two socials added, or null (with the reason) when it is left as it is.</summary>
    public static (string? Text, SocialsResult Result) Add(string text, string fileName)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        int start = lines.FindIndex(l => l.Trim().Equals("[Socials]", StringComparison.OrdinalIgnoreCase));
        int end = lines.Count;
        var used = new HashSet<(int, int)>();
        if (start >= 0)
        {
            for (int i = start + 1; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith('['))
                {
                    end = i;
                    break;
                }

                if (line.StartsWith("Page", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("Name", StringComparison.OrdinalIgnoreCase)
                    && (line.EndsWith("=" + FirstName, StringComparison.OrdinalIgnoreCase) || line.EndsWith("=" + SecondName, StringComparison.OrdinalIgnoreCase)))
                {
                    return (null, new SocialsResult(fileName, SocialsOutcome.AlreadyThere));
                }

                if (SocialKey().Match(line) is { Success: true } key) // any key at all marks the slot as taken
                {
                    used.Add((int.Parse(key.Groups["p"].Value, CultureInfo.InvariantCulture), int.Parse(key.Groups["b"].Value, CultureInfo.InvariantCulture)));
                }
            }
        }

        var free = new List<(int Page, int Button)>();
        for (int page = 1; page <= Pages && free.Count < 2; page++)
        {
            for (int button = 1; button <= ButtonsPerPage && free.Count < 2; button++)
            {
                if (!used.Contains((page, button)))
                {
                    free.Add((page, button));
                }
            }
        }

        if (free.Count < 2)
        {
            return (null, new SocialsResult(fileName, SocialsOutcome.NoRoom));
        }

        var added = new List<string>();
        added.AddRange(Social(free[0], FirstName, FirstLines));
        added.AddRange(Social(free[1], SecondName, SecondLines));
        if (start < 0)
        {
            lines.Add("[Socials]");
            lines.AddRange(added);
        }
        else
        {
            // After the section's last non-blank line, so a blank line before the next section stays where it was.
            int at = end;
            while (at > start + 1 && lines[at - 1].Trim().Length == 0)
            {
                at--;
            }

            lines.InsertRange(at, added);
        }

        string slots = string.Join(", ", free.Select(f => $"page {f.Page.ToString(CultureInfo.InvariantCulture)} button {f.Button.ToString(CultureInfo.InvariantCulture)}"));
        return (string.Join(newline, lines) + newline, new SocialsResult(fileName, SocialsOutcome.Added, slots));
    }

    private static IEnumerable<string> Social((int Page, int Button) slot, string name, IReadOnlyList<string> commands)
    {
        string key = $"Page{slot.Page.ToString(CultureInfo.InvariantCulture)}Button{slot.Button.ToString(CultureInfo.InvariantCulture)}";
        yield return key + "Name=" + name;
        yield return key + "Color=0";
        for (int i = 0; i < commands.Count; i++)
        {
            yield return key + "Line" + (i + 1).ToString(CultureInfo.InvariantCulture) + "=" + commands[i];
        }
    }
}
