using EQOverwatch.Core.Drops;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>The online shared drop table: files other players shared, merged with the counted-once rule.</summary>
public sealed class SharedDropsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqshared-" + Guid.NewGuid().ToString("N"));

    public SharedDropsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static DropStore Store()
    {
        var store = new DropStore();
        store.SetZoneProvider((_, _) => "Crushbone");
        return store;
    }

    private static void Line(DropStore store, string character, string message, int second) =>
        store.OnLine(character, "frostreaver", message, T0.AddSeconds(second));

    private static void PawnKill(DropStore store, string character, int second, string item = "Orc Scalp")
    {
        Line(store, character, "Orc pawn was slain by Kel!", second);
        Line(store, character, $"--Kel has looted an {item} from orc pawn's corpse.--", second + 2);
    }

    [Fact]
    public void A_shared_file_adds_to_everyone_and_a_fight_both_logged_counts_once()
    {
        DropStore mine = Store();
        PawnKill(mine, "Ream", 0);
        PawnKill(mine, "Ream", 30);

        DropStore theirs = Store();
        PawnKill(theirs, "Kel", 1); // the same fight as mine at 0
        PawnKill(theirs, "Kel", 100);
        SharedDropsUpload upload = theirs.ExportShared("Frostreaver")!;
        Assert.Equal(("frostreaver", 2, 2), (upload.Server, upload.Kills, upload.Drops));
        Assert.Equal(0x1f, upload.Gzip[0]);

        Assert.Equal(2, mine.ImportShared("abc123abc123abc123", "frostreaver", 500, upload.Gzip));
        Assert.Equal(3, mine.Table("frostreaver", DropScope.Everyone).Single().Kills);
        Assert.Equal(2, mine.Table("frostreaver", DropScope.ImportedFiles).Single().Kills);
        Assert.Equal(2, mine.Table("frostreaver", DropScope.Mine).Single().Kills);
        Assert.Empty(mine.Sources()); // shared files are not listed with imported files
        Assert.Equal(500, mine.SharedFiles("frostreaver")["abc123abc123abc123"]);

        // A newer copy replaces the older one; removing takes it out.
        Assert.Equal(2, mine.ImportShared("abc123abc123abc123", "frostreaver", 900, upload.Gzip));
        Assert.Equal(3, mine.Table("frostreaver", DropScope.Everyone).Single().Kills);
        mine.RemoveShared("frostreaver", "abc123abc123abc123");
        Assert.Empty(mine.SharedFiles("frostreaver"));
        Assert.Equal(2, mine.Table("frostreaver", DropScope.Everyone).Single().Kills);
    }

    [Fact]
    public void Shared_drops_without_a_kill_just_before_them_do_not_count()
    {
        DropStore theirs = Store();
        PawnKill(theirs, "Kel", 0);
        Line(theirs, "Kel", "--Kel has looted a Crown of Rile from orc pawn's corpse.--", 600); // no kill seen near it

        DropStore mine = Store();
        Assert.Equal(1, mine.ImportShared("xyz789xyz789xyz789", "frostreaver", 1, theirs.ExportShared("frostreaver")!.Gzip));
        DropMobRow pawn = mine.Table("frostreaver", DropScope.Everyone).Single();
        Assert.Equal(new[] { new DropItemRow("Orc Scalp", 1) }, pawn.Items);
    }

    [Fact]
    public void Shared_files_are_saved_and_a_damaged_one_is_refused()
    {
        string path = Path.Combine(_dir, "drops.json");
        DropStore theirs = Store();
        PawnKill(theirs, "Kel", 0);
        var store = Store();
        store.SetStoragePath(path);
        store.ImportShared("saved123saved123sa", "frostreaver", 42, theirs.ExportShared("frostreaver")!.Gzip);
        store.Flush();

        var again = Store();
        again.SetStoragePath(path);
        Assert.Equal(42, again.SharedFiles("frostreaver")["saved123saved123sa"]);
        Assert.Equal(1, again.Table("frostreaver", DropScope.Everyone).Single().Kills);
        Assert.Empty(again.Table("frostreaver", DropScope.ImportedFiles));

        Assert.Throws<InvalidDataException>(() => again.ImportShared("bad", "frostreaver", 1, new byte[] { 1, 2, 3, 4 }));
        Assert.Null(Store().ExportShared("frostreaver"));
    }
}
