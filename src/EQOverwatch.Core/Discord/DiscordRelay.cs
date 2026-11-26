using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQOverwatch.Core.Discord;

/// <summary>A raid boss kill for the kills webhook; <see cref="Png"/> is the Discord picture.</summary>
public sealed record KillPost(string Mob, DateTime At, double Seconds, long Damage, double Dps, string Zone, byte[] Png)
    : DiscordPost(HookKind.Kills, Mob, At);

/// <summary>
/// Sends posts to Discord webhooks through the Overwatch server. The owner of a webhook registers it under a raid key; everyone else
/// only ever sends to the key, so the webhook address never leaves the owner's PC except to the server. The server writes the
/// message from the post's fields and posts each event once, however many raiders send it.
/// </summary>
public interface IDiscordRelay
{
    /// <summary>Registers (or replaces) the webhook behind a raid key. Returns null when it worked, otherwise why not.</summary>
    Task<string?> RegisterAsync(string key, HookKind kind, string webhookUrl, string installId, string installKey, CancellationToken cancellation = default);

    /// <summary>Forgets a raid key's webhook.</summary>
    Task UnregisterAsync(string key, string installId, string installKey, CancellationToken cancellation = default);

    /// <summary>Sends one post to one key. Returns null when it was posted (or was already posted by someone else), otherwise why not.</summary>
    Task<string?> PostAsync(string key, DiscordPost post, CancellationToken cancellation = default);
}

public sealed partial class HttpDiscordRelay : IDiscordRelay
{
    private readonly HttpClient _http;
    private readonly Uri _base;

    public HttpDiscordRelay(HttpClient http, string address = Drops.HttpSharedDropsService.DefaultAddress)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _base = new Uri(address.EndsWith('/') ? address : address + "/");
    }

    [GeneratedRegex(@"^https://(?:ptb\.|canary\.)?discord(?:app)?\.com/api/webhooks/\d{5,25}/[A-Za-z0-9_-]{20,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex WebhookPattern();

    /// <summary>True for a Discord webhook address.</summary>
    public static bool IsWebhook(string url) => WebhookPattern().IsMatch(url.Trim());

    public async Task<string?> RegisterAsync(string key, HookKind kind, string webhookUrl, string installId, string installKey, CancellationToken cancellation = default)
    {
        if (!IsWebhook(webhookUrl))
        {
            return "That isn't a Discord webhook address.";
        }

        string body = JsonSerializer.Serialize(new { id = installId, kind = Kind(kind), url = webhookUrl.Trim() });
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(_base, "v1/hooks/" + Uri.EscapeDataString(key)))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Install-Key", installKey);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? null : await Reason(response, cancellation).ConfigureAwait(false);
    }

    public async Task UnregisterAsync(string key, string installId, string installKey, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_base, "v1/hooks/" + Uri.EscapeDataString(key) + "?id=" + Uri.EscapeDataString(installId)));
        request.Headers.Add("X-Install-Key", installKey);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
    }

    public async Task<string?> PostAsync(string key, DiscordPost post, CancellationToken cancellation = default)
    {
        string payload = JsonSerializer.Serialize(Payload(post));
        HttpContent content;
        if (post is KillPost { Png.Length: > 0 } kill)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload");
            var image = new ByteArrayContent(kill.Png);
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(image, "image", "kill.png");
            content = form;
        }
        else
        {
            content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        using (content)
        {
            using HttpResponseMessage response = await _http.PostAsync(new Uri(_base, "v1/hooks/" + Uri.EscapeDataString(key) + "/post"), content, cancellation)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode ? null : await Reason(response, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>The fields the server writes the message from.</summary>
    public static Dictionary<string, object> Payload(DiscordPost post)
    {
        long t = new DateTimeOffset(DateTime.SpecifyKind(post.At, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var fields = new Dictionary<string, object> { ["mob"] = post.Mob, ["t"] = t };
        switch (post)
        {
            case KillPost kill:
                fields["type"] = "kill";
                fields["seconds"] = Math.Round(kill.Seconds);
                fields["damage"] = kill.Damage;
                fields["dps"] = Math.Round(kill.Dps);
                fields["zone"] = kill.Zone;
                break;
            case LootListPost list:
                fields["type"] = "loot";
                fields["poster"] = list.Poster;
                fields["items"] = list.Items.Select(i => new Dictionary<string, object> { ["n"] = i.Item, ["c"] = i.Count }).ToList();
                break;
            case UnlootedPost unlooted:
                fields["type"] = "unlooted";
                fields["item"] = unlooted.Item;
                fields["player"] = unlooted.Player;
                break;
            case LootReminderPost reminder:
                fields["type"] = "reminder";
                fields["items"] = reminder.Items.Select(i => new Dictionary<string, object> { ["n"] = i.Item, ["c"] = i.Count }).ToList();
                break;
        }

        return fields;
    }

    private static string Kind(HookKind kind) => kind switch
    {
        HookKind.Kills => "kills",
        HookKind.Loot => "loot",
        _ => "unlooted",
    };

    private static async Task<string> Reason(HttpResponseMessage response, CancellationToken cancellation)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out JsonElement error) && error.GetString() is { Length: > 0 } message)
            {
                return message;
            }
        }
        catch (JsonException)
        {
        }

        return response.StatusCode == HttpStatusCode.NotFound
            ? "Nobody has set up that raid key."
            : "The Overwatch server answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".";
    }
}
