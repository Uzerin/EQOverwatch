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
        public Dictionary<(string Server, string Id), (long Updated, byte[] Bytes, int Kills, int Drops)> Files { get; } = new();

        public List<string> Calls { get; } = new();

        public Task<IReadOnlyList<SharedDropsEntry>> ListAsync(string server, CancellationToken cancellation = default)
        {
            Calls.Add("list " + server);
            return Task.FromResult<IReadOnlyList<SharedDropsEntry>>(Files.Where(f => f.Key.Server == server)
                .Select(f => new SharedDropsEntry(f.Key.Id, f.Value.Updated, f.Value.Kills, f.Value.Drops)).ToList());
        }

        public Task<byte[]> DownloadAsync(string server, string id, CancellationToken cancellation = default)
        {
            Calls.Add("get " + id);
            return Task.FromResult(Files[(server, id)].Bytes);
        }

        public Task<bool> UploadAsync(string id, string key, SharedDropsUpload upload, CancellationToken cancellation = default)
        {
            Calls.Add("put " + upload.Server);
            Files[(upload.Server, id)] = (1, upload.Gzip, upload.Kills, upload.Drops);
            return Task.FromResult(true);
        }

        public Task DeleteAsync(string server, string id, string key, CancellationToken cancellation = default)
        {
            Calls.Add("delete " + server);
            Files.Remove((server, id));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_shared_drop_switches_upload_yours_add_everyone_elses_and_take_them_back_out()
    {
        using var h = new Harness();
        FillDrops(h.Pipeline.Drops);
        var service = new FakeSharedDrops();
        h.Vm.ConfigureSharedDrops(service);
        Assert.Equal(new[] { MainViewModel.DropSourceAll, MainViewModel.DropSourceMine }, h.Vm.DropSources);

        // Someone else's upload is waiting on the service.
        var friend = new DropStore();
        friend.SetZoneProvider((_, _) => "Clan Crushbone");
        Drop(friend, "Orc pawn was slain by Kel!", 1000, character: "Kel");
        Drop(friend, "--Kel has looted a Torch from orc pawn's corpse.--", 1002, character: "Kel");
        SharedDropsUpload theirs = friend.ExportShared("frostreaver")!;
        service.Files[("frostreaver", "friendfriendfriend")] = (77, theirs.Gzip, theirs.Kills, theirs.Drops);

        h.Vm.UseSharedDrops = true;
        await h.Vm.SharedDropsTask;
        Assert.Equal(MainViewModel.DropSourceEveryone, h.Vm.SelectedDropSource);
        Assert.Equal(MainViewModel.DropSourceEveryone, h.Vm.DropSources[0]);
        Assert.DoesNotContain(service.Calls, c => c.StartsWith("put", StringComparison.Ordinal)); // nothing of yours is sent
        Assert.Equal("5", h.Vm.DropRows.First(r => r.Name == "orc pawn").Kills);
        Assert.StartsWith("1 other player shared drops (1 new or updated).", h.Vm.SharedDropsStatus, StringComparison.Ordinal);

        h.Vm.ShareMyDrops = true;
        await h.Vm.SharedDropsTask;
        Assert.Contains("put frostreaver", service.Calls);
        Assert.Contains("put oakwynd", service.Calls);
        Assert.Single(service.Calls, c => c.StartsWith("get", StringComparison.Ordinal)); // read once; your own file is never read back

        h.Vm.ShareMyDrops = false;
        await h.Vm.SharedDropsTask;
        Assert.Contains("delete frostreaver", service.Calls);
        Assert.Equal("Your drops are no longer shared; your files were taken off the service.", h.Vm.SharedDropsStatus);

        h.Vm.UseSharedDrops = false;
        Assert.Equal(MainViewModel.DropSourceAll, h.Vm.SelectedDropSource);
        Assert.DoesNotContain(MainViewModel.DropSourceEveryone, h.Vm.DropSources);
        Assert.Equal("4", h.Vm.DropRows.First(r => r.Name == "orc pawn").Kills);
        Assert.Empty(h.Pipeline.Drops.SharedFiles("frostreaver"));
    }
}
