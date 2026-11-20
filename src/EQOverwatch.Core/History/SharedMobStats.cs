using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQOverwatch.Core.History;

/// <summary>A kill shared for mob stats: how long it took, the damage it took and how many attacked (no names).</summary>
public sealed record SharedKill(
    [property: JsonPropertyName("m")] string Mob,
    [property: JsonPropertyName("s")] double Seconds,
    [property: JsonPropertyName("d")] long Damage,
    [property: JsonPropertyName("p")] int Players,
    [property: JsonPropertyName("t")] long Start);

/// <summary>A mob's level range in a zone, from /con.</summary>
public sealed record SharedLevel(
    [property: JsonPropertyName("m")] string Mob,
    [property: JsonPropertyName("z")] string Zone,
    [property: JsonPropertyName("lo")] int Lowest,
    [property: JsonPropertyName("hi")] int Highest);

/// <summary>Spells cast on a mob of one resist type (1 magic ... 5 disease): how many landed and how many were resisted.</summary>
public sealed record SharedResist(
    [property: JsonPropertyName("m")] string Mob,
    [property: JsonPropertyName("r")] int Type,
    [property: JsonPropertyName("l")] int Landed,
    [property: JsonPropertyName("x")] int Resisted);

/// <summary>A mob's pooled regen and hit points, from everyone's kills on every server.</summary>
public sealed record SharedRegen(
    [property: JsonPropertyName("m")] string Mob,
    [property: JsonPropertyName("r")] double Regen,
    [property: JsonPropertyName("hp")] long HitPoints,
    [property: JsonPropertyName("k")] int Kills);

/// <summary>The fastest kill and best raid DPS anyone has logged on a mob, on one server.</summary>
public sealed record ServerBest(
    [property: JsonPropertyName("m")] string Mob,
    [property: JsonPropertyName("f")] double FastestSeconds,
    [property: JsonPropertyName("dps")] double BestDps);

/// <summary>Everyone's pooled mob stats: regen and levels and resists over every server, best kills on one.</summary>
public sealed class SharedMobStats
{
    public static SharedMobStats Empty { get; } = new(Array.Empty<SharedRegen>(), Array.Empty<SharedLevel>(), Array.Empty<SharedResist>(), Array.Empty<ServerBest>(), 0);

    private readonly Dictionary<string, SharedRegen> _regen;
    private readonly ILookup<string, SharedLevel> _levels;
    private readonly ILookup<string, SharedResist> _resists;
    private readonly Dictionary<string, ServerBest> _best;

    public SharedMobStats(IEnumerable<SharedRegen> regen, IEnumerable<SharedLevel> levels, IEnumerable<SharedResist> resists, IEnumerable<ServerBest> best, long updated)
    {
        _regen = regen.GroupBy(r => r.Mob, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Levels = levels.ToList();
        _levels = Levels.ToLookup(l => l.Mob, StringComparer.OrdinalIgnoreCase);
        _resists = resists.ToLookup(r => r.Mob, StringComparer.OrdinalIgnoreCase);
        _best = best.GroupBy(b => b.Mob, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Updated = updated;
    }

    /// <summary>When the service worked these out (Unix seconds), 0 for none.</summary>
    public long Updated { get; }

    public IReadOnlyList<SharedLevel> Levels { get; }

    public MobRegen? Regen(string mob) =>
        _regen.TryGetValue(mob.Trim(), out SharedRegen? r) ? new MobRegen(r.Regen, r.HitPoints, r.Kills) : null;

    /// <summary>The mob's level range in <paramref name="zone"/>, or over every zone when that zone has none.</summary>
    public MobLevels? LevelRange(string mob, string? zone)
    {
        List<SharedLevel> found = _levels[mob.Trim()].ToList();
        List<SharedLevel> here = zone is null ? new() : found.Where(l => l.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase)).ToList();
        List<SharedLevel> use = here.Count > 0 ? here : found;
        return use.Count == 0 ? null : new MobLevels(mob, use.Min(l => l.Lowest), use.Max(l => l.Highest), here.Count > 0 ? zone : null);
    }

    /// <summary>The lowest and highest mob level seen in a zone, or null.</summary>
    public (int Lowest, int Highest)? ZoneRange(string zone)
    {
        List<SharedLevel> found = Levels.Where(l => l.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase)).ToList();
        return found.Count == 0 ? null : (found.Min(l => l.Lowest), found.Max(l => l.Highest));
    }

    /// <summary>Landed and resisted per resist type for a mob.</summary>
    public IReadOnlyDictionary<int, (int Landed, int Resisted)> Resists(string mob) =>
        _resists[mob.Trim()].GroupBy(r => r.Type).ToDictionary(g => g.Key, g => (g.Sum(r => r.Landed), g.Sum(r => r.Resisted)));

    public ServerBest? Best(string mob) => _best.GetValueOrDefault(mob.Trim());
}

/// <summary>What one install sends for mob stats: its new kills, and its current level ranges and resist counts.</summary>
public sealed record MobStatsUpload(IReadOnlyList<SharedKill> Kills, IReadOnlyList<SharedLevel> Levels, IReadOnlyList<SharedResist> Resists);

/// <summary>The shared mob stats service (the drops worker's /v1/mobs endpoints).</summary>
public interface IMobStatsService
{
    /// <summary>Shares mob stats (no names). False when the service says it was uploaded too recently.</summary>
    Task<bool> UploadAsync(string server, string id, string key, MobStatsUpload upload, CancellationToken cancellation = default);

    /// <summary>Everyone's pooled mob stats, with best kills on <paramref name="server"/>.</summary>
    Task<SharedMobStats> DownloadAsync(string server, CancellationToken cancellation = default);

    /// <summary>Takes down everything this install shared for mob stats on <paramref name="server"/>.</summary>
    Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default);
}

public sealed class HttpMobStatsService : IMobStatsService
{
    public const string Format = "EQOverwatchMobs";

    private readonly HttpClient _http;
    private readonly Uri _base;

    public HttpMobStatsService(HttpClient http, string address = Drops.HttpSharedDropsService.DefaultAddress)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _base = new Uri(address.EndsWith('/') ? address : address + "/");
    }

    private Uri Address(string server, string? id = null) =>
        new(_base, $"v1/mobs/{Uri.EscapeDataString(server.ToLowerInvariant())}" + (id is null ? string.Empty : "/" + Uri.EscapeDataString(id)));

    public static string Pack(MobStatsUpload upload) =>
        JsonSerializer.Serialize(new { format = Format, version = 1, kills = upload.Kills, levels = upload.Levels, resists = upload.Resists });

    public async Task<bool> UploadAsync(string server, string id, string key, MobStatsUpload upload, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Address(server, id));
        request.Headers.Add("X-Install-Key", key);
        request.Content = new StringContent(Pack(upload), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        return true;
    }

    public async Task<SharedMobStats> DownloadAsync(string server, CancellationToken cancellation = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(Address(server), cancellation).ConfigureAwait(false);
        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        return Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
    }

    public async Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Address(server, id));
        request.Headers.Add("X-Install-Key", key);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        }
    }

    private sealed record Wire(List<SharedRegen>? Regen, List<SharedLevel>? Levels, List<SharedResist>? Resists, List<ServerBest>? Best, long Updated);

    /// <summary>Reads the service's answer: { regen: [...], levels: [...], resists: [...], best: [...], updated }.</summary>
    public static SharedMobStats Parse(string json)
    {
        Wire? wire = JsonSerializer.Deserialize<Wire>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return wire is null
            ? SharedMobStats.Empty
            : new SharedMobStats(
                (wire.Regen ?? new()).Where(r => !string.IsNullOrEmpty(r.Mob)),
                (wire.Levels ?? new()).Where(l => !string.IsNullOrEmpty(l.Mob) && !string.IsNullOrEmpty(l.Zone)),
                (wire.Resists ?? new()).Where(r => !string.IsNullOrEmpty(r.Mob)),
                (wire.Best ?? new()).Where(b => !string.IsNullOrEmpty(b.Mob)),
                wire.Updated);
    }

    private static async Task ThrowIfFailed(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message = $"The mob stats service answered {(int)response.StatusCode}.";
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
