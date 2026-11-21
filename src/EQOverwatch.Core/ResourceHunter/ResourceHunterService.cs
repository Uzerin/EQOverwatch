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

        string address = $"{site}/api/servers/{Uri.EscapeDataString(server.ToLowerInvariant())}/bonuses/today";
        using HttpResponseMessage response = await _http.GetAsync(address, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{site} answered {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return ParseWebsite(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false), day);
    }

    /// <summary>Reads the website's { ok, data: [{ name, bonus, state, bonusDate }] }: confirmed bonuses (and "none") for <paramref name="day"/>.</summary>
    public static IReadOnlyList<ResourceZoneBonus> ParseWebsite(string json, string day)
    {
        var list = new List<ResourceZoneBonus>();
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (JsonElement z in data.EnumerateArray())
        {
            string? name = z.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            string? state = z.TryGetProperty("state", out JsonElement s) ? s.GetString() : null;
            string? date = z.TryGetProperty("bonusDate", out JsonElement d) ? d.GetString() : null;
            ResourceBonus? bonus = z.TryGetProperty("bonus", out JsonElement b) ? ResourceHunterText.FromKey(b.GetString()) : null;
            if (!string.IsNullOrEmpty(name) && bonus is { } found && date == day && !string.Equals(state, "UNCONFIRMED", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new ResourceZoneBonus(name, found, ResourceBonusSource.Website));
            }
        }

        return list;
    }
}
