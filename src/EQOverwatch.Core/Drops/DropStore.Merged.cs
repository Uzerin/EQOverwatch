using System.IO.Compression;
using System.Text.Json;

namespace EQOverwatch.Core.Drops;

/// <summary>When a server's merged shared table was made, and how many players' drops are in it.</summary>
public sealed record MergedDropsInfo(long Updated, int Players);

// The merged shared table: the service merges everyone's uploads once a day (10:00 UTC) and the app downloads one file per server. The
// Drop Table's "You and all players" shows those counts plus this PC's own kills and drops since the upload the merge holds.
public sealed partial class DropStore
{
    private const string MergedFormat = "EQOverwatchDropsMerged";

    /// <summary>Each upload repeats this much of the last one, so a drop whose loot line came late is not missed (the service ignores repeats).</summary>
    public const long UploadOverlapSeconds = 600;

    private const int MaxUploadsKept = 10;

    private readonly Dictionary<int, Merged> _merged = new();
    private readonly List<(int Server, long At, long Through)> _uploads = new();

    private sealed class Merged
    {
        public long Updated { get; init; }

        public int Players { get; init; }

        public byte[] Gzip { get; init; } = Array.Empty<byte>();

        public Dictionary<(int Zone, int Mob), int> Kills { get; } = new();

        public Dictionary<(int Zone, int Mob, int Item), int> Drops { get; } = new();
    }

    /// <summary>The merged file's contents: names, then [zone, mob, count, last seen] kills and [zone, mob, item, count, last seen] drops.</summary>
    public sealed class MergedFile
    {
        public string Format { get; set; } = string.Empty;

        public int Version { get; set; }

        public string Server { get; set; } = string.Empty;

        public long Updated { get; set; }

        public int Players { get; set; }

        public List<string> Zones { get; set; } = new();

        public List<string> Mobs { get; set; } = new();

        public List<string> Items { get; set; } = new();

        public List<long[]> Kills { get; set; } = new();

        public List<long[]> Drops { get; set; } = new();
    }

    /// <summary>The merged table held for a server, or null.</summary>
    public MergedDropsInfo? MergedInfo(string server)
    {
        lock (_gate)
        {
            int serverId = _names.FindServer(server);
            return serverId >= 0 && _merged.TryGetValue(serverId, out Merged? m) ? new MergedDropsInfo(m.Updated, m.Players) : null;
        }
    }

    /// <summary>
    /// Puts in a server's merged table (replacing the one held). Throws <see cref="InvalidDataException"/> for a damaged file.
    /// Old per-player shared files are taken out: the merged table already has them.
    /// </summary>
    public void ImportMerged(string server, long updated, int players, byte[] gzipped)
    {
        MergedFile file = ParseMerged(gzipped) ?? throw new InvalidDataException("That isn't a merged drops file.");
        lock (_gate)
        {
            LoadMergedLocked(server, updated, players, gzipped, file);
            for (int i = 1; i < _sources.Count; i++)
            {
                if (_sources[i].Shared && string.Equals(_sources[i].Server, server, StringComparison.OrdinalIgnoreCase))
                {
                    int source = i;
                    _events.RemoveAll(e => e.Source == source);
                    _sources[i] = new Source(string.Empty, string.Empty, DateTime.MinValue);
                }
            }

            Changed();
        }
    }

    /// <summary>Notes an upload to the shared table: when, and the newest event time in it.</summary>
    public void RecordSharedUpload(string server, DateTime atUtc, long through)
    {
        lock (_gate)
        {
            int serverId = _names.Server(server);
            _uploads.Add((serverId, ToUnix(atUtc), through));
            List<(int Server, long At, long Through)> ours = _uploads.Where(u => u.Server == serverId).ToList();
            foreach ((int, long, long) old in ours.Take(Math.Max(0, ours.Count - MaxUploadsKept)))
            {
                _uploads.Remove(old);
            }

            Changed();
        }
    }

    /// <summary>When this PC last uploaded to a server's shared table, or null.</summary>
    public DateTime? LastSharedUpload(string server)
    {
        lock (_gate)
        {
            int serverId = _names.FindServer(server);
            return serverId >= 0 && LastUploadLocked(serverId) is { } last ? DateTimeOffset.FromUnixTimeSeconds(last.At).UtcDateTime : null;
        }
    }

    /// <summary>Forgets this PC's uploads (sharing switched off and its drops taken down), so sharing again sends everything.</summary>
    public void ForgetSharedUploads()
    {
        lock (_gate)
        {
            if (_uploads.Count > 0)
            {
                _uploads.Clear();
                Changed();
            }
        }
    }

    private (int Server, long At, long Through)? LastUploadLocked(int serverId)
    {
        (int Server, long At, long Through)? last = null;
        foreach ((int Server, long At, long Through) u in _uploads)
        {
            if (u.Server == serverId && (last is null || u.At > last.Value.At))
            {
                last = u;
            }
        }

        return last;
    }

    /// <summary>
    /// This PC's events newer than this are not in the merged table yet: the newest event of the last upload made before the merge.
    /// With no such upload, none of this PC's events are in it.
    /// </summary>
    private long OwnSinceLocked(int serverId, Merged merged)
    {
        long since = long.MinValue;
        long at = long.MinValue;
        foreach ((int server, long uploadedAt, long through) in _uploads)
        {
            if (server == serverId && uploadedAt <= merged.Updated && uploadedAt > at)
            {
                at = uploadedAt;
                since = through;
            }
        }

        return since;
    }

    private static MergedFile? ParseMerged(byte[] gzipped)
    {
        try
        {
            using var input = new GZipStream(new MemoryStream(gzipped), CompressionMode.Decompress);
            using var limited = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (limited.Length + read > MaxSharedBytes)
                {
                    throw new InvalidDataException("That merged file is too large.");
                }

                limited.Write(chunk, 0, read);
            }

            limited.Position = 0;
            MergedFile? file = JsonSerializer.Deserialize<MergedFile>(limited);
            return file is not null && file.Format == MergedFormat && file.Kills.Count + file.Drops.Count <= MaxImportEvents ? file : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private void LoadMergedLocked(string server, long updated, int players, byte[] gzipped, MergedFile file)
    {
        int serverId = _names.Server(server);
        var merged = new Merged { Updated = updated, Players = players, Gzip = gzipped };
        foreach (long[] k in file.Kills)
        {
            if (k.Length >= 3 && Name(file.Zones, k[0]) is { } zone && Name(file.Mobs, k[1]) is { Length: > 0 } mob && k[2] > 0)
            {
                (int, int) key = (_names.Zone(zone), _names.Mob(mob));
                merged.Kills[key] = merged.Kills.GetValueOrDefault(key) + (int)Math.Min(k[2], int.MaxValue / 2);
            }
        }

        foreach (long[] d in file.Drops)
        {
            if (d.Length >= 4 && Name(file.Zones, d[0]) is { } zone && Name(file.Mobs, d[1]) is { Length: > 0 } mob
                && Name(file.Items, d[2]) is { Length: > 0 } item && d[3] > 0)
            {
                (int, int, int) key = (_names.Zone(zone), _names.Mob(mob), _names.Item(item));
                merged.Drops[key] = merged.Drops.GetValueOrDefault(key) + (int)Math.Min(d[3], int.MaxValue / 2);
            }
        }

        _merged[serverId] = merged;

        static string? Name(List<string> names, long index) => index >= 0 && index < names.Count ? names[(int)index] : null;
    }
}
