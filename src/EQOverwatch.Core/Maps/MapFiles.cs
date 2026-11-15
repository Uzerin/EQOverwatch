using System.Text;

namespace EQOverwatch.Core.Maps;

/// <summary>A folder of map files: a pack under <c>maps</c> (such as "Dark Brewall"), or the <c>maps</c> folder itself.</summary>
public sealed record MapPack(string Name, string Folder)
{
    /// <summary>The name the <c>maps</c> folder itself is listed under.</summary>
    public const string RootLabel = "maps folder (no pack)";

    public bool IsRoot => Name.Length == 0;

    public string Label => IsRoot ? RootLabel : Name;
}

/// <summary>Reads and writes the map files in the EverQuest folder.</summary>
public static class MapFiles
{
    /// <summary>The base map is layer 0 (<c>zone.txt</c>); layers 1 to 3 are <c>zone_1.txt</c> to <c>zone_3.txt</c>.</summary>
    public const int LayerCount = 4;

    private static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Every map pack in the EQ folder's <c>maps</c> folder (folders holding map files), then the <c>maps</c> folder itself if it holds any.</summary>
    public static IReadOnlyList<MapPack> Packs(string? eqFolder)
    {
        var packs = new List<MapPack>();
        if (string.IsNullOrWhiteSpace(eqFolder))
        {
            return packs;
        }

        string maps = Path.Combine(eqFolder, "maps");
        if (!Directory.Exists(maps))
        {
            return packs;
        }

        try
        {
            foreach (string folder in Directory.EnumerateDirectories(maps).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (Directory.EnumerateFiles(folder, "*.txt").Any())
                {
                    packs.Add(new MapPack(Path.GetFileName(folder), folder));
                }
            }

            if (Directory.EnumerateFiles(maps, "*.txt").Any())
            {
                packs.Add(new MapPack(string.Empty, maps));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return packs;
    }

    /// <summary>The short names of the zones that have a map file (any layer) in <paramref name="folder"/>, lower case.</summary>
    public static IReadOnlyList<string> Zones(string folder)
    {
        var zones = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.txt"))
            {
                string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (name.Length > 2 && name[^2] == '_' && name[^1] is >= '1' and <= '3')
                {
                    name = name[..^2];
                }

                if (name.Length > 0 && !name.Contains(' ', StringComparison.Ordinal))
                {
                    zones.Add(name);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return zones.ToList();
    }

    public static string FileName(string zone, int layer) => layer == 0 ? $"{zone}.txt" : $"{zone}_{layer}.txt";

    public static string LayerPath(string folder, string zone, int layer) => Path.Combine(folder, FileName(zone, layer));

    /// <summary>The zone's base map and its three layers; a layer with no file is empty.</summary>
    public static MapLayer[] Load(string folder, string zone)
    {
        var layers = new MapLayer[LayerCount];
        for (int layer = 0; layer < LayerCount; layer++)
        {
            string path = LayerPath(folder, zone, layer);
            layers[layer] = File.Exists(path) ? MapLayer.Parse(File.ReadLines(path)) : MapLayer.Empty;
        }

        return layers;
    }

    /// <summary>
    /// Writes one layer's file. The first time a file the app did not write is changed, a copy is kept next to it as
    /// <c>.bak</c>. Returns the path written.
    /// </summary>
    public static string Save(string folder, string zone, int layer, MapLayer content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string path = LayerPath(folder, zone, layer);
        string backup = path + ".bak";
        if (File.Exists(path) && !File.Exists(backup))
        {
            File.Copy(path, backup);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, content.ToFileText(), FileEncoding);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// The map pack a character uses in game: the <c>MapSkin</c> line of its newest <c>UI_Name_server*.ini</c> in the EQ folder,
    /// or null when it has none.
    /// </summary>
    public static string? CharacterPack(string? eqFolder, string character, string server)
    {
        if (string.IsNullOrWhiteSpace(eqFolder) || string.IsNullOrWhiteSpace(character) || !Directory.Exists(eqFolder))
        {
            return null;
        }

        try
        {
            string pattern = string.IsNullOrEmpty(server) ? $"UI_{character}_*.ini" : $"UI_{character}_{server}*.ini";
            FileInfo? newest = new DirectoryInfo(eqFolder).EnumerateFiles(pattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
            {
                return null;
            }

            foreach (string line in File.ReadLines(newest.FullName))
            {
                if (line.StartsWith("MapSkin=", StringComparison.OrdinalIgnoreCase))
                {
                    string skin = line["MapSkin=".Length..].Trim();
                    return skin.Length == 0 ? null : skin;
                }
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
}
