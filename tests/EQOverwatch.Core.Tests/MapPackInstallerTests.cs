using System.IO.Compression;
using System.Text;
using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class MapPackInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ow-mappack-" + Guid.NewGuid().ToString("N"));

    public MapPackInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private (MapPackManifest Manifest, string Zip) Pack(params (string Name, string Text)[] files)
    {
        string zip = Path.Combine(_root, "pack.zip");
        File.Delete(zip);
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach ((string name, string text) in files)
            {
                using Stream s = archive.CreateEntry(name).Open();
                s.Write(B(text));
            }
        }

        var manifest = new MapPackManifest("2026-11-21", files.Select(f => new MapPackFile(f.Name, f.Text.Length, MapPackInstaller.Hash(B(f.Text)))).ToList());
        return (manifest, zip);
    }

    [Fact]
    public void FirstInstallAddsMissingFilesAndProtectsFilesItDidNotInstall()
    {
        string folder = Path.Combine(_root, "maps", "Overwatch");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "same.txt"), "L 1, 2\n", Encoding.Latin1);
        File.WriteAllText(Path.Combine(folder, "mine.txt"), "L 0, 0\nP 1, 1, 0, 255, 0, 0, 2, My_Camp\n", Encoding.Latin1);

        var (manifest, zip) = Pack(("new.txt", "L 5, 5\n"), ("same.txt", "L 1, 2\n"), ("mine.txt", "L 0, 0\n"));
        var record = new MapPackRecord();
        var audits = MapPackInstaller.Audit(folder, manifest, record);

        Assert.Equal(MapFileState.Missing, audits.Single(a => a.File.Name == "new.txt").State);
        Assert.Equal(MapFileState.Current, audits.Single(a => a.File.Name == "same.txt").State);
        Assert.Equal(MapFileState.Edited, audits.Single(a => a.File.Name == "mine.txt").State);

        var result = MapPackInstaller.Install(zip, folder, manifest, audits, new Dictionary<string, MapEditChoice>(), record, Path.Combine(_root, "bak"));

        Assert.Equal(new MapInstallResult(1, 0, 1, 1, 0, 0, 0), result);
        Assert.Contains("My_Camp", File.ReadAllText(Path.Combine(folder, "mine.txt")));
        Assert.Equal("L 5, 5\n", File.ReadAllText(Path.Combine(folder, "new.txt")));
        Assert.False(record.Files.ContainsKey("mine.txt"));
        Assert.True(record.Files.ContainsKey("new.txt"));
    }

    [Fact]
    public void UpdateReplacesUntouchedFilesButAsksAboutEditedOnes()
    {
        string folder = Path.Combine(_root, "Overwatch");
        var (v1, zip1) = Pack(("a.txt", "L 1\n"), ("b.txt", "L 2\n"));
        var record = new MapPackRecord();
        MapPackInstaller.Install(zip1, folder, v1, MapPackInstaller.Audit(folder, v1, record), new Dictionary<string, MapEditChoice>(), record, Path.Combine(_root, "bak"));

        File.AppendAllText(Path.Combine(folder, "b.txt"), "P 3, 4, 0, 0, 255, 0, 2, Tankard_Spot\n", Encoding.Latin1);
        var (v2, zip2) = Pack(("a.txt", "L 1\nL 9\n"), ("b.txt", "L 2\nL 8\n"));
        var audits = MapPackInstaller.Audit(folder, v2, record);
        Assert.Equal(MapFileState.Update, audits.Single(a => a.File.Name == "a.txt").State);
        Assert.Equal(MapFileState.Edited, audits.Single(a => a.File.Name == "b.txt").State);

        string backups = Path.Combine(_root, "bak2");
        var result = MapPackInstaller.Install(zip2, folder, v2, audits,
            new Dictionary<string, MapEditChoice> { ["b.txt"] = MapEditChoice.Merge }, record, backups);

        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Merged);
        Assert.Equal("L 1\nL 9\n", File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Equal("L 2\nL 8\nP 3, 4, 0, 0, 255, 0, 2, Tankard_Spot\n", File.ReadAllText(Path.Combine(folder, "b.txt")));
        Assert.Contains("Tankard_Spot", File.ReadAllText(Path.Combine(backups, "b.txt")));

        // A merged file stays the player's: the next update asks again instead of replacing it.
        Assert.Equal(MapFileState.Edited, MapPackInstaller.Audit(folder, v2, record).Single(a => a.File.Name == "b.txt").State);
    }

    [Fact]
    public void OverwriteBacksUpAndFailedChecksumIsSkipped()
    {
        string folder = Path.Combine(_root, "Overwatch");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "mine\n");
        var (manifest, zip) = Pack(("a.txt", "pack\n"), ("c.txt", "pack c\n"));
        var bad = manifest with { Files = manifest.Files.Select(f => f.Name == "c.txt" ? f with { Sha256 = new string('0', 64) } : f).ToList() };
        var record = new MapPackRecord();
        var audits = MapPackInstaller.Audit(folder, bad, record);

        var result = MapPackInstaller.Install(zip, folder, bad, audits,
            new Dictionary<string, MapEditChoice> { ["a.txt"] = MapEditChoice.Overwrite }, record, Path.Combine(_root, "bak"));

        Assert.Equal(1, result.Overwritten);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("pack\n", File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(_root, "bak", "a.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "c.txt")));
    }

    [Fact]
    public void ManifestDropsUnsafeNames()
    {
        string hash = new('a', 64);
        var manifest = MapPackInstaller.ParseManifest(
            $$"""{"version":"v","files":[{"name":"ok.txt","size":1,"sha256":"{{hash}}"},{"name":"../evil.txt","size":1,"sha256":"{{hash}}"},{"name":"sub/x.txt","size":1,"sha256":"{{hash}}"},{"name":"run.exe","size":1,"sha256":"{{hash}}"}]}""");
        Assert.Equal(new[] { "ok.txt" }, manifest.Files.Select(f => f.Name));
    }

    [Fact]
    public void ReleasesPickNewestMapsReleaseOnTheRepository()
    {
        const string repo = "Uzerin/EQOverwatch";
        string Asset(string tag, string name, string host = "https://github.com/" + repo) =>
            $$"""{"name":"{{name}}","browser_download_url":"{{host}}/releases/download/{{tag}}/{{name}}"}""";
        string json = "[" +
            $$"""{"tag_name":"v1.0.90","published_at":"2026-11-22T00:00:00Z","assets":[{{Asset("v1.0.90", "OverwatchMaps.json")}},{{Asset("v1.0.90", "OverwatchMaps.zip")}}]},""" +
            $$"""{"tag_name":"maps-2026-11-21","published_at":"2026-11-21T00:00:00Z","assets":[{{Asset("maps-2026-11-21", "OverwatchMaps.json")}},{{Asset("maps-2026-11-21", "OverwatchMaps.zip")}}]},""" +
            $$"""{"tag_name":"maps-2026-11-23","published_at":"2026-11-23T00:00:00Z","assets":[{{Asset("maps-2026-11-23", "OverwatchMaps.json", "https://example.com")}},{{Asset("maps-2026-11-23", "OverwatchMaps.zip")}}]},""" +
            $$"""{"tag_name":"maps-2026-11-01","published_at":"2026-11-01T00:00:00Z","assets":[{{Asset("maps-2026-11-01", "OverwatchMaps.json")}},{{Asset("maps-2026-11-01", "OverwatchMaps.zip")}}]}""" +
            "]";

        MapPackRelease? release = MapPackInstaller.ParseReleases(json, repo);

        Assert.NotNull(release);
        Assert.Equal("maps-2026-11-21", release!.Tag);
        Assert.Null(MapPackInstaller.ParseReleases("[]", repo));
    }

    [Theory]
    [InlineData(0, 0, 0, -1)]   // Characters
    [InlineData(2, 0, 1, 1)]    // old Raid Triggers -> Triggers / Raid
    [InlineData(5, 0, 2, -1)]   // old Encounter DPS
    [InlineData(7, 0, 3, 1)]    // old Drop Table -> Loot / Drop Table
    [InlineData(18, 0, 4, 2)]   // old Players & Pets -> Raid / Players & Pets
    [InlineData(19, 0, 8, -1)]  // old Settings
    [InlineData(1, 1, 1, -1)]   // Triggers keeps its saved sub-tab
    [InlineData(6, 1, 3, 2)]    // Items & Scrolls -> Loot / Items & Scrolls
    [InlineData(10, 1, 5, 2)]   // Leveling -> Progress / Leveling
    [InlineData(12, 1, 6, -1)]  // Maps
    [InlineData(13, 1, 7, 1)]   // Log Search -> Logs / Log Search
    [InlineData(14, 1, 7, 0)]   // Chat -> Logs / Chat
    [InlineData(16, 1, 8, -1)]  // Settings
    [InlineData(5, 2, 5, -1)]   // already current
    public void OldTabIndexMovesToTheGroupedLayout(int index, int layout, int expectedMain, int expectedSub)
    {
        Assert.Equal((expectedMain, expectedSub), AppSettings.MigrateTabIndex(index, layout));
    }
}
