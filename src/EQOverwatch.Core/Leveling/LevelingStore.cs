using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EQOverwatch.Core.Leveling;

/// <summary>A stretch with the server it was played on (kept locally; the server is the upload's address, not part of the stretch).</summary>
public sealed record ServerStretch(string Server, LevelingStretch Stretch);

/// <summary>
/// Your own leveling stretches, read from your logs a little at a time (each log carries on from where the last scan stopped), kept in
/// a JSON file with what has been shared so far. Thread-safe.
/// </summary>
public sealed class LevelingStore
{
    /// <summary>Stretches older than this are dropped (zones and experience change over time).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(730);

    /// <summary>
    /// 2: stretches carry coin and AA points, and levels are kept. 3: the power-leveling rule of 1.0.84 (bursts over 30 and 60 seconds,
    /// with 5 minutes either side). Older files are read again from the logs.
    /// </summary>
    private const int FileVersion = 3;

    private sealed class SavedFile
    {
        public int Version { get; set; }
        public Dictionary<string, List<LevelEvent>> Levels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LevelingScanState> Logs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ServerStretch> Stretches { get; set; } = new();
        public Dictionary<string, long> SharedThrough { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _gate = new();
    private SavedFile _file = new();
    private string? _path;

    public event Action? Changed;

    /// <summary>
    /// True when the file was made under an older rule: the logs are read again, so whatever this install shared must be taken down and
    /// shared again. Cleared with <see cref="ReadAgainHandled"/>.
    /// </summary>
    public bool ReadAgain { get; private set; }

    public void ReadAgainHandled() => ReadAgain = false;

    public void SetStoragePath(string path)
    {
        lock (_gate)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    SavedFile? saved = JsonSerializer.Deserialize<SavedFile>(File.ReadAllText(path));
                    ReadAgain = saved is not null && saved.Version < FileVersion; // shared under the old rule, here or by a copy with the same id
                    if (saved is not null && saved.Version >= FileVersion)
                    {
                        _file = new SavedFile
                        {
                            Version = FileVersion,
                            Levels = new Dictionary<string, List<LevelEvent>>(saved.Levels ?? new(), StringComparer.OrdinalIgnoreCase),
                            Logs = new Dictionary<string, LevelingScanState>(saved.Logs ?? new(), StringComparer.OrdinalIgnoreCase),
                            Stretches = (saved.Stretches ?? new()).Where(s => s?.Stretch?.Zone is not null && !string.IsNullOrEmpty(s.Server)).ToList(),
                            SharedThrough = new Dictionary<string, long>(saved.SharedThrough ?? new(), StringComparer.OrdinalIgnoreCase),
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _file = new SavedFile(); // a damaged file starts empty; the logs are read again
            }
        }
    }

    /// <summary>Reads what is new in each log (<c>Path</c>, <c>Server</c>) and keeps the stretches found. Returns how many were found.</summary>
    public int Scan(IEnumerable<(string Path, string Server)> logs, DateTime nowUtc)
    {
        int found = 0;
        foreach ((string path, string server) in logs)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(server) || !File.Exists(path))
            {
                continue;
            }

            LevelingScanState? state;
            lock (_gate)
            {
                state = _file.Logs.GetValueOrDefault(path);
            }

            try
            {
                (IReadOnlyList<LevelingStretch> stretches, LevelingScanState next, IReadOnlyList<LevelEvent> levels) = LevelingScanner.ScanLogWithLevels(path, state);
                lock (_gate)
                {
                    _file.Logs[path] = next;
                    if (levels.Count > 0)
                    {
                        if (!_file.Levels.TryGetValue(path, out List<LevelEvent>? list))
                        {
                            _file.Levels[path] = list = new List<LevelEvent>();
                        }

                        list.AddRange(levels);
                    }

                    _file.Stretches.AddRange(stretches.Select(s => new ServerStretch(server.ToLowerInvariant(), s)));
                    found += stretches.Count;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // tried again next time
            }
        }

        lock (_gate)
        {
            long oldest = new DateTimeOffset(nowUtc - KeepFor).ToUnixTimeSeconds();
            _file.Stretches.RemoveAll(s => s.Stretch.Start < oldest);
            Save();
        }

        Changed?.Invoke();
        return found;
    }

    /// <summary>The levels a log's character reached, oldest first.</summary>
    public IReadOnlyList<LevelEvent> Levels(string path)
    {
        lock (_gate)
        {
            return _file.Levels.TryGetValue(path, out List<LevelEvent>? list) ? list.OrderBy(l => l.Time).ToList() : Array.Empty<LevelEvent>();
        }
    }

    /// <summary>Your stretches on <paramref name="server"/>.</summary>
    public IReadOnlyList<LevelingStretch> Stretches(string server)
    {
        lock (_gate)
        {
            return _file.Stretches.Where(s => s.Server.Equals(server, StringComparison.OrdinalIgnoreCase)).Select(s => s.Stretch).ToList();
        }
    }

    /// <summary>Every server you have stretches on.</summary>
    public IReadOnlyList<string> Servers()
    {
        lock (_gate)
        {
            return _file.Stretches.Select(s => s.Server).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Stretches on <paramref name="server"/> not shared yet.</summary>
    public IReadOnlyList<LevelingStretch> Unshared(string server)
    {
        lock (_gate)
        {
            long through = _file.SharedThrough.GetValueOrDefault(server);
            return _file.Stretches.Where(s => s.Server.Equals(server, StringComparison.OrdinalIgnoreCase) && s.Stretch.Start > through)
                .Select(s => s.Stretch).ToList();
        }
    }

    /// <summary>Forgets what was shared on <paramref name="server"/>, so everything is shared again next time (after a take-down).</summary>
    public void ForgetShared(string server)
    {
        lock (_gate)
        {
            _file.SharedThrough.Remove(server);
            Save();
        }
    }

    /// <summary>Marks the stretches on <paramref name="server"/> up to <paramref name="through"/> (Unix seconds) as shared.</summary>
    public void MarkShared(string server, long through)
    {
        lock (_gate)
        {
            _file.SharedThrough[server] = Math.Max(_file.SharedThrough.GetValueOrDefault(server), through);
            Save();
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _path + ".tmp";
            _file.Version = FileVersion;
            File.WriteAllText(temp, JsonSerializer.Serialize(_file));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // kept in memory; the next change tries again
        }
    }
}

/// <summary>Everyone's shared leveling on one server: a row per player, zone, band and solo/group, plus every zone seen there.</summary>
public sealed record SharedLeveling(IReadOnlyList<LevelingRow> Rows, IReadOnlyList<string> Zones, int Players);

/// <summary>The shared leveling service (the drops worker's /v1/leveling endpoints).</summary>
public interface ILevelingService
{
    /// <summary>Uploads stretches (no names). False when the service says it was uploaded too recently.</summary>
    Task<bool> UploadAsync(string server, string id, string key, IReadOnlyList<LevelingStretch> stretches, CancellationToken cancellation = default);

    /// <summary>Everyone else's leveling on <paramref name="server"/> (this install's own rows, sent as <paramref name="id"/>, left out).</summary>
    Task<SharedLeveling> DownloadAsync(string server, string id, CancellationToken cancellation = default);

    /// <summary>Takes down everything this install shared for leveling on <paramref name="server"/>.</summary>
    Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default);
}

public sealed class HttpLevelingService : ILevelingService
{
    public const string Format = "EQOverwatchLeveling";

    private readonly HttpClient _http;
    private readonly Uri _base;

    public HttpLevelingService(HttpClient http, string address = Drops.HttpSharedDropsService.DefaultAddress)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _base = new Uri(address.EndsWith('/') ? address : address + "/");
    }

    /// <summary>The upload body: gzip JSON with short keys (zone, level, group, seconds, xp, kills, start).</summary>
    public static byte[] Pack(IReadOnlyList<LevelingStretch> stretches)
    {
        string json = JsonSerializer.Serialize(new
        {
            format = Format,
            version = 2, // 2: made under the power-leveling rule of 1.0.84 (the server refuses older files)
            stretches = stretches.Select(s => new { z = s.Zone, l = s.Level, g = s.Group ? 1 : 0, s = s.Seconds, x = s.Xp, k = s.Kills, t = s.Start, c = s.Copper, a = s.Aa }),
        });
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    public async Task<bool> UploadAsync(string server, string id, string key, IReadOnlyList<LevelingStretch> stretches, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_base, $"v1/leveling/{Uri.EscapeDataString(server.ToLowerInvariant())}/{Uri.EscapeDataString(id)}"));
        request.Headers.Add("X-Install-Key", key);
        request.Content = new ByteArrayContent(Pack(stretches));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        return true;
    }

    public async Task<SharedLeveling> DownloadAsync(string server, string id, CancellationToken cancellation = default)
    {
        var address = new Uri(_base, $"v1/leveling/{Uri.EscapeDataString(server.ToLowerInvariant())}?skip={Uri.EscapeDataString(id)}");
        using HttpResponseMessage response = await _http.GetAsync(address, cancellation).ConfigureAwait(false);
        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        return Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
    }

    public async Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_base, $"v1/leveling/{Uri.EscapeDataString(server.ToLowerInvariant())}/{Uri.EscapeDataString(id)}"));
        request.Headers.Add("X-Install-Key", key);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the service's answer: { rows: [{ z, b, g, p, s, x, k }], zones: [...], players }.</summary>
    public static SharedLeveling Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        var rows = new List<LevelingRow>();
        if (root.TryGetProperty("rows", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in list.EnumerateArray())
            {
                rows.Add(new LevelingRow(
                    r.GetProperty("z").GetString() ?? string.Empty, r.GetProperty("b").GetInt32(), r.GetProperty("g").GetInt32() == 1,
                    "p" + r.GetProperty("p").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                    r.GetProperty("s").GetDouble(), r.GetProperty("x").GetDouble(), r.GetProperty("k").GetInt32(),
                    r.TryGetProperty("c", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0,
                    r.TryGetProperty("a", out JsonElement a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0,
                    r.TryGetProperty("q", out JsonElement q) && q.ValueKind == JsonValueKind.Number && q.GetInt32() == 1));
            }
        }

        var zones = new List<string>();
        if (root.TryGetProperty("zones", out JsonElement z) && z.ValueKind == JsonValueKind.Array)
        {
            zones.AddRange(z.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0));
        }

        int players = root.TryGetProperty("players", out JsonElement p) && p.TryGetInt32(out int n) ? n : 0;
        return new SharedLeveling(rows.Where(r => r.Zone.Length > 0).ToList(), zones, players);
    }

    private static async Task ThrowIfFailed(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message = $"The leveling service answered {(int)response.StatusCode}.";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("error", out JsonElement error) && error.GetString() is { Length: > 0 } text)
            {
                message = text;
            }
        }
        catch (JsonException)
        {
        }

        throw new HttpRequestException(message, null, response.StatusCode);
    }
}
