using System.IO;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Drops;
using Xunit;

namespace EQOverwatch.App.Tests;

/// <summary>The Drop table tab: per-server tables by mob and by item, search, stats links, and drops files.</summary>
public partial class MainViewModelTests
{
    private static readonly DateTime DropsT0 = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);

    private static void Drop(DropStore store, string message, int second, string server = "frostreaver", string character = "Ream") =>
        store.OnLine(character, server, message, DropsT0.AddSeconds(second));

    private static void FillDrops(DropStore store)
    {
        store.SetZoneProvider((_, _) => "Clan Crushbone");
        for (int i = 0; i < 4; i++)
        {
            Drop(store, "Orc pawn was slain by Jannder!", i * 20);
            Drop(store, "--Jannder has looted an Orc Scalp from orc pawn's corpse.--", i * 20 + 2);
        }

        Drop(store, "--Jannder has looted a Torch from orc pawn's corpse.--", 63);
        Drop(store, "Emperor Crush was slain by Jannder!", 200);
        Drop(store, "--Jannder has looted a Torch from Emperor Crush's corpse.--", 202);
        Drop(store, "--You have looted a Torch from orc pawn's corpse.--", 0, "oakwynd", "Abundance");
    }

    [Fact]
    public void The_drop_table_shows_each_mob_with_its_items_and_rates_per_server()
    {
        using var h = new Harness();
        FillDrops(h.Pipeline.Drops);
        h.Vm.SelectedDropSource = MainViewModel.DropSourceMine; // rebuilds the table
        h.Vm.SelectedDropSource = MainViewModel.DropSourceAll;

        Assert.Equal(new[] { "frostreaver", "oakwynd" }, h.Vm.DropServers);
        Assert.Equal("frostreaver", h.Vm.SelectedDropServer);
        Assert.Equal(new[] { MainViewModel.AllDropZones, "Clan Crushbone" }, h.Vm.DropZones);
        Assert.Equal(
            new[]
            {
                new DropRowView("orc pawn", "Clan Crushbone", "4", string.Empty, string.Empty, 0, true, null),
                new DropRowView("Orc Scalp", string.Empty, string.Empty, "4", "100.0%", 60, false, "Orc Scalp"),
                new DropRowView("Torch", string.Empty, string.Empty, "1", "25.0%", 15, false, "Torch"),
                new DropRowView("Emperor Crush", "Clan Crushbone", "1", string.Empty, string.Empty, 0, true, null),
                new DropRowView("Torch", string.Empty, string.Empty, "1", "100.0%", 60, false, "Torch"),
            },
            h.Vm.DropRows);

        h.Vm.IsDropsByItem = true;
        Assert.Equal("Item / mob", h.Vm.DropFirstColumn);
        Assert.Equal(new[] { "Orc Scalp", "orc pawn", "Torch", "Emperor Crush", "orc pawn" }, h.Vm.DropRows.Select(r => r.Name));
        Assert.Equal("2", h.Vm.DropRows[2].Drops); // Torch in all

        h.Vm.DropSearch = "crush";
        Assert.Equal(new[] { "Torch", "Emperor Crush", "orc pawn" }, h.Vm.DropRows.Select(r => r.Name)); // an item one of whose mobs matches

        h.Vm.SelectedDropServer = "oakwynd";
        h.Vm.DropSearch = string.Empty;
        Assert.Equal(new[] { "Torch", "orc pawn" }, h.Vm.DropRows.Select(r => r.Name));

        h.Vm.OpenItemStatsCommand.Execute("Crown of Rile");
        Assert.Equal("https://everquest.allakhazam.com/search.html?q=Crown%20of%20Rile", h.OpenedUrls.Single());
    }

    [Fact]
    public void Drops_files_are_exported_imported_and_removed_from_the_sharing_view()
    {
        using var h = new Harness();
        FillDrops(h.Pipeline.Drops);
        h.Vm.SelectedDropSource = MainViewModel.DropSourceMine;
        h.Vm.IsDropsSharing = true;
        Assert.Equal("frostreaver", h.Vm.DropExportServer);

        string file = Path.Combine(h.Dir, "drops.eqodrops");
        h.Dialogs.DropsExportPath = file;
        h.Vm.ExportDropsCommand.Execute(null);
        Assert.StartsWith("Saved drops.eqodrops: 6 drops on frostreaver", h.Vm.DropShareStatus, StringComparison.Ordinal);

        h.Dialogs.DropsImportFile = file;
        h.Vm.ImportDropsCommand.Execute(null);
        Assert.StartsWith("Couldn't import that file: That file is your own export.", h.Vm.DropShareStatus, StringComparison.Ordinal);

        // Someone else's file.
        var friend = new DropStore();
        friend.SetTag("Kel-91B");
        friend.SetZoneProvider((_, _) => "Clan Crushbone");
        Drop(friend, "Orc pawn was slain by Kel!", 1000, character: "Kel");
        Drop(friend, "--Kel has looted a Torch from orc pawn's corpse.--", 1002, character: "Kel");
        string theirs = Path.Combine(h.Dir, "kel.eqodrops");
        friend.Export("frostreaver", theirs);
        h.Dialogs.DropsImportFile = theirs;
        h.Vm.ImportDropsCommand.Execute(null);
        Assert.Equal("Imported Kel-91B's drops on frostreaver: 1 drops and 1 kills. A fight you both logged counts once.", h.Vm.DropShareStatus);
        Assert.StartsWith("Kel-91B on frostreaver: 1 drops, 1 kills", h.Vm.DropImports.Single().Text, StringComparison.Ordinal);

        h.Vm.SelectedDropSource = MainViewModel.DropSourceAll;
        h.Vm.IsDropsByMob = true;
        Assert.Equal("5", h.Vm.DropRows.First(r => r.Name == "orc pawn").Kills);

        h.Vm.RemoveDropImportCommand.Execute(h.Vm.DropImports.Single());
        Assert.Empty(h.Vm.DropImports);
        Assert.Equal("4", h.Vm.DropRows.First(r => r.Name == "orc pawn").Kills);

        h.Vm.DropTag = "bad/tag";
        Assert.Equal("A tag is 1 to 24 letters, digits, spaces, - or _.", h.Vm.DropShareStatus);
        h.Vm.DropTag = "Guild Drops";
        Assert.Equal("Guild Drops", h.Pipeline.Drops.Tag);
    }

    private sealed class FakeSharedDrops : ISharedDropsService
    {
        /// <summary>The merged table of each server: when it was made, by how many players, and the file.</summary>
        public Dictionary<string, (long Updated, int Players, byte[] Gzip)> Merged { get; } = new();

        public long LastMerge { get; set; }

        public List<string> Calls { get; } = new();

        public List<(string Id, string Version)> Pings { get; } = new();

        public Task<MergedDropsDownload?> DownloadMergedAsync(string server, long have, CancellationToken cancellation = default)
        {
            Calls.Add("merged " + server);
            if (!Merged.TryGetValue(server, out var m))
            {
                return Task.FromResult<MergedDropsDownload?>(null);
            }

            return Task.FromResult<MergedDropsDownload?>(new MergedDropsDownload(m.Updated, m.Players, have >= m.Updated ? null : m.Gzip, LastMerge));
        }

        public Task<bool> UploadAsync(string id, string key, SharedDropsUpload upload, CancellationToken cancellation = default)
        {
            Calls.Add("put " + upload.Server);
            return Task.FromResult(true);
        }

        public Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default)
        {
            Calls.Add("delete " + server);
            return Task.CompletedTask;
        }

        public Task PingAsync(string id, string version, CancellationToken cancellation = default)
        {
            Pings.Add((id, version));
            return Task.CompletedTask;
        }
    }

    /// <summary>A merged table the service would make: orc pawn killed <paramref name="kills"/> times, with that many Orc Scalps.</summary>
    private static byte[] MergedFile(int kills)
    {
        var file = new DropStore.MergedFile
        {
            Format = "EQOverwatchDropsMerged", Version = 1, Server = "frostreaver",
            Zones = { "Clan Crushbone" }, Mobs = { "orc pawn" }, Items = { "Orc Scalp" },
            Kills = { new long[] { 0, 0, kills, 1 } }, Drops = { new long[] { 0, 0, 0, kills, 1 } },
        };
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            System.Text.Json.JsonSerializer.Serialize(gzip, file);
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task Shared_drops_use_the_daily_merged_table_plus_your_own_drops_since_your_last_upload()
    {
        using var h = new Harness();
        FillDrops(h.Pipeline.Drops); // 4 orc pawn kills of your own
        var service = new FakeSharedDrops();
        h.Vm.UseSharedDrops = false; // sharing starts on; this test switches it on itself below
        h.Vm.ShareMyDrops = false;
        h.Vm.ConfigureSharedDrops(service);
        Assert.Equal(new[] { MainViewModel.DropSourceAll, MainViewModel.DropSourceMine }, h.Vm.DropSources);

        // Today's merge (UTC midnight, so it is "today" whenever the test runs): 10 kills by 5 other players.
        long morning = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).ToUnixTimeSeconds();
        service.LastMerge = morning;
        service.Merged["frostreaver"] = (morning, 5, MergedFile(10));

        h.Vm.UseSharedDrops = true;
        await h.Vm.SharedDropsTask;
        Assert.Equal(MainViewModel.DropSourceEveryone, h.Vm.SelectedDropSource);
        Assert.DoesNotContain(service.Calls, c => c.StartsWith("put", StringComparison.Ordinal)); // nothing of yours is sent
        Assert.Equal("14", h.Vm.DropRows.First(r => r.Name == "orc pawn").Kills); // the merged 10 and your own 4
        Assert.StartsWith("Shared drops from 5 players, updated today ", h.Vm.SharedDropsStatus, StringComparison.Ordinal);
        Assert.Equal(new MergedDropsInfo(morning, 5), h.Pipeline.Drops.MergedInfo("frostreaver"));

        // Sharing uploads each server's drops; your 4 kills still count on top, as the merge does not have them yet.
        h.Vm.ShareMyDrops = true;
        await h.Vm.SharedDropsTask;
        Assert.Single(service.Calls, c => c == "put frostreaver");
        Assert.Contains("put oakwynd", service.Calls);
        Assert.Equal("14", h.Vm.DropRows.First(r => r.Name == "orc pawn").Kills);

        // The next merge has your upload in it: your 4 kills are not added twice, and nothing new is uploaded again.
        long next = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        service.LastMerge = next;
        service.Merged["frostreaver"] = (next, 6, MergedFile(14));
        await h.Vm.SyncSharedDropsNowCommand.ExecuteAsync(null);
        Assert.Equal("14", h.Vm.DropRows.First(r => r.Name == "orc pawn").Kills);
        Assert.Single(service.Calls, c => c == "put frostreaver");
        Assert.Equal(6, h.Pipeline.Drops.MergedInfo("frostreaver")!.Players);

        // Without the merged table nothing else is downloaded until after the next daily merge.
        Assert.True(MainViewModel.NextMergedCheck(next, DateTime.UtcNow) > DateTime.UtcNow.AddHours(23));
        Assert.True(MainViewModel.NextMergedCheck(DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds(), DateTime.UtcNow) <= DateTime.UtcNow.AddHours(1));

        h.Vm.ShareMyDrops = false;
        await h.Vm.SharedDropsTask;
        Assert.Contains("delete frostreaver", service.Calls);
        Assert.StartsWith("Your drops are no longer shared", h.Vm.SharedDropsStatus, StringComparison.Ordinal);

        h.Vm.UseSharedDrops = false;
        Assert.Equal(MainViewModel.DropSourceAll, h.Vm.SelectedDropSource);
        Assert.DoesNotContain(MainViewModel.DropSourceEveryone, h.Vm.DropSources);
        Assert.Equal("4", h.Vm.DropRows.First(r => r.Name == "orc pawn").Kills);
    }

    [Fact]
    public async Task The_anonymous_player_count_is_sent_once_a_day_unless_switched_off()
    {
        using var h = new Harness();
        var service = new FakeSharedDrops();
        h.Vm.ConfigureSharedDrops(service);
        Assert.True(h.Vm.CountMeIn); // on by default

        h.Vm.Refresh();
        await h.Vm.PlayerCountTask;
        (string id, string version) = Assert.Single(service.Pings);
        Assert.Equal(EQOverwatch.Core.AppInfo.Version, version);
        Assert.InRange(id.Length, 16, 40);

        h.Vm.Refresh();
        await h.Vm.PlayerCountTask;
        Assert.Single(service.Pings); // once a day

        using var off = new Harness();
        var quiet = new FakeSharedDrops();
        off.Vm.ConfigureSharedDrops(quiet);
        off.Vm.CountMeIn = false;
        off.Vm.Refresh();
        await off.Vm.PlayerCountTask;
        Assert.Empty(quiet.Pings);
    }
}
