using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EQOverwatch.Core.Drops;

/// <summary>This PC's drops on one server, packed for the online shared table.</summary>
public sealed record SharedDropsUpload(string Server, int Kills, int Drops, byte[] Gzip);

/// <summary>One player's file in the online shared table.</summary>
public sealed record SharedDropsEntry(string Id, long Updated, int Kills, int Drops);

/// <summary>The online shared drop table: each install keeps one file per server there, and the app merges everyone's files itself.</summary>
public interface ISharedDropsService
{
    Task<IReadOnlyList<SharedDropsEntry>> ListAsync(string server, CancellationToken cancellation = default);

    Task<byte[]> DownloadAsync(string server, string id, CancellationToken cancellation = default);

    /// <summary>Uploads (or replaces) this install's file. False when the service says it was uploaded too recently.</summary>
    Task<bool> UploadAsync(string id, string key, SharedDropsUpload upload, CancellationToken cancellation = default);

    Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default);
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

    public async Task<IReadOnlyList<SharedDropsEntry>> ListAsync(string server, CancellationToken cancellation = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(new Uri(_base, $"v1/drops/{Uri.EscapeDataString(server.ToLowerInvariant())}"), cancellation)
            .ConfigureAwait(false);
        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        var list = new List<SharedDropsEntry>();
        if (doc.RootElement.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement f in files.EnumerateArray())
            {
                list.Add(new SharedDropsEntry(
                    f.GetProperty("id").GetString() ?? string.Empty,
                    f.GetProperty("updated").GetInt64(),
                    f.GetProperty("kills").GetInt32(),
                    f.GetProperty("drops").GetInt32()));
            }
        }

        return list;
    }

    public async Task<byte[]> DownloadAsync(string server, string id, CancellationToken cancellation = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(new Uri(_base, FilePath(server, id)), cancellation).ConfigureAwait(false);
        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
    }

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
