using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EQOverwatch.Core.Drops;

/// <summary>This PC's new drops on one server, packed for the online shared table. <see cref="Through"/> is the newest event's time.</summary>
public sealed record SharedDropsUpload(string Server, int Kills, int Drops, byte[] Gzip, long Through = 0);

/// <summary>
/// A server's merged shared table as the service answered: <see cref="Gzip"/> is null when the one held is still the newest.
/// <see cref="LastMerge"/> is when the service last ran its daily merge (unix seconds, 0 when it did not say).
/// </summary>
public sealed record MergedDropsDownload(long Updated, int Players, byte[]? Gzip, long LastMerge);

/// <summary>A server that has a merged shared table: its name, how many players it pools and its kills.</summary>
public sealed record MergedServer(string Server, int Players, long Kills);

/// <summary>
/// The online shared drop table: each install uploads its new kills and drops at most once a day, the service merges everyone's once a
/// day, and the app downloads one merged file per server. It also takes the anonymous daily player count.
/// </summary>
public interface ISharedDropsService
{
    /// <summary>The merged table of a server when it is newer than <paramref name="have"/>; null when the server has none yet.</summary>
    Task<MergedDropsDownload?> DownloadMergedAsync(string server, long have, CancellationToken cancellation = default);

    /// <summary>Every server that has a merged table (for pooling servers with normal loot).</summary>
    Task<IReadOnlyList<MergedServer>> MergedServersAsync(CancellationToken cancellation = default) =>
        Task.FromResult<IReadOnlyList<MergedServer>>(Array.Empty<MergedServer>());

    /// <summary>Uploads this install's new kills and drops. False when the service says it was uploaded too recently.</summary>
    Task<bool> UploadAsync(string id, string key, SharedDropsUpload upload, CancellationToken cancellation = default);

    Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default);

    /// <summary>Counts this install (a random id, not a name) as using the app today, with its version.</summary>
    Task PingAsync(string id, string version, CancellationToken cancellation = default);

    /// <summary>The same, with the servers its characters played on lately (server names only).</summary>
    Task PingAsync(string id, string version, IReadOnlyList<string> servers, CancellationToken cancellation = default) =>
        PingAsync(id, version, cancellation);

    /// <summary>The public player counts, or null when they could not be read.</summary>
    Task<PlayerStats?> StatsAsync(CancellationToken cancellation = default) => Task.FromResult<PlayerStats?>(null);
}

/// <summary>How many installs used the app today, and per server today and in the last 7 days.</summary>
public sealed record PlayerStats(int ActiveToday, IReadOnlyDictionary<string, int> ServersToday, IReadOnlyDictionary<string, int> Servers7Days)
{
    public static PlayerStats Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        static Dictionary<string, int> Servers(JsonElement root, string name)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in list.EnumerateArray())
                {
                    if (row.TryGetProperty("s", out JsonElement s) && row.TryGetProperty("n", out JsonElement n) && s.GetString() is { Length: > 0 } server)
                    {
                        result[server] = n.GetInt32();
                    }
                }
            }

            return result;
        }

        return new PlayerStats(
            root.TryGetProperty("activeToday", out JsonElement today) ? today.GetInt32() : 0,
            Servers(root, "serversToday"), Servers(root, "servers7Days"));
    }
}

/// <summary>The EQ Overwatch shared drops service (a Cloudflare Worker; see server/drops-worker).</summary>
public sealed class HttpSharedDropsService : ISharedDropsService
{
    public const string DefaultAddress = "https://eqo-drops.eqoverwatch.workers.dev/";

    private readonly HttpClient _http;
    private readonly Uri _base;

    public HttpSharedDropsService(HttpClient http, string address = DefaultAddress)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _base = new Uri(address.EndsWith('/') ? address : address + "/");
    }

    public async Task<IReadOnlyList<MergedServer>> MergedServersAsync(CancellationToken cancellation = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(new Uri(_base, "v1/merged"), cancellation).ConfigureAwait(false);
        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        return ParseMergedServers(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
    }

    /// <summary>Reads <c>{"servers":[{"s":"tormax","p":3,"k":1200}]}</c>; anything malformed is skipped.</summary>
    public static IReadOnlyList<MergedServer> ParseMergedServers(string body)
    {
        var list = new List<MergedServer>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("servers", out JsonElement servers) || servers.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (JsonElement s in servers.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("s", out JsonElement name) && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } server)
                {
                    int players = s.TryGetProperty("p", out JsonElement p) && p.TryGetInt32(out int pv) ? pv : 0;
                    long kills = s.TryGetProperty("k", out JsonElement k) && k.TryGetInt64(out long kv) ? kv : 0;
                    list.Add(new MergedServer(server.ToLowerInvariant(), players, kills));
                }
            }
        }
        catch (JsonException)
        {
        }

        return list;
    }

    public async Task<MergedDropsDownload?> DownloadMergedAsync(string server, long have, CancellationToken cancellation = default)
    {
        var address = new Uri(_base,
            $"v1/merged/{Uri.EscapeDataString(server.ToLowerInvariant())}?have={have.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        using HttpResponseMessage response = await _http.GetAsync(address, cancellation).ConfigureAwait(false);
        long lastMerge = Header(response, "X-Last-Merge");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return lastMerge > 0 ? new MergedDropsDownload(0, 0, null, lastMerge) : null;
        }

        long updated = Header(response, "X-Updated");
        int players = (int)Math.Min(int.MaxValue, Header(response, "X-Players"));
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new MergedDropsDownload(updated, players, null, lastMerge);
        }

        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
        return new MergedDropsDownload(updated, players, bytes, lastMerge);
    }

    public Task PingAsync(string id, string version, CancellationToken cancellation = default) =>
        PingAsync(id, version, Array.Empty<string>(), cancellation);

    public async Task<PlayerStats?> StatsAsync(CancellationToken cancellation = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(new Uri(_base, "v1/stats"), cancellation).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? PlayerStats.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false)) : null;
    }

    public async Task PingAsync(string id, string version, IReadOnlyList<string> servers, CancellationToken cancellation = default)
    {
        string body = servers.Count > 0 ? JsonSerializer.Serialize(new { id, version, servers }) : JsonSerializer.Serialize(new { id, version });
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.PostAsync(new Uri(_base, "v1/ping"), content, cancellation).ConfigureAwait(false);
        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
    }

    private static long Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
        && long.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value)
            ? value
            : 0;

    public async Task<bool> UploadAsync(string id, string key, SharedDropsUpload upload, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_base, FilePath(upload.Server, id)));
        request.Headers.Add("X-Install-Key", key);
        request.Headers.Add("X-Kills", upload.Kills.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Drops", upload.Drops.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new ByteArrayContent(upload.Gzip);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        return true;
    }

    public async Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_base, FilePath(server, id)));
        request.Headers.Add("X-Install-Key", key);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        }
    }

    private static string FilePath(string server, string id) =>
        $"v1/drops/{Uri.EscapeDataString(server.ToLowerInvariant())}/{Uri.EscapeDataString(id)}";

    /// <summary>Throws <see cref="HttpRequestException"/> with the service's own message when the request failed.</summary>
    private static async Task ThrowIfFailed(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message = $"The drops service answered {(int)response.StatusCode}.";
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

    /// <summary>A new random install id and key (URL-safe).</summary>
    public static (string Id, string Key) NewInstall() =>
        (RandomText(16), RandomText(32));

    private static string RandomText(int bytes) =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
