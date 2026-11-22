using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQOverwatch.Core.Maps;

/// <summary>One map file of the Overwatch map pack, as the pack's file list gives it.</summary>
public sealed record MapPackFile(string Name, long Size, string Sha256);

/// <summary>The Overwatch map pack's file list (<c>OverwatchMaps.json</c>).</summary>
public sealed record MapPackManifest(string Version, IReadOnlyList<MapPackFile> Files);

/// <summary>Where a published map pack's file list and zip can be downloaded.</summary>
public sealed record MapPackRelease(string Tag, string ManifestUrl, string ZipUrl);

/// <summary>What a pack file needs on this PC.</summary>
public enum MapFileState
{
    /// <summary>The player's file already matches the pack.</summary>
    Current,

    /// <summary>The player has no such file.</summary>
    Missing,

    /// <summary>The player's file is the one the app installed earlier, so it can be replaced.</summary>
    Update,

    /// <summary>The player's file is not one the app installed (edited, or from elsewhere); it is replaced only with approval.</summary>
    Edited,
}

/// <summary>A pack file and what it needs.</summary>
public sealed record MapFileAudit(MapPackFile File, MapFileState State);

/// <summary>What to do with a map file the player edited.</summary>
public enum MapEditChoice
{
    KeepMine,
    Overwrite,
    Merge,
}

/// <summary>What an install did.</summary>
public sealed record MapInstallResult(int Installed, int Updated, int Current, int Kept, int Overwritten, int Merged, int Skipped);

/// <summary>
/// The checksums of the map files the app installed, so a later update can tell the pack's own files (replaced freely) from
/// ones the player changed since (replaced only with approval). Kept as <c>map-pack.json</c> in the app's data folder.
/// </summary>
public sealed class MapPackRecord
{
    public string Version { get; set; } = string.Empty;

    /// <summary>The release tag the files came from ("maps-2026-11-21"); empty for records saved before it was kept.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>The tag of the pack installed, or empty when none was.</summary>
    public string InstalledTag => Files.Count == 0 ? string.Empty
        : Tag.Length > 0 ? Tag
        : Version.Length > 0 ? MapPackInstaller.TagPrefix + Version
        : string.Empty;

    /// <summary>True when <paramref name="latestTag"/> is a different pack from the one installed (and one was installed).</summary>
    public bool IsOutdated(string latestTag) =>
        InstalledTag.Length > 0 && !string.Equals(InstalledTag, latestTag, StringComparison.OrdinalIgnoreCase);

    /// <summary>Installed file name to its SHA-256 (lower-case hex).</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static MapPackRecord Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var record = JsonSerializer.Deserialize<MapPackRecord>(File.ReadAllText(path)) ?? new MapPackRecord();
                record.Files = new Dictionary<string, string>(record.Files, StringComparer.OrdinalIgnoreCase);
                return record;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new MapPackRecord();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Finds, checks and installs the Overwatch map pack into <c>maps\Overwatch</c> of the EverQuest folder. Only files of the pack
/// are written; nothing is deleted and no other map folder is touched. Files the player changed are left alone unless approved,
/// and a backup is taken before one is replaced or merged.
/// </summary>
public sealed class MapPackInstaller
{
    public const string PackFolderName = "Overwatch";
    public const string ManifestAsset = "OverwatchMaps.json";
    public const string ZipAsset = "OverwatchMaps.zip";
    public const string TagPrefix = "maps-";

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly HttpClient _client;
    private readonly string _repository;
    private readonly string _userAgent;

    public MapPackInstaller(HttpClient client, string repository, string userAgent)
    {
        _client = client;
        _repository = repository;
        _userAgent = userAgent;
    }

    /// <summary>The newest published map pack, or null when none is published.</summary>
    public async Task<MapPackRelease?> FindLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request($"https://api.github.com/repos/{_repository}/releases?per_page=50");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseReleases(json, _repository);
    }

    /// <summary>Picks the newest <c>maps-</c> release that has both assets from GitHub's release list.</summary>
    public static MapPackRelease? ParseReleases(string json, string repository)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        MapPackRelease? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            string tag = release.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
            if (!tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)
                || (release.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True))
            {
                continue;
            }

            string manifest = string.Empty, zip = string.Empty;
            if (release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                    string url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
                    if (!IsReleaseUrl(url, repository))
                    {
                        continue;
                    }

                    if (name.Equals(ManifestAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        manifest = url;
                    }
                    else if (name.Equals(ZipAsset, StringComparison.OrdinalIgnoreCase))
                    {
                        zip = url;
                    }
                }
            }

            if (manifest.Length == 0 || zip.Length == 0)
            {
                continue;
            }

            DateTime published = release.TryGetProperty("published_at", out JsonElement p) && p.ValueKind == JsonValueKind.String
                && p.TryGetDateTime(out DateTime when) ? when : DateTime.MinValue;
            if (best is null || published > bestTime)
            {
                best = new MapPackRelease(tag, manifest, zip);
                bestTime = published;
            }
        }

        return best;
    }

    public static bool IsReleaseUrl(string url, string repository) =>
        url.StartsWith($"https://github.com/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    public async Task<MapPackManifest> DownloadManifestAsync(MapPackRelease release, CancellationToken cancellationToken = default)
    {
        using var request = Request(release.ManifestUrl);
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseManifest(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads a file list; entries whose name is not a plain <c>.txt</c> file name are dropped.</summary>
    public static MapPackManifest ParseManifest(string json)
    {
        var data = JsonSerializer.Deserialize<ManifestJson>(json) ?? new ManifestJson();
        var files = (data.Files ?? new List<ManifestFileJson>())
            .Where(f => IsSafeName(f.Name) && f.Sha256 is { Length: 64 })
            .Select(f => new MapPackFile(f.Name!, f.Size, f.Sha256!.ToLowerInvariant()))
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        return new MapPackManifest(data.Version ?? string.Empty, files);
    }

    /// <summary>Downloads the pack's zip to <paramref name="path"/>, reporting progress from 0 to 1.</summary>
    public async Task DownloadZipAsync(MapPackRelease release, string path, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        using var request = Request(release.ZipUrl);
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream target = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;
            if (total > 0)
            {
                progress?.Report(Math.Min(1.0, (double)done / total));
            }
        }
    }

    /// <summary>Compares the player's <c>maps\Overwatch</c> folder with the pack.</summary>
    public static IReadOnlyList<MapFileAudit> Audit(string packFolder, MapPackManifest manifest, MapPackRecord record)
    {
        var audits = new List<MapFileAudit>(manifest.Files.Count);
        foreach (MapPackFile file in manifest.Files)
        {
            string path = Path.Combine(packFolder, file.Name);
            if (!File.Exists(path))
            {
                audits.Add(new MapFileAudit(file, MapFileState.Missing));
                continue;
            }

            string hash = HashFile(path);
            MapFileState state = hash == file.Sha256 ? MapFileState.Current
                : record.Files.TryGetValue(file.Name, out string? installed) && installed == hash ? MapFileState.Update
                : MapFileState.Edited;
            audits.Add(new MapFileAudit(file, state));
        }

        return audits;
    }

    /// <summary>
    /// Installs the files that need it from the downloaded zip. Edited files follow <paramref name="choices"/> (missing means
    /// keep mine); each replaced or merged file is first copied to <paramref name="backupFolder"/>. Every file written is
    /// checked against the pack's checksum first, and the record is updated so the next update knows what it installed.
    /// A merged file is left out of the record, so it keeps counting as the player's own.
    /// </summary>
    public static MapInstallResult Install(
        string zipPath,
        string packFolder,
        MapPackManifest manifest,
        IReadOnlyList<MapFileAudit> audits,
        IReadOnlyDictionary<string, MapEditChoice> choices,
        MapPackRecord record,
        string backupFolder)
    {
        Directory.CreateDirectory(packFolder);
        int installed = 0, updated = 0, current = 0, kept = 0, overwritten = 0, merged = 0, skipped = 0;
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        foreach (MapFileAudit audit in audits)
        {
            MapPackFile file = audit.File;
            string path = Path.Combine(packFolder, file.Name);
            if (audit.State == MapFileState.Current)
            {
                record.Files[file.Name] = file.Sha256;
                current++;
                continue;
            }

            MapEditChoice choice = audit.State == MapFileState.Edited
                ? choices.GetValueOrDefault(file.Name, MapEditChoice.KeepMine)
                : MapEditChoice.Overwrite;
            if (choice == MapEditChoice.KeepMine)
            {
                kept++;
                continue;
            }

            byte[]? bytes = ReadEntry(zip, file);
            if (bytes is null)
            {
                skipped++;
                continue;
            }

            if (audit.State == MapFileState.Edited)
            {
                Directory.CreateDirectory(backupFolder);
                File.Copy(path, Path.Combine(backupFolder, file.Name), overwrite: true);
            }

            if (choice == MapEditChoice.Merge)
            {
                string mergedText = Merge(Latin1.GetString(bytes), File.ReadAllText(path, Latin1));
                WriteAtomic(path, Latin1.GetBytes(mergedText));
                record.Files.Remove(file.Name);
                merged++;
                continue;
            }

            WriteAtomic(path, bytes);
            record.Files[file.Name] = file.Sha256;
            switch (audit.State)
            {
                case MapFileState.Missing: installed++; break;
                case MapFileState.Update: updated++; break;
                default: overwritten++; break;
            }
        }

        record.Version = manifest.Version;
        return new MapInstallResult(installed, updated, current, kept, overwritten, merged, skipped);
    }

    /// <summary>The pack's new file plus the player's label lines (<c>P ...</c>) the new file does not already have.</summary>
    public static string Merge(string packText, string mineText)
    {
        string newline = packText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var packLines = new HashSet<string>(
            packText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0), StringComparer.Ordinal);
        var added = mineText.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("P ", StringComparison.OrdinalIgnoreCase) && !packLines.Contains(l))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (added.Count == 0)
        {
            return packText;
        }

        var builder = new StringBuilder(packText);
        if (packText.Length > 0 && !packText.EndsWith('\n'))
        {
            builder.Append(newline);
        }

        foreach (string line in added)
        {
            builder.Append(line).Append(newline);
        }

        return builder.ToString();
    }

    public static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSafeName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
        && name != "." && !name.Contains("..", StringComparison.Ordinal);

    private static byte[]? ReadEntry(ZipArchive zip, MapPackFile file)
    {
        ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(file.Name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using Stream stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        return Hash(bytes) == file.Sha256 ? bytes : null;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = path + ".owtmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        return request;
    }

    private sealed class ManifestJson
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("files")]
        public List<ManifestFileJson>? Files { get; set; }
    }

    private sealed class ManifestFileJson
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }
    }
}
