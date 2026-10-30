using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Sync;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class MultiPcSyncTests
{
    [Fact]
    public void A_message_only_opens_with_the_same_code()
    {
        byte[] sealedBytes = SyncProtocol.Encrypt("K7QMT-4TXD2", new byte[] { 1, 2, 3, 4 });

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, SyncProtocol.Decrypt("k7qmt 4txd2", sealedBytes)); // case, dash and space do not matter
        Assert.Null(SyncProtocol.Decrypt("K7QMT-4TXD3", sealedBytes));
    }

    [Fact]
    public void A_changed_message_is_rejected()
    {
        byte[] sealedBytes = SyncProtocol.Encrypt("ABCDE-FGHJK", new byte[] { 9, 9, 9 });
        sealedBytes[^1] ^= 0xFF;

        Assert.Null(SyncProtocol.Decrypt("ABCDE-FGHJK", sealedBytes));
    }

    [Fact]
    public void New_codes_look_right_and_differ()
    {
        string a = SyncProtocol.NewCode();
        string b = SyncProtocol.NewCode();

        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", a);
        Assert.NotEqual(a, b);
        Assert.Equal(10, SyncProtocol.NormalizeCode(a).Length);
    }

    [Fact]
    public async Task Files_sent_with_the_right_code_are_saved_with_their_write_time()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-sync-" + Guid.NewGuid().ToString("N"));
        const string code = "ABCDE-FGHJK";
        using var host = new CharacterSyncHost(dir, () => code, tcpPort: 0, discoveryPort: 0);
        try
        {
            var received = new TaskCompletionSource<(string Pc, IReadOnlyList<string> Paths)>();
            host.Received += (pc, paths) => received.TrySetResult((pc, paths));
            host.Start();
            Assert.True(host.Port > 0);

            DateTime written = new(2026, 10, 20, 18, 30, 0, DateTimeKind.Utc);
            var bundle = new SyncBundle("Basement PC", new List<SyncFile>
            {
                new("Nasti_frostreaver-Inventory.txt", written, "Location\tName\tID\tCount\tSlots\nGeneral1\tRusty Dagger\t1\t1\t0"),
                new("..\\..\\evil_frostreaver-Inventory.txt", written, "x"),
                new("notes.txt", written, "not an output file"),
            });

            SyncSendResult result = await CharacterSyncSender.SendAsync("127.0.0.1", code, bundle, CancellationToken.None, host.Port);
            Assert.Equal(SyncSendResult.Sent, result);

            var (pc, paths) = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Basement PC", pc);
            string folder = Path.Combine(dir, "Basement PC");
            string saved = Path.Combine(folder, "Nasti_frostreaver-Inventory.txt");
            Assert.Contains(saved, paths);
            Assert.All(paths, p => Assert.StartsWith(folder, p)); // a name with folders in it cannot reach outside the PC's folder
            Assert.Equal(written, File.GetLastWriteTimeUtc(saved));
            Assert.False(File.Exists(Path.Combine(folder, "notes.txt")));
            Assert.False(File.Exists(Path.Combine(dir, "evil_frostreaver-Inventory.txt")));
        }
        finally
        {
            host.Stop();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_wrong_code_is_not_accepted_and_nothing_is_saved()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-sync-" + Guid.NewGuid().ToString("N"));
        using var host = new CharacterSyncHost(dir, () => "ABCDE-FGHJK", tcpPort: 0, discoveryPort: 0);
        try
        {
            host.Start();
            var bundle = new SyncBundle("PC2", new List<SyncFile> { new("Nasti_frostreaver-Inventory.txt", DateTime.UtcNow, "x") });

            SyncSendResult result = await CharacterSyncSender.SendAsync("127.0.0.1", "ZZZZZ-ZZZZZ", bundle, CancellationToken.None, host.Port);

            Assert.Equal(SyncSendResult.WrongCode, result);
            Assert.False(Directory.Exists(Path.Combine(dir, "PC2")));
        }
        finally
        {
            host.Stop();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Nothing_listening_is_reported_as_unreachable()
    {
        SyncSendResult result = await CharacterSyncSender.SendAsync(
            "127.0.0.1", "ABCDE-FGHJK", new SyncBundle("PC2", new List<SyncFile>()), CancellationToken.None, port: 1);

        Assert.Equal(SyncSendResult.Unreachable, result);
    }

    [Fact]
    public void Only_changed_output_files_are_collected()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-collect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "Nasti_frostreaver-Inventory.txt");
            File.WriteAllText(path, "data");
            File.WriteAllText(Path.Combine(dir, "readme_something.txt"), "ignored");

            List<SyncFile> first = CharacterSyncSender.CollectChanged(dir, new Dictionary<string, DateTime>());
            SyncFile file = Assert.Single(first);
            Assert.Equal("Nasti_frostreaver-Inventory.txt", file.Name);

            var sent = new Dictionary<string, DateTime> { [file.Name] = file.WrittenUtc };
            Assert.Empty(CharacterSyncSender.CollectChanged(dir, sent));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_received_file_is_credited_to_the_pc_it_came_from()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-from-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "Nasti_frostreaver-Inventory.txt");
            File.WriteAllLines(path, new[] { "Location\tName\tID\tCount\tSlots", "General1\tRusty Dagger\t1\t1\t0" });
            var store = new CharacterDataStore();

            Assert.True(store.Import(path, "Basement PC"));

            Assert.Equal("Basement PC", store.Find("Nasti")!.FromPc);
            ItemHit hit = Assert.Single(CharacterPlanner.FindItems(store.All(), store.SharedBanks(), "dagger"));
            Assert.Equal("Nasti (Basement PC)", hit.Character);
            Assert.Empty(CharacterPlanner.FindItems(store.All(), store.SharedBanks(), "dagger", "oakwynd"));
            Assert.Single(CharacterPlanner.FindItems(store.All(), store.SharedBanks(), "dagger", "Frostreaver"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
