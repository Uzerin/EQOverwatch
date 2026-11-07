using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Updates;
using Xunit;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public sealed class UpdateAndHealingTests : IDisposable
{
    private const string Repo = "Uzerin/EQOverwatch";
    private const string ZipUrl = "https://github.com/Uzerin/EQOverwatch/releases/download/v1.0.46/EQOverwatch-v1.0.46-win-x64.zip";
    private const string HashUrl = ZipUrl + ".sha256";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public UpdateAndHealingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ───────────────────────── healing crits ─────────────────────────

    [Fact]
    public void A_critical_heal_is_counted_and_marked_and_overheal_still_works()
    {
        var tracker = new LiveDpsTracker();
        tracker.OnCombatEvent(new CombatEvent(At("20:13:00"), CombatEventKind.Damage, "Stackprime", "a frost giant", 100, DamageKind.Melee, "hit"));
        tracker.OnLogOwner("Ream");
        tracker.OnLogClock(At("20:13:01"));
        tracker.OnNonCombatMessage("You healed Regnier for 638 (16830) hit points by Complete Heal. (Critical)");
        tracker.OnNonCombatMessage("You healed Regnier for 300 (16830) hit points by Complete Heal.");
        tracker.OnNonCombatMessage("Mediq healed Tufts over time for 150 (306) hit points by Celestial Elixir.");

        EncounterSnapshot fight = Assert.Single(tracker.Snapshot.Active);
        HealingSnapshot ream = fight.HealingRows.Single(h => h.Name == "Ream");
        Assert.Equal(938, ream.Effective);
        Assert.Equal(2, ream.Heals);
        Assert.Equal(1, ream.CritHeals);
        Assert.Equal(638, ream.CritEffective);
        Assert.Equal(100.0 * 638 / 938, ream.CritHealPercent, 3);
        Assert.Equal("Complete Heal", ream.TopSpell); // the trailing tag is not part of the spell name

        HealingSnapshot mediq = fight.HealingRows.Single(h => h.Name == "Mediq");
        Assert.Equal(150, mediq.Effective);
        Assert.Equal(156, mediq.Overheal);
        Assert.Equal(0, mediq.CritHeals);
    }

    [Fact]
    public void Pets_get_their_own_attack_type()
    {
        Assert.Equal("Pet", AttackTypes.Label(AttackType.Pet));
        Assert.Contains(AttackType.Pet, AttackTypes.All);
        var scheme = new AttackColorScheme(new Dictionary<string, string> { ["Pet"] = "#112233", ["Healing"] = "#00FF00" });
        Assert.Equal("#112233", scheme.Normal(AttackType.Pet));
        Assert.Equal("#00FF00", scheme.Healing);
        Assert.NotEqual(scheme.Healing, scheme.HealingCrit);
        Assert.Equal(AttackColorScheme.DefaultHealingColor, new AttackColorScheme().Healing);
    }

    [Fact]
    public void Combat_is_a_line_in_the_last_ten_seconds()
    {
        DateTime now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(OverlayFade.IsInCombat(now.AddSeconds(-10), now));
        Assert.False(OverlayFade.IsInCombat(now.AddSeconds(-11), now));
        Assert.False(OverlayFade.IsInCombat(null, now));
    }

    // ───────────────────────── finding the release files ─────────────────────────

    private static JsonElement Release(params (string Name, string Url)[] assets)
    {
        string json = JsonSerializer.Serialize(new
        {
            assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url }),
        });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void A_release_with_a_zip_and_its_checksum_can_be_installed()
    {
        UpdateAssets? assets = GitHubUpdateChecker.FindAssets(Release(
            ("EQOverwatch-v1.0.46-win-x64.zip", ZipUrl), ("EQOverwatch-v1.0.46-win-x64.zip.sha256", HashUrl)));

        Assert.NotNull(assets);
        Assert.Equal(ZipUrl, assets!.ZipUrl);
        Assert.Equal(HashUrl, assets.HashUrl);
    }

    [Fact]
    public void A_release_without_a_checksum_cannot_be_installed_from_the_app()
    {
        Assert.Null(GitHubUpdateChecker.FindAssets(Release(("EQOverwatch-v1.0.46-win-x64.zip", ZipUrl))));
        Assert.Null(GitHubUpdateChecker.FindAssets(JsonDocument.Parse("{}").RootElement));
    }

    // ───────────────────────── download and verify ─────────────────────────

    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files;

        public FakeGitHub(Dictionary<string, byte[]> files) => _files = files;

        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(_files.TryGetValue(url, out byte[]? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static byte[] ZipWith(string entryName, string content)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName);
            using Stream writer = entry.Open();
            writer.Write(Encoding.UTF8.GetBytes(content));
        }

        return stream.ToArray();
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static UpdateInstaller Installer(Dictionary<string, byte[]> files, out FakeGitHub handler)
    {
        handler = new FakeGitHub(files);
        return new UpdateInstaller(new HttpClient(handler), Repo);
    }

    private static readonly UpdateAssets Assets = new("EQOverwatch-v1.0.46-win-x64.zip", ZipUrl, HashUrl);

    [Fact]
    public async Task A_download_that_matches_its_checksum_is_unpacked()
    {
        byte[] zip = ZipWith("EQOverwatch.exe", "new program");
        UpdateInstaller installer = Installer(new()
        {
            [ZipUrl] = zip,
            [HashUrl] = Encoding.ASCII.GetBytes($"{Sha256(zip)}  EQOverwatch-v1.0.46-win-x64.zip\r\n"),
        }, out _);

        string exe = await installer.PrepareAsync(Assets, Path.Combine(_dir, ".update"), null, CancellationToken.None);

        Assert.Equal("new program", File.ReadAllText(exe));
        Assert.False(File.Exists(Path.Combine(_dir, ".update", "update.zip"))); // the zip is removed once unpacked
    }

    [Fact]
    public async Task A_download_that_does_not_match_is_refused_and_removed()
    {
        byte[] zip = ZipWith("EQOverwatch.exe", "tampered");
        UpdateInstaller installer = Installer(new()
        {
            [ZipUrl] = zip,
            [HashUrl] = Encoding.ASCII.GetBytes(new string('a', 64)),
        }, out _);

        var ex = await Assert.ThrowsAsync<UpdateInstallException>(
            () => installer.PrepareAsync(Assets, Path.Combine(_dir, ".update"), null, CancellationToken.None));

        Assert.Contains("checksum", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, ".update", "EQOverwatch.exe")));
        Assert.False(File.Exists(Path.Combine(_dir, ".update", "update.zip")));
    }

    [Fact]
    public async Task A_missing_or_unreadable_checksum_stops_the_install()
    {
        byte[] zip = ZipWith("EQOverwatch.exe", "x");
        UpdateInstaller noHash = Installer(new() { [ZipUrl] = zip }, out _);
        await Assert.ThrowsAsync<UpdateInstallException>(
            () => noHash.PrepareAsync(Assets, Path.Combine(_dir, "a"), null, CancellationToken.None));

        UpdateInstaller junk = Installer(new() { [ZipUrl] = zip, [HashUrl] = Encoding.ASCII.GetBytes("not a hash") }, out _);
        await Assert.ThrowsAsync<UpdateInstallException>(
            () => junk.PrepareAsync(Assets, Path.Combine(_dir, "b"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Only_files_attached_to_this_repositorys_releases_are_downloaded()
    {
        UpdateInstaller installer = Installer(new(), out FakeGitHub handler);
        var evil = new UpdateAssets("x.zip", "https://example.com/EQOverwatch-win-x64.zip", "https://example.com/x.sha256");

        await Assert.ThrowsAsync<UpdateInstallException>(
            () => installer.PrepareAsync(evil, Path.Combine(_dir, ".update"), null, CancellationToken.None));
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task A_zip_without_the_exe_is_refused()
    {
        byte[] zip = ZipWith("somewhere/EQOverwatch.exe", "nested");
        UpdateInstaller installer = Installer(new() { [ZipUrl] = zip, [HashUrl] = Encoding.ASCII.GetBytes(Sha256(zip)) }, out _);

        await Assert.ThrowsAsync<UpdateInstallException>(
            () => installer.PrepareAsync(Assets, Path.Combine(_dir, ".update"), null, CancellationToken.None));
    }

    // ───────────────────────── swapping the exe ─────────────────────────

    [Fact]
    public void The_old_exe_moves_aside_and_the_new_one_takes_its_place()
    {
        string current = Path.Combine(_dir, "EQOverwatch.exe");
        string staged = Path.Combine(_dir, ".update", "EQOverwatch.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(current, "old");
        File.WriteAllText(staged, "new");

        UpdateSwapper.Swap(current, staged);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.Equal("old", File.ReadAllText(UpdateSwapper.BackupPath(current)));

        UpdateSwapper.CleanUp(current);
        Assert.False(File.Exists(UpdateSwapper.BackupPath(current)));
        Assert.False(Directory.Exists(Path.Combine(_dir, ".update")) && Directory.GetFiles(Path.Combine(_dir, ".update")).Length > 0);
        Assert.Equal("new", File.ReadAllText(current));
    }

    [Fact]
    public void A_failed_swap_puts_the_old_exe_back()
    {
        string current = Path.Combine(_dir, "EQOverwatch.exe");
        File.WriteAllText(current, "old");

        Assert.Throws<UpdateInstallException>(() => UpdateSwapper.Swap(current, Path.Combine(_dir, "missing.exe")));

        Assert.Equal("old", File.ReadAllText(current));
        Assert.False(File.Exists(UpdateSwapper.BackupPath(current)));
    }
}
