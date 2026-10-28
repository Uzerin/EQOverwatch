using EQOverwatch.Core.Pipeline;
using Xunit;
using static EQOverwatch.Core.Tests.TestData;
using static EQOverwatch.Core.Tests.TestHelpers;

namespace EQOverwatch.Core.Tests;

public sealed class LiveBackfillTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public LiveBackfillTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Create(params string[] lines)
    {
        string path = Path.Combine(_dir, "eqlog_Stackprime_Frostreaver.txt");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public async Task A_fight_still_in_progress_stays_active_after_the_catch_up()
    {
        string path = Create(
            Line("You slash a frost giant for 142 points of damage.", "11:45:01"),
            Line("You slash a frost giant for 100 points of damage.", "11:45:03"));
        await using var pipeline = new CompanionPipeline();

        await pipeline.BackfillLiveAsync(path, null, At("11:45:05"));

        var active = Assert.Single(pipeline.LiveDps.Snapshot.Active);
        Assert.Equal(242, active.TotalDamage);
        Assert.Empty(pipeline.LiveDps.Snapshot.Completed);
        Assert.False(pipeline.LiveDps.KeepActiveEncountersOnEndBatch);
    }

    [Fact]
    public async Task A_fight_that_ended_long_ago_is_closed_by_the_catch_up()
    {
        string path = Create(
            Line("You slash a frost giant for 142 points of damage.", "08:00:01"),
            Line("You slash a frost giant for 100 points of damage.", "08:00:03"));
        await using var pipeline = new CompanionPipeline();

        await pipeline.BackfillLiveAsync(path, null, At("11:45:05"));

        Assert.Empty(pipeline.LiveDps.Snapshot.Active);
        Assert.Equal(242, Assert.Single(pipeline.LiveDps.Snapshot.Completed).TotalDamage);
    }

    [Fact]
    public async Task The_catch_up_replaces_earlier_live_data()
    {
        string path = Create(Line("You slash a gnoll for 5 points of damage.", "11:45:01"));
        await using var pipeline = new CompanionPipeline();
        pipeline.LiveDps.OnCombatEvent(Hit("Oodles", "a bat", 9, "10:00:00"));

        await pipeline.BackfillLiveAsync(path, null, At("11:45:02"));

        Assert.Equal("a gnoll", Assert.Single(pipeline.LiveDps.Snapshot.Active).TargetName);
        Assert.Empty(pipeline.LiveDps.Snapshot.Completed);
    }
}
