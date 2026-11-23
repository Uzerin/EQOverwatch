using System.Net;
using System.Text;
using System.Text.Json;

namespace EQOverwatch.Core.ResourceHunter;

/// <summary>Shares the bonuses your logs found and reads today's bonuses from the EQ Overwatch server and a server's tracking website.</summary>
public interface IResourceHunterService
{
    /// <summary>Shares findings (zone, bonus and Pacific day only). False when the server asked to wait.</summary>
    Task<bool> UploadAsync(string server, string id, string key, IReadOnlyList<ResourceFinding> findings, CancellationToken cancellation = default);

    /// <summary>Today's bonuses other EQ Overwatch players found on the server.</summary>
    Task<IReadOnlyList<ResourceZoneBonus>> SharedAsync(string server, string day, CancellationToken cancellation = default);

    /// <summary>Today's bonuses from the server's tracking website (<see cref="ResourceHunterText.WebsiteFor"/>); empty when it has none.</summary>
    Task<IReadOnlyList<ResourceZoneBonus>> WebsiteAsync(string server, string day, CancellationToken cancellation = default);

    /// <summary>A past day's whole list from the server's tracking website; empty when it has none.</summary>
    Task<IReadOnlyList<ResourceZoneBonus>> WebsiteDayAsync(string server, string day, CancellationToken cancellation = default);
}

public sealed class HttpResourceHunterService : IResourceHunterService
{
    private readonly HttpClient _http;
    private readonly Uri _base;

    public HttpResourceHunterService(HttpClient http, string address = Drops.HttpSharedDropsService.DefaultAddress)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _base = new Uri(address.EndsWith('/') ? address : address + "/");
    }

    private Uri Address(string server, string? id = null) =>
        new(_base, $"v1/bonuses/{Uri.EscapeDataString(server.ToLowerInvariant())}" + (id is null ? string.Empty : "/" + Uri.EscapeDataString(id)));

    public static string Pack(IReadOnlyList<ResourceFinding> findings) =>
        JsonSerializer.Serialize(new
        {
            bonuses = findings.Select(f => new { d = f.Day, z = f.Zone, b = ResourceHunterText.Key(f.Bonus) }),
        });

    public async Task<bool> UploadAsync(string server, string id, string key, IReadOnlyList<ResourceFinding> findings, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Address(server, id));
        request.Headers.Add("X-Install-Key", key);
        request.Content = new StringContent(Pack(findings), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The bonus service answered {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return true;
    }

    public async Task<IReadOnlyList<ResourceZoneBonus>> SharedAsync(string server, string day, CancellationToken cancellation = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(new Uri(Address(server) + "?day=" + Uri.EscapeDataString(day)), cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The bonus service answered {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return ParseShared(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false), day);
    }

    /// <summary>Reads { day, zones: [{ z, b, n }] } (n: how many players reported it).</summary>
    public static IReadOnlyList<ResourceZoneBonus> ParseShared(string json, string day)
    {
        var list = new List<ResourceZoneBonus>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("day", out JsonElement d) && d.GetString() != day)
        {
            return list;
        }

        if (doc.RootElement.TryGetProperty("zones", out JsonElement zones) && zones.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement z in zones.EnumerateArray())
            {
                if (z.TryGetProperty("z", out JsonElement zone) && zone.GetString() is { Length: > 0 } name
                    && z.TryGetProperty("b", out JsonElement b) && ResourceHunterText.FromKey(b.GetString()) is { } bonus)
                {
                    int n = z.TryGetProperty("n", out JsonElement count) && count.TryGetInt32(out int c) ? c : 1;
                    list.Add(new ResourceZoneBonus(name, bonus, ResourceBonusSource.Shared, n));
                }
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<ResourceZoneBonus>> WebsiteAsync(string server, string day, CancellationToken cancellation = default)
    {
        if (ResourceHunterText.WebsiteFor(server) is not { } site)
        {
            return Array.Empty<ResourceZoneBonus>();
        }

        return await ReadWebsiteAsync(site, $"{site}/api/servers/{Uri.EscapeDataString(server.ToLowerInvariant())}/bonuses/today", day, cancellation).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ResourceZoneBonus>> WebsiteDayAsync(string server, string day, CancellationToken cancellation = default)
    {
        if (ResourceHunterText.WebsiteFor(server) is not { } site)
        {
            return Array.Empty<ResourceZoneBonus>();
        }

        string address = $"{site}/api/servers/{Uri.EscapeDataString(server.ToLowerInvariant())}/bonuses/history/date?date={Uri.EscapeDataString(day)}";
        return await ReadWebsiteAsync(site, address, day, cancellation).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ResourceZoneBonus>> ReadWebsiteAsync(string site, string address, string day, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await _http.GetAsync(address, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{site} answered {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return ParseWebsite(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false), day);
    }

    /// <summary>
    /// Reads the website's { ok, data: [{ name, bonus, state, bonusDate, minLevel, maxLevel }] }: every zone for <paramref name="day"/> with its
    /// level range; bonuses nobody confirmed yet come back as <see cref="ResourceBonus.Unknown"/>.
    /// </summary>
    public static IReadOnlyList<ResourceZoneBonus> ParseWebsite(string json, string day)
    {
        var list = new List<ResourceZoneBonus>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int Number(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

        foreach (JsonElement z in data.EnumerateArray().Where(z => z.ValueKind == JsonValueKind.Object))
        {
            string? name = Text(z, "name");
            if (string.IsNullOrEmpty(name) || Text(z, "bonusDate") != day)
            {
                continue;
            }

            ResourceBonus bonus = string.Equals(Text(z, "state"), "UNCONFIRMED", StringComparison.OrdinalIgnoreCase)
                ? ResourceBonus.Unknown
                : ResourceHunterText.FromKey(Text(z, "bonus")) ?? ResourceBonus.Unknown;
            list.Add(new ResourceZoneBonus(name, bonus, ResourceBonusSource.Website, 0, Number(z, "minLevel"), Number(z, "maxLevel")));
        }

        return list;
    }
}
