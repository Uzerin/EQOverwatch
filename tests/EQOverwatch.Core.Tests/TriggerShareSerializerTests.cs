using System.IO;
using System.IO.Compression;
using System.Text;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Sharing;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class TriggerShareSerializerTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);

    private static TriggerRule SampleTrigger() => new()
    {
        Name = "Mez broke",
        Pattern = @"^(?<who>\w+) has been awakened by",
        UseRegex = true,
        TimerDurationSeconds = 18,
        TimerLabelTemplate = "Mez ${who}",
        AlertTextTemplate = "Mez broke: ${who}",
        AudioText = "Mez broke",
        AudioFilePath = @"C:\secret\evil.wav",
        OverlayCategory = TimerOverlayCategory.Debuffs,
    };

    private static string Base64Of(string json, Func<Stream, Stream> wrap)
    {
        using var ms = new MemoryStream();
        using (Stream z = wrap(ms))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            z.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    [Fact]
    public void A_trigger_round_trips_but_never_carries_the_audio_file_path()
    {
        string token = TriggerShareSerializer.Export(SampleTrigger());

        Assert.StartsWith("{EQO:", token);
        Assert.EndsWith("}", token);
        Assert.True(TriggerShareSerializer.TryImport(token, out SharedItem? item, out string error), error);
        Assert.NotNull(item);
        Assert.Equal(SharedItemKind.Trigger, item!.Kind);

        TriggerRule rule = item.Trigger!;
        Assert.Equal("Mez broke", rule.Name);
        Assert.Equal(@"^(?<who>\w+) has been awakened by", rule.Pattern);
        Assert.True(rule.UseRegex);
        Assert.Equal(18, rule.TimerDurationSeconds);
        Assert.Equal("Mez ${who}", rule.TimerLabelTemplate);
        Assert.Equal("Mez broke: ${who}", rule.AlertTextTemplate);
        Assert.Equal("Mez broke", rule.AudioText);
        Assert.Equal(TimerOverlayCategory.Debuffs, rule.OverlayCategory);
        Assert.True(string.IsNullOrEmpty(rule.AudioFilePath));
        Assert.NotEqual(SampleTrigger().Id, rule.Id);
    }

    [Fact]
    public void A_raid_boss_timer_round_trips()
    {
        var rule = new RaidBossTimerRule
        {
            MobName = "Tormax", SpellId = 1234, SpellName = "Dragon Roar", MatchText = "blasted by a roar",
            RecastSeconds = 48, OverlayCategory = TimerOverlayCategory.Debuffs, StartTtsText = "Roar now", WarningTtsText = "Roar soon",
        };

        string token = TriggerShareSerializer.Export(rule);
        Assert.True(TriggerShareSerializer.TryImport(token, out SharedItem? item, out string error), error);

        Assert.Equal(SharedItemKind.RaidBossTimer, item!.Kind);
        RaidBossTimerRule back = item.RaidBossTimer!;
        Assert.Equal("Tormax", back.MobName);
        Assert.Equal(1234, back.SpellId);
        Assert.Equal("Dragon Roar", back.SpellName);
        Assert.Equal("blasted by a roar", back.MatchText);
        Assert.Equal(48, back.RecastSeconds);
        Assert.Equal(TimerOverlayCategory.Debuffs, back.OverlayCategory);
        Assert.Equal("Roar now", back.StartTtsText);
        Assert.Equal("Roar soon", back.WarningTtsText);
        Assert.Null(back.WarningTiming);
    }

    [Fact]
    public void A_raid_timer_with_the_old_single_text_and_timing_is_shared_as_separate_texts()
    {
        var rule = new RaidBossTimerRule
        {
            MobName = "Tormax", SpellName = "Dragon Roar", MatchText = "blasted", RecastSeconds = 48,
            WarningTtsText = "Roar", WarningTiming = RaidWarningTiming.Both,
        };

        Assert.True(TriggerShareSerializer.TryImport(TriggerShareSerializer.Export(rule), out SharedItem? item, out string error), error);

        RaidBossTimerRule back = item!.RaidBossTimer!;
        Assert.Equal("Roar", back.StartTtsText);
        Assert.Equal("Roar", back.WarningTtsText);
    }

    [Fact]
    public void Tokens_are_found_inside_chat_lines()
    {
        string token = TriggerShareSerializer.Export(SampleTrigger());
        string line = $"Ream tells the raid, 'use this {token} thanks'";

        var found = TriggerShareSerializer.FindTokens(line);

        Assert.Equal("EQO", Assert.Single(found).Tag);
        Assert.Empty(TriggerShareSerializer.FindTokens("nothing to see {EQX:abc}"));
        Assert.Empty(TriggerShareSerializer.FindTokens(null));
    }

    [Fact]
    public void Garbage_truncated_and_oversized_payloads_are_rejected()
    {
        Assert.False(TriggerShareSerializer.TryImport("{EQO:not-base64!!}", out SharedItem? a, out string e1));
        Assert.Null(a);
        Assert.False(string.IsNullOrEmpty(e1));

        string token = TriggerShareSerializer.Export(SampleTrigger());
        string truncated = token[..(token.Length / 2)] + "}";
        Assert.False(TriggerShareSerializer.TryImport(truncated, out _, out _));

        string huge = "{EQO:" + new string('A', TriggerShareSerializer.MaxPayloadChars + 10) + "}";
        Assert.False(TriggerShareSerializer.TryImport(huge, out _, out _));

        Assert.False(TriggerShareSerializer.TryImport("hello", out _, out _));
        Assert.False(TriggerShareSerializer.TryImport(null, out _, out _));
    }

    [Fact]
    public void A_regex_that_does_not_compile_is_rejected()
    {
        TriggerRule bad = SampleTrigger();
        bad.Pattern = "([unclosed";
        string token = TriggerShareSerializer.Export(bad);

        Assert.False(TriggerShareSerializer.TryImport(token, out SharedItem? item, out string error));
        Assert.Null(item);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Absurd_numbers_are_clamped()
    {
        TriggerRule rule = SampleTrigger();
        rule.TimerDurationSeconds = 1e12;

        Assert.True(TriggerShareSerializer.TryImport(TriggerShareSerializer.Export(rule), out SharedItem? item, out _));
        Assert.True(item!.Trigger!.TimerDurationSeconds <= 24 * 3600.0 + 1);
    }

    [Fact]
    public void Legacy_EQC_tokens_are_decoded_too()
    {
        string json = """{"v":1,"kind":"trigger","trigger":{"name":"Legacy","pattern":"hello","useRegex":false,"timerSeconds":5}}""";

        Assert.True(TriggerShareSerializer.TryDecode("EQC", Base64Of(json, s => new DeflateStream(s, CompressionLevel.Optimal, true)), out SharedItem? item, out string error), error);
        Assert.Equal("Legacy", item!.Trigger!.Name);
        Assert.Equal("EQC", item.Tag);
    }

    [Fact]
    public void Foreign_EQNag_payloads_are_mapped_leniently_from_gzip_json()
    {
        string json = """{"Name":"Nag trigger","SearchText":"gets hit","Duration":12,"DisplayText":"Hit!"}""";
        string payload = Base64Of(json, s => new GZipStream(s, CompressionLevel.Optimal, true));

        bool ok = TriggerShareSerializer.TryDecode("EQNag", payload, out SharedItem? item, out string error);

        // Best-effort mapping: must never throw, and a recognizable trigger keeps its name.
        if (ok)
        {
            Assert.Equal("Nag trigger", item!.Trigger!.Name);
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    [Fact]
    public void Tokens_that_fit_in_chat_are_reported()
    {
        Assert.True(TriggerShareSerializer.FitsInChat("{EQO:abc}"));
        Assert.False(TriggerShareSerializer.FitsInChat(new string('x', TriggerShareSerializer.ChatSafeLength + 1)));
    }

    // ───────────────────────── scanner ─────────────────────────

    [Fact]
    public void The_scanner_offers_a_valid_token_once_even_when_two_logs_hear_it()
    {
        var scanner = new SharedTriggerScanner();
        var offers = new List<SharedTriggerOffer>();
        scanner.Offered += offers.Add;
        string line = $"Ream tells the raid, '{TriggerShareSerializer.Export(SampleTrigger())}'";

        scanner.OnLine("Stackprime", line, T0);
        scanner.OnLine("Oodles", line, T0.AddSeconds(1));

        SharedTriggerOffer offer = Assert.Single(offers);
        Assert.Equal("Mez broke", offer.Item.Name);
        Assert.Equal("Ream", offer.Sender);

        scanner.OnLine("Stackprime", line, T0.AddSeconds(60)); // long after: a new share
        Assert.Equal(2, offers.Count);
    }

    [Fact]
    public void The_scanner_ignores_ordinary_and_damaged_lines()
    {
        var scanner = new SharedTriggerScanner();
        int offered = 0;
        scanner.Offered += _ => offered++;

        scanner.OnLine("Stackprime", "You slash a gnoll for 100 points of damage.", T0);
        scanner.OnLine("Stackprime", "Ream tells the raid, '{EQO:%%%%%%%%}'", T0);

        Assert.Equal(0, offered);
    }
}
