using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.Core.Sharing;

public enum SharedItemKind
{
    Trigger,
    RaidBossTimer,
}

/// <summary>A trigger or raid boss timer that arrived through chat, already validated and cleaned.</summary>
public sealed class SharedItem
{
    public SharedItemKind Kind { get; init; }
    public TriggerRule? Trigger { get; init; }
    public RaidBossTimerRule? RaidBossTimer { get; init; }

    /// <summary>The chat tag it came with: "EQO", "EQC" or "EQNag".</summary>
    public string Tag { get; init; } = TriggerShareSerializer.Tag;

    public string Name => Kind == SharedItemKind.Trigger ? Trigger?.Name ?? string.Empty : RaidBossTimer?.Label ?? string.Empty;

    /// <summary>For the approval bar, e.g. <c>trigger "Protective Skin Faded"</c> or <c>raid timer "Tormax: Dragon Roar", every 30s</c>.</summary>
    public string Description => Kind == SharedItemKind.Trigger
        ? $"trigger \"{Name}\""
        : $"raid timer \"{Name}\", every {RaidBossTimer?.RecastSeconds}s";
}

/// <summary>
/// Packs a <see cref="TriggerRule"/> or <see cref="RaidBossTimerRule"/> into one chat-sized token and reads such tokens back.
/// <para>
/// Format: <c>{EQO:&lt;Base64&gt;}</c> where the Base64 text is the rule as JSON, compressed with Deflate. <c>{EQC:...}</c> (the
/// previous product name) is read the same way. <c>{EQNag:...}</c> is read on a best-effort basis: Base64, then GZip, ZLib, Deflate
/// or plain UTF-8, then JSON whose common property names (name, pattern/search, regex, duration, text, tts...) are mapped onto a trigger.
/// </para>
/// <para>
/// Everything that arrives is untrusted: payloads and decompressed JSON are size limited, strings are length limited, numbers are
/// clamped, regular expressions must compile, a local sound-file path is never imported, and every imported rule gets a new id.
/// </para>
/// </summary>
public static partial class TriggerShareSerializer
{
    public const string Tag = "EQO";
    public const string LegacyTag = "EQC";
    public const string NagTag = "EQNag";

    /// <summary>Longest Base64 payload that is even attempted.</summary>
    public const int MaxPayloadChars = 16 * 1024;

    /// <summary>Largest JSON document accepted after decompression (stops decompression bombs).</summary>
    public const int MaxJsonBytes = 64 * 1024;

    /// <summary>EverQuest cuts chat lines at about this many characters; longer tokens are likely to arrive damaged.</summary>
    public const int ChatSafeLength = 240;

    private const int MaxNameChars = 100;
    private const int MaxTextChars = 500;
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // empty text is left out; numbers and enums are always written
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Finds <c>{EQO:...}</c>, <c>{EQC:...}</c> and <c>{EQNag:...}</c> anywhere in a line.</summary>
    [GeneratedRegex(@"\{(?<tag>EQO|EQC|EQNag):(?<payload>[A-Za-z0-9+/=_\-]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    /// <summary>Every share token in <paramref name="text"/>, as (tag, payload).</summary>
    public static IReadOnlyList<(string Tag, string Payload)> FindTokens(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("{EQ", StringComparison.Ordinal) < 0)
        {
            return Array.Empty<(string, string)>();
        }

        var found = new List<(string, string)>();
        foreach (Match match in TokenRegex().Matches(text))
        {
            found.Add((match.Groups["tag"].Value, match.Groups["payload"].Value));
        }

        return found;
    }

    // ───────────────────────── export ─────────────────────────

    /// <summary>The <c>{EQO:...}</c> token for a trigger. The id and any local sound-file path are left out.</summary>
    public static string Export(TriggerRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var envelope = new Envelope
        {
            V = FormatVersion,
            Kind = "trigger",
            Trigger = new TriggerDto
            {
                Name = NullIfEmpty(rule.Name),
                Pattern = rule.Pattern ?? string.Empty,
                UseRegex = rule.UseRegex,
                TimerSeconds = rule.TimerDurationSeconds,
                TimerLabel = rule.TimerDurationSeconds > 0 ? NullIfEmpty(rule.TimerLabelTemplate) : null,
                Alert = NullIfEmpty(rule.AlertTextTemplate),
                Speak = NullIfEmpty(rule.AudioText),
                Overlay = rule.OverlayCategory,
            },
        };

        return ToToken(envelope);
    }

    /// <summary>The <c>{EQO:...}</c> token for a raid boss timer.</summary>
    public static string Export(RaidBossTimerRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        RaidBossTimerRule exported = rule.Clone();
        exported.ApplyLegacyTiming();

        var envelope = new Envelope
        {
            V = FormatVersion,
            Kind = "raid",
            Raid = new RaidDto
            {
                Mob = NullIfEmpty(rule.MobName),
                SpellId = rule.SpellId,
                Spell = rule.SpellName ?? string.Empty,
                Match = rule.MatchText ?? string.Empty,
                Recast = rule.RecastSeconds,
                Overlay = rule.OverlayCategory,
                Start = NullIfEmpty(exported.StartTtsText),
                Warn = NullIfEmpty(exported.WarningTtsText),
            },
        };

        return ToToken(envelope);
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>True when the token is short enough that EverQuest chat is unlikely to cut it.</summary>
    public static bool FitsInChat(string token) => token.Length <= ChatSafeLength;

    private static string ToToken(Envelope envelope)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, WriteOptions);

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(json, 0, json.Length);
        }

        return $"{{{Tag}:{Convert.ToBase64String(output.ToArray())}}}";
    }

    // ───────────────────────── import ─────────────────────────

    /// <summary>
    /// Reads a full token (<c>{EQO:...}</c>) or a bare Base64 payload. <paramref name="error"/> says what was wrong when this returns false.
    /// </summary>
    public static bool TryImport(string? text, out SharedItem? item, out string error)
    {
        item = null;
        error = string.Empty;

        string tag = Tag;
        string payload = text?.Trim() ?? string.Empty;

        IReadOnlyList<(string Tag, string Payload)> tokens = FindTokens(payload);
        if (tokens.Count > 0)
        {
            tag = tokens[0].Tag;
            payload = tokens[0].Payload;
        }

        return TryDecode(tag, payload, out item, out error);
    }

    /// <summary>Decodes the Base64 <paramref name="payload"/> of a token whose tag is <paramref name="tag"/>.</summary>
    public static bool TryDecode(string tag, string? payload, out SharedItem? item, out string error)
    {
        item = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "There is nothing to import.";
            return false;
        }

        if (payload.Length > MaxPayloadChars)
        {
            error = "The shared trigger is too large.";
            return false;
        }

        if (!TryBase64(payload, out byte[] bytes))
        {
            error = "The shared trigger is not valid Base64 (the chat line may have been cut off).";
            return false;
        }

        string canonicalTag = CanonicalTag(tag);
        foreach (string json in DecodeCandidates(bytes))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryBuildItem(document.RootElement, canonicalTag, out item, out error))
                {
                    return true;
                }

                if (error.Length > 0)
                {
                    return false; // it was a trigger, just not an acceptable one
                }
            }
            catch (JsonException)
            {
                // not this decoding; try the next
            }
        }

        error = "The shared trigger could not be read (unknown format).";
        return false;
    }

    private static string CanonicalTag(string tag) =>
        string.Equals(tag, NagTag, StringComparison.OrdinalIgnoreCase) ? NagTag
        : string.Equals(tag, LegacyTag, StringComparison.OrdinalIgnoreCase) ? LegacyTag
        : Tag;

    private static bool TryBase64(string payload, out byte[] bytes)
    {
        // Padding is worked out from the payload without its '=' signs (chat may drop them, other tools may keep them).
        string normalized = payload.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
        int padding = (4 - (normalized.Length % 4)) % 4;
        if (padding == 3)
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        normalized += new string('=', padding);

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>The ways the bytes might be packed, most likely first. Each yields UTF-8 text or nothing.</summary>
    private static IEnumerable<string> DecodeCandidates(byte[] bytes)
    {
        // EQO / EQC: raw Deflate. Other tools: GZip, ZLib, or no compression at all.
        foreach (Func<byte[], byte[]?> decoder in new Func<byte[], byte[]?>[]
                 {
                     b => Inflate(b, s => new DeflateStream(s, CompressionMode.Decompress)),
                     b => Inflate(b, s => new GZipStream(s, CompressionMode.Decompress)),
                     b => Inflate(b, s => new ZLibStream(s, CompressionMode.Decompress)),
                     b => b.Length <= MaxJsonBytes ? b : null,
                 })
        {
            byte[]? decoded = decoder(bytes);
            if (decoded is { Length: > 0 })
            {
                yield return Encoding.UTF8.GetString(decoded).TrimStart('﻿');
            }
        }
    }

    private static byte[]? Inflate(byte[] compressed, Func<Stream, Stream> open)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using Stream decompressor = open(input);
            using var output = new MemoryStream();

            var buffer = new byte[4096];
            int read;
            while ((read = decompressor.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > MaxJsonBytes)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    // ───────────────────────── JSON to rule ─────────────────────────

    /// <summary>
    /// Maps a JSON object onto a rule. Returns false with an empty <paramref name="error"/> when the object is not recognizable
    /// (so the next decoding is tried) and false with a message when it is a trigger that cannot be accepted.
    /// </summary>
    private static bool TryBuildItem(JsonElement root, string tag, out SharedItem? item, out string error)
    {
        item = null;
        error = string.Empty;

        // Our own envelope: {"v":1,"kind":"trigger"|"raid","trigger":{...}|"raid":{...}}
        if (TryGetObject(root, out JsonElement trigger, "trigger") && IsKind(root, "trigger"))
        {
            return BuildTrigger(trigger, tag, out item, out error);
        }

        if (TryGetObject(root, out JsonElement raid, "raid") && IsKind(root, "raid"))
        {
            return BuildRaid(raid, tag, out item, out error);
        }

        // Another tool's flat object (or one wrapped in "trigger"/"rule").
        JsonElement flat = TryGetObject(root, out JsonElement wrapped, "trigger", "rule", "data") ? wrapped : root;
        if (HasAny(flat, "recastSeconds", "recast", "matchText"))
        {
            return BuildRaid(flat, tag, out item, out error);
        }

        if (HasAny(flat, "pattern", "search", "searchText", "regex", "trigger", "text", "name"))
        {
            return BuildTrigger(flat, tag, out item, out error);
        }

        return false;
    }

    private static bool BuildTrigger(JsonElement e, string tag, out SharedItem? item, out string error)
    {
        item = null;
        string name = Clip(GetString(e, "name", "triggerName", "title"), MaxNameChars);
        string pattern = Clip(GetString(e, "pattern", "search", "searchText", "regexPattern", "regex", "trigger", "text"), MaxTextChars);
        bool useRegex = GetBool(e, "useRegex", "enableRegex", "isRegex", "regexEnabled") ?? InferRegex(e);

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "The shared trigger has no pattern.";
            return false;
        }

        if (useRegex)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.CultureInvariant, TriggerEngineService.MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                error = $"The shared trigger's pattern is not a valid regular expression: {ex.Message}";
                return false;
            }
        }

        var rule = new TriggerRule
        {
            Id = Guid.NewGuid(),
            Name = name.Length > 0 ? name : "Shared trigger",
            Pattern = pattern,
            UseRegex = useRegex,
            IsEnabled = true,
            TimerDurationSeconds = Math.Clamp(GetNumber(e, "timerSeconds", "timerDurationSeconds", "timerDuration", "duration", "durationSeconds") ?? 0, 0, 86400),
            TimerLabelTemplate = Clip(GetString(e, "timerLabel", "timerLabelTemplate"), MaxTextChars, TriggerRule.DefaultTemplate),
            AlertTextTemplate = Clip(GetString(e, "alert", "alertTextTemplate", "displayText", "displayMessage", "message"), MaxTextChars, string.Empty),
            AudioText = Clip(GetString(e, "speak", "audioText", "tts", "speech", "textToSpeech"), MaxTextChars),
            AudioFilePath = string.Empty, // a sound file on the sender's disk means nothing here (and must not point at ours)
            OverlayCategory = GetEnum(e, TimerOverlayCategory.RaidTriggers, "overlay", "overlayCategory"),
        };

        item = new SharedItem { Kind = SharedItemKind.Trigger, Trigger = rule, Tag = tag };
        error = string.Empty;
        return true;
    }

    private static bool BuildRaid(JsonElement e, string tag, out SharedItem? item, out string error)
    {
        item = null;
        string spell = Clip(GetString(e, "spell", "spellName"), MaxNameChars);
        string match = Clip(GetString(e, "match", "matchText"), MaxTextChars);

        if (string.IsNullOrWhiteSpace(spell) || string.IsNullOrEmpty(match))
        {
            error = "The shared raid timer needs a spell name and the log text to match.";
            return false;
        }

        double recast = GetNumber(e, "recast", "recastSeconds") ?? 30;
        var rule = new RaidBossTimerRule
        {
            Id = Guid.NewGuid(),
            IsEnabled = true,
            MobName = Clip(GetString(e, "mob", "mobName"), MaxNameChars),
            SpellId = (int)Math.Clamp(GetNumber(e, "spellId") ?? 0, 0, int.MaxValue),
            SpellName = spell,
            MatchText = match,
            RecastSeconds = (int)Math.Clamp(Math.Round(recast), 1, 86400),
            OverlayCategory = GetEnum(e, TimerOverlayCategory.RaidTriggers, "overlay", "overlayCategory"),
            StartTtsText = Clip(GetString(e, "start", "startTtsText"), MaxTextChars),
            WarningTtsText = Clip(GetString(e, "warn", "warningTtsText"), MaxTextChars),
        };

        // Tokens made before the start and 10-second announcements were separated carry one text plus a "warnWhen" setting.
        if (TryGetProperty(e, "warnWhen", out _) || TryGetProperty(e, "warningTiming", out _))
        {
            rule.WarningTiming = GetEnum(e, RaidWarningTiming.Both, "warnWhen", "warningTiming");
            rule.ApplyLegacyTiming();
        }

        item = new SharedItem { Kind = SharedItemKind.RaidBossTimer, RaidBossTimer = rule, Tag = tag };
        error = string.Empty;
        return true;
    }

    /// <summary>Foreign formats usually mark regex explicitly; when they say nothing, assume plain text unless it has regex syntax.</summary>
    private static bool InferRegex(JsonElement e) => HasAny(e, "regex", "regexPattern") && !HasAny(e, "pattern", "search", "searchText");

    private static bool IsKind(JsonElement root, string kind) =>
        string.Equals(GetString(root, "kind"), kind, StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── lenient JSON access ─────────────────────────

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetObject(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(obj, name, out value) && value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool HasAny(JsonElement obj, params string[] names) =>
        names.Any(n => TryGetProperty(obj, n, out JsonElement v) && v.ValueKind != JsonValueKind.Null);

    private static string GetString(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(obj, name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool? GetBool(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(obj, name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return null;
    }

    private static double? GetNumber(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(obj, name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
                && double.IsFinite(parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static T GetEnum<T>(JsonElement obj, T fallback, params string[] names) where T : struct, Enum
    {
        foreach (string name in names)
        {
            if (!TryGetProperty(obj, name, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String && Enum.TryParse(value.GetString(), ignoreCase: true, out T parsed) && Enum.IsDefined(parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && Enum.IsDefined(typeof(T), number))
            {
                return (T)Enum.ToObject(typeof(T), number);
            }
        }

        return fallback;
    }

    private static string Clip(string text, int max, string? whenEmpty = null)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 && whenEmpty is not null)
        {
            return whenEmpty;
        }

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    // ───────────────────────── wire shapes ─────────────────────────

    private sealed class Envelope
    {
        [JsonPropertyName("v")] public int V { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("trigger")] public TriggerDto? Trigger { get; set; }
        [JsonPropertyName("raid")] public RaidDto? Raid { get; set; }
    }

    private sealed class TriggerDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("pattern")] public string Pattern { get; set; } = string.Empty;
        [JsonPropertyName("useRegex")] public bool UseRegex { get; set; }
        [JsonPropertyName("timerSeconds")] public double TimerSeconds { get; set; }
        [JsonPropertyName("timerLabel")] public string? TimerLabel { get; set; }
        [JsonPropertyName("alert")] public string? Alert { get; set; }
        [JsonPropertyName("speak")] public string? Speak { get; set; }
        [JsonPropertyName("overlay")] public TimerOverlayCategory Overlay { get; set; }
    }

    private sealed class RaidDto
    {
        [JsonPropertyName("mob")] public string? Mob { get; set; }
        [JsonPropertyName("spellId")] public int SpellId { get; set; }
        [JsonPropertyName("spell")] public string Spell { get; set; } = string.Empty;
        [JsonPropertyName("match")] public string Match { get; set; } = string.Empty;
        [JsonPropertyName("recast")] public int Recast { get; set; }
        [JsonPropertyName("overlay")] public TimerOverlayCategory Overlay { get; set; }
        [JsonPropertyName("start")] public string? Start { get; set; }
        [JsonPropertyName("warn")] public string? Warn { get; set; }
    }
}
