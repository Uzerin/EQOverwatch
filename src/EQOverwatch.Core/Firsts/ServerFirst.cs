using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;

namespace EQOverwatch.Core.Firsts;

/// <summary>One player in a shared Server First fight. Names and guilds are shared here, and only here (the player chose to).</summary>
public sealed record FirstPlayer(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("g")] string Guild,
    [property: JsonPropertyName("c")] string Class,
    [property: JsonPropertyName("d")] long Damage,
    [property: JsonPropertyName("h")] long Healing,
    [property: JsonPropertyName("t")] long Taken,
    [property: JsonPropertyName("x")] int Deaths);

/// <summary>A raid fight shared for Server First: the mob, when it started (Unix seconds, UTC), how long it took and everyone in it.</summary>
public sealed record FirstFight(
    [property: JsonPropertyName("m")] string Mob,
    [property: JsonPropertyName("s")] long Start,
    [property: JsonPropertyName("l")] double Seconds,
    [property: JsonPropertyName("k")] bool Killed,
    [property: JsonPropertyName("p")] IReadOnlyList<FirstPlayer> Players)
{
    /// <summary>The guild most of the players are in (empty when none is known).</summary>
    [JsonIgnore]
    public string Guild => Players.Where(p => p.Guild.Length > 0).GroupBy(p => p.Guild, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? string.Empty;

    [JsonIgnore]
    public DateTime StartUtc => DateTimeOffset.FromUnixTimeSeconds(Start).UtcDateTime;
}

/// <summary>A mob's Server First on a server: its first kill and how sure it is.</summary>
/// <param name="FirstKill">Unix seconds (UTC) of the first kill; 0 for a raid target nobody has killed yet.</param>
/// <param name="Verified">True when the kill was shared within two days of happening (older kills are only the earliest known).</param>
/// <param name="Witnesses">How many installs logged the same kill.</param>
/// <param name="Mine">True when this install shared that first kill.</param>
public sealed record ServerFirstEntry(string Mob, long FirstKill, bool Verified, int Witnesses, bool Mine, FirstFight? Fight)
{
    public bool IsOpen => FirstKill == 0;

    public DateTime FirstKillLocal => DateTimeOffset.FromUnixTimeSeconds(FirstKill).LocalDateTime;
}

/// <summary>How one class does on one boss across every shared fight: DPS by tenths (10th to 90th percentile) and medians.</summary>
public sealed record ClassBenchmark(string Mob, string Class, int Fights, IReadOnlyList<double> DpsDeciles, double MedianHps, double MedianTakenPerSecond)
{
    /// <summary>The share of players of this class that <paramref name="dps"/> beats, 0 to 99 (between two tenths, in proportion).</summary>
    public double Percentile(double dps)
    {
        IReadOnlyList<double> d = DpsDeciles;
        if (d.Count == 0 || dps <= 0)
        {
            return 0;
        }

        if (dps <= d[0])
        {
            return d[0] > 0 ? 10 * dps / d[0] : 0;
        }

        for (int i = 1; i < d.Count; i++)
        {
            if (dps <= d[i])
            {
                double width = d[i] - d[i - 1];
                return (10 * i) + (width > 0 ? 10 * (dps - d[i - 1]) / width : 0);
            }
        }

        double last = d.Count > 1 ? d[^1] - d[^2] : d[^1];
        return Math.Min(99, (10 * d.Count) + (last > 0 ? 10 * (dps - d[^1]) / last : 9.9));
    }

    /// <summary>"Top 10% WAR", "Top 25% WAR", "Top 50% WAR", or empty.</summary>
    public string Badge(double dps)
    {
        double p = Percentile(dps);
        string top = p >= 90 ? "Top 10%" : p >= 75 ? "Top 25%" : p >= 50 ? "Top 50%" : string.Empty;
        return top.Length == 0 ? string.Empty : top + " " + Class;
    }
}

/// <summary>Everything the service knows about Server Firsts on one server.</summary>
public sealed record ServerFirsts(IReadOnlyList<ServerFirstEntry> Entries, IReadOnlyList<ClassBenchmark> Benchmarks, long Updated)
{
    public static ServerFirsts Empty { get; } = new(Array.Empty<ServerFirstEntry>(), Array.Empty<ClassBenchmark>(), 0);

    public ServerFirstEntry? Find(string mob) => Entries.FirstOrDefault(e => e.Mob.Equals(mob, StringComparison.OrdinalIgnoreCase));

    public ClassBenchmark? Benchmark(string mob, string cls) =>
        Benchmarks.FirstOrDefault(b => b.Mob.Equals(mob, StringComparison.OrdinalIgnoreCase) && b.Class.Equals(cls, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Which fights count for Server First, and the shared record of one.</summary>
public static class ServerFirstRules
{
    /// <summary>A fight needs more than 12 players (anyone who did damage, tanked or healed).</summary>
    public const int MinPlayers = 13;

    /// <summary>The boss must take this many times the median damage of the raid's other kills before it (the trash on the way).</summary>
    public const double BigFightFactor = 5;

    /// <summary>The other kills looked at are the ones up to this long before the fight.</summary>
    public static readonly TimeSpan TrashWindow = TimeSpan.FromHours(2);

    /// <summary>Mobs named "a ..." or "an ..." are trash; bosses are named.</summary>
    public static bool IsNamed(string mob)
    {
        string name = mob.Trim();
        return name.Length > 0 && !name.StartsWith("a ", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("an ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The players in the fight: everyone who did damage, took damage from the mob or healed.</summary>
    public static IReadOnlyList<string> Players(EncounterSnapshot fight) =>
        fight.Participants.Where(p => p.TotalDamage > 0).Select(p => p.Name)
            .Concat(fight.TakenRows.Where(t => t.TotalDamage > 0 || t.Hits > 0).Select(t => t.Name))
            .Concat(fight.HealingRows.Where(h => h.Effective > 0).Select(h => h.Name))
            .Where(PlayerNameHeuristic.CouldBePlayer)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// True for a fight that counts: a named mob, more than 12 players, and far more damage than the kills before it
    /// (<paramref name="earlierKillDamage"/>: the damage of the raid's other kills in the <see cref="TrashWindow"/> before it).
    /// </summary>
    public static bool Qualifies(EncounterSnapshot fight, IEnumerable<long> earlierKillDamage)
    {
        if (fight.IsActive || !IsNamed(fight.TargetName) || Players(fight).Count < MinPlayers)
        {
            return false;
        }

        long[] earlier = earlierKillDamage.Where(d => d > 0).OrderBy(d => d).ToArray();
        if (earlier.Length == 0)
        {
            return true; // pulled with no trash before it
        }

        double median = earlier.Length % 2 == 1 ? earlier[earlier.Length / 2] : (earlier[(earlier.Length / 2) - 1] + earlier[earlier.Length / 2]) / 2.0;
        return fight.TotalDamage >= BigFightFactor * median;
    }

    /// <summary>The shared record of a fight, with each player's guild from <paramref name="guildOf"/> (empty when not known).</summary>
    public static FirstFight Build(EncounterSnapshot fight, Func<string, string> guildOf)
    {
        var damage = fight.Participants.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var healing = fight.HealingRows.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var taken = fight.TakenRows.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        List<FirstPlayer> players = Players(fight).Select(name =>
        {
            damage.TryGetValue(name, out ParticipantSnapshot? d);
            healing.TryGetValue(name, out HealingSnapshot? h);
            taken.TryGetValue(name, out DamageTakenSnapshot? t);
            return new FirstPlayer(name, guildOf(name), d?.Class ?? string.Empty, d?.TotalDamage ?? 0, h?.Effective ?? 0, t?.TotalDamage ?? 0,
                Math.Max(d?.Deaths ?? 0, t?.Deaths ?? 0));
        }).ToList();
        long start = new DateTimeOffset(DateTime.SpecifyKind(fight.StartTime, DateTimeKind.Local)).ToUnixTimeSeconds();
        return new FirstFight(fight.TargetName, start, Math.Round(fight.DurationSeconds, 1), fight.Killed, players);
    }

    /// <summary>A fight snapshot from a shared record, for the picture (classes as shared; no timeline).</summary>
    public static EncounterSnapshot ToSnapshot(FirstFight fight)
    {
        double seconds = Math.Max(1, fight.Seconds);
        long total = fight.Players.Sum(p => p.Damage);
        long healTotal = fight.Players.Sum(p => p.Healing);
        long takenTotal = fight.Players.Sum(p => p.Taken);
        DateTime start = DateTimeOffset.FromUnixTimeSeconds(fight.Start).LocalDateTime;
        var participants = fight.Players.Where(p => p.Damage > 0).OrderByDescending(p => p.Damage)
            .Select(p => new ParticipantSnapshot(p.Name, p.Damage, total > 0 ? 100.0 * p.Damage / total : 0, p.Damage / seconds, p.Damage / seconds,
                0, 0, 0, 0, Class: p.Class, Deaths: p.Deaths))
            .ToList();
        var heals = fight.Players.Where(p => p.Healing > 0).OrderByDescending(p => p.Healing)
            .Select(p => new HealingSnapshot(p.Name, p.Healing, 0, 0, 0, 0, p.Healing / seconds, string.Empty))
            .ToList();
        var taken = fight.Players.Where(p => p.Taken > 0).OrderByDescending(p => p.Taken)
            .Select(p => new DamageTakenSnapshot(p.Name, p.Taken, takenTotal > 0 ? 100.0 * p.Taken / takenTotal : 0, 0, 0, p.Deaths))
            .ToList();
        _ = healTotal;
        return new EncounterSnapshot(0, fight.Mob, start, start.AddSeconds(seconds), seconds, total, false, participants, taken, Healing: heals, Killed: fight.Killed);
    }
}

/// <summary>The Server First service (the drops worker's /v1/firsts endpoints).</summary>
public interface IServerFirstService
{
    /// <summary>Shares raid fights (with names and guilds). False when the service says too many were sent today.</summary>
    Task<bool> UploadAsync(string server, string id, string key, IReadOnlyList<FirstFight> fights, CancellationToken cancellation = default);

    /// <summary>Every Server First, open raid target and class benchmark on <paramref name="server"/>; <paramref name="id"/> marks your own firsts.</summary>
    Task<ServerFirsts> DownloadAsync(string server, string id, CancellationToken cancellation = default);

    /// <summary>Takes down everything this install shared for Server First on <paramref name="server"/>.</summary>
    Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default);
}

public sealed class HttpServerFirstService : IServerFirstService
{
    public const string Format = "EQOverwatchFirsts";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly Uri _base;

    public HttpServerFirstService(HttpClient http, string address = Drops.HttpSharedDropsService.DefaultAddress)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _base = new Uri(address.EndsWith('/') ? address : address + "/");
    }

    private Uri Address(string server, string? id = null) =>
        new(_base, $"v1/firsts/{Uri.EscapeDataString(server.ToLowerInvariant())}" + (id is null ? string.Empty : "/" + Uri.EscapeDataString(id)));

    public static string Pack(IReadOnlyList<FirstFight> fights) => JsonSerializer.Serialize(new { format = Format, version = 1, fights });

    public async Task<bool> UploadAsync(string server, string id, string key, IReadOnlyList<FirstFight> fights, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Address(server, id));
        request.Headers.Add("X-Install-Key", key);
        request.Content = new StringContent(Pack(fights), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return false;
        }

        await ThrowIfFailed(response, cancellation).ConfigureAwait(false);
        return true;
    }

    public async Task<ServerFirsts> DownloadAsync(string server, string id, CancellationToken cancellation = default)
    {
        var address = new Uri(Address(server) + "?me=" + Uri.EscapeDataString(id));
        using HttpResponseMessage response = await _http.GetAsync(address, cancellation).ConfigureAwait(false);
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

    private sealed record WireEntry(string Mob, long First, bool Verified, int Witnesses, bool Mine, FirstFight? Fight);

    private sealed record WireBench(string Mob, string Class, int Fights, double[] Dps, double Hps, double Taken);

    private sealed record Wire(List<WireEntry>? Firsts, List<WireBench>? Benchmarks, long Updated);

    /// <summary>Reads the service's answer: { firsts: [{ mob, first, verified, witnesses, mine, fight }], benchmarks: [...], updated }.</summary>
    public static ServerFirsts Parse(string json)
    {
        Wire? wire = JsonSerializer.Deserialize<Wire>(json, Json);
        if (wire is null)
        {
            return ServerFirsts.Empty;
        }

        return new ServerFirsts(
            (wire.Firsts ?? new()).Where(f => !string.IsNullOrEmpty(f.Mob)).Select(f => new ServerFirstEntry(f.Mob, f.First, f.Verified, f.Witnesses, f.Mine, f.Fight)).ToList(),
            (wire.Benchmarks ?? new()).Where(b => !string.IsNullOrEmpty(b.Mob)).Select(b => new ClassBenchmark(b.Mob, b.Class ?? string.Empty, b.Fights, b.Dps ?? Array.Empty<double>(), b.Hps, b.Taken)).ToList(),
            wire.Updated);
    }

    private static async Task ThrowIfFailed(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message = $"The Server First service answered {(int)response.StatusCode}.";
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

    /// <summary>"Nov 12, 2026" for a Unix time.</summary>
    public static string DateText(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
}
