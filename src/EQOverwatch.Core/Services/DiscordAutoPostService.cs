using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EQOverwatch.Core.Models;

namespace EQOverwatch.Core.Services;

/// <summary>Sends one message (with a picture) to a Discord channel's webhook.</summary>
public interface IDiscordPoster
{
    /// <summary>Posts <paramref name="message"/> and, when given, the PNG picture. Returns null on success, or a short reason it failed.</summary>
    Task<string?> PostAsync(string webhookUrl, string message, byte[]? png, string fileName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides which finished fights are posted to Discord, and words the message. A fight is posted once, only when it has just ended
/// (so reading an old log never posts), and only when it passes the filters. Thread-safe.
/// </summary>
public sealed class DiscordAutoPostSelector
{
    public const int DefaultMinSeconds = 30;
    public const int MaxMinSeconds = 3600;
    public const int DefaultMinPlayers = 6;
    public const int MaxMinPlayers = 72;

    /// <summary>A fight older than this when it is first seen is not posted (it came from catching up on a log, not from a live fight).</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _minSeconds = DefaultMinSeconds;
    private int _minPlayers = DefaultMinPlayers;

    /// <summary>Posting happens only while this is on; off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>When true, a fight that ended without the mob dying is posted too.</summary>
    public bool PostWipes { get; set; }

    /// <summary>Shortest fight posted, in seconds.</summary>
    public int MinSeconds
    {
        get { lock (_gate) { return _minSeconds; } }
        set { lock (_gate) { _minSeconds = Math.Clamp(value, 0, MaxMinSeconds); } }
    }

    /// <summary>Fewest attackers a posted fight must have.</summary>
    public int MinPlayers
    {
        get { lock (_gate) { return _minPlayers; } }
        set { lock (_gate) { _minPlayers = Math.Clamp(value, 1, MaxMinPlayers); } }
    }

    /// <summary>
    /// Looks at the finished fights and returns the ones to post now. Every fight seen is remembered, posted or not, so a fight is
    /// never considered twice and switching posting on does not send what happened before.
    /// </summary>
    public IReadOnlyList<EncounterSnapshot> Select(IEnumerable<EncounterSnapshot> completed, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(completed);
        var result = new List<EncounterSnapshot>();
        lock (_gate)
        {
            foreach (EncounterSnapshot fight in completed)
            {
                if (fight.IsActive || !_seen.Add(Key(fight)))
                {
                    continue;
                }

                if (Enabled && Qualifies(fight, now))
                {
                    result.Add(fight);
                }
            }

            if (_seen.Count > 5000)
            {
                _seen.Clear(); // only fights still in the tracker's list can come back, and those are fresh enough to be skipped by age anyway
            }
        }

        return result;
    }

    private bool Qualifies(EncounterSnapshot fight, DateTime now)
    {
        if (!fight.Killed && !PostWipes)
        {
            return false;
        }

        return now - fight.EndTime <= FreshWindow
            && fight.DurationSeconds >= _minSeconds
            && fight.Participants.Count >= _minPlayers;
    }

    private static string Key(EncounterSnapshot fight) =>
        string.Create(CultureInfo.InvariantCulture, $"{fight.TargetName}|{fight.StartTime.Ticks}");

    /// <summary>The line posted above the picture, such as "Praetorian Pius killed in 3:42 - 148K DPS, 24 players, 1 death".</summary>
    public static string BuildMessage(EncounterSnapshot fight)
    {
        ArgumentNullException.ThrowIfNull(fight);
        int seconds = (int)Math.Round(fight.DurationSeconds);
        string time = string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
        long dps = (long)Math.Round(fight.TotalDamage / Math.Max(1.0, fight.DurationSeconds));
        int deaths = fight.DeathRows.Count;
        string result = fight.Killed ? $"killed in {time}" : $"not killed after {time}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"**{fight.TargetName}** {result} - {dps:N0} raid DPS, {fight.Participants.Count} players, {deaths} death{(deaths == 1 ? string.Empty : "s")}");
    }

    /// <summary>True for an address that looks like a Discord webhook (https, discord.com or discordapp.com, /api/webhooks/).</summary>
    public static bool IsWebhookUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        bool host = uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase);
        return uri.Scheme == Uri.UriSchemeHttps && host && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Posts to a Discord webhook over HTTPS, waiting and trying once more when Discord says to slow down.</summary>
public sealed class DiscordWebhookPoster : IDiscordPoster
{
    private const int MaxRetryMilliseconds = 15_000;

    private readonly HttpClient _http;

    public DiscordWebhookPoster(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<string?> PostAsync(string webhookUrl, string message, byte[]? png, string fileName, CancellationToken cancellationToken = default)
    {
        if (!DiscordAutoPostSelector.IsWebhookUrl(webhookUrl))
        {
            return "The Discord webhook address is not valid.";
        }

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                using HttpRequestMessage request = BuildRequest(webhookUrl.Trim(), message, png, fileName);
                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return null;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
                {
                    double wait = await ReadRetrySecondsAsync(response, cancellationToken).ConfigureAwait(false);
                    await Task.Delay((int)Math.Min(MaxRetryMilliseconds, Math.Max(250, wait * 1000)), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return response.StatusCode switch
                {
                    HttpStatusCode.NotFound or HttpStatusCode.Unauthorized => "Discord does not know that webhook (was it deleted?).",
                    HttpStatusCode.TooManyRequests => "Discord is asking the app to slow down.",
                    _ => $"Discord answered {(int)response.StatusCode}.",
                };
            }
        }
        catch (OperationCanceledException)
        {
            return "The Discord post timed out.";
        }
        catch (HttpRequestException ex)
        {
            return $"Discord could not be reached: {ex.Message}";
        }
    }

    private static HttpRequestMessage BuildRequest(string url, string message, byte[]? png, string fileName)
    {
        string json = JsonSerializer.Serialize(new { content = message, allowed_mentions = new { parse = Array.Empty<string>() } });
        if (png is null)
        {
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        var form = new MultipartFormDataContent();
        form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "payload_json");
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files[0]", fileName);
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
    }

    private static async Task<double> ReadRetrySecondsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("retry_after", out JsonElement value) && value.TryGetDouble(out double seconds))
            {
                return seconds;
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
        }

        return 2.0;
    }
}
