using EQCompanion.Core.Models;
using EQCompanion.Core.Pipeline;
using EQCompanion.Core.Services;
using Xunit;
using static EQCompanion.Core.Tests.TestData;
using static EQCompanion.Core.Tests.TestHelpers;

namespace EQCompanion.Core.Tests;

public sealed class BuffTrackingPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public BuffTrackingPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string CreateLog(string character, params string[] lines)
    {
        string path = Path.Combine(_dir, $"eqlog_{character}_Frostreaver.txt");
        File.WriteAllText(path, lines.Length == 0 ? string.Empty : string.Join("\n", lines) + "\n");
        return path;
    }

    private CompanionPipeline CreatePipeline()
    {
        var pipeline = new CompanionPipeline();
        pipeline.Spells.Load(BuffTrackerServiceTests.WriteSpellFile(Path.Combine(_dir, "spells_us.txt")));
        pipeline.Buffs.SetTrackedSpells(new[] { 100, 101 });
        return pipeline;
    }

    private static Task AppendAsync(string path, params string[] lines) =>
        File.AppendAllTextAsync(path, string.Join("\n", lines) + "\n");

    [Fact]
    public async Task Live_lines_start_and_cancel_buff_timers_end_to_end()
    {
        string path = CreateLog("Stackprime", Line("an old line"));
        await using var pipeline = CreatePipeline();
        pipeline.Logs.AddFile(path);
        pipeline.StartLive();
        try
        {
            await AppendAsync(path,
                Line("You begin casting Spirit of Wolf.", "11:45:01"),
                Line("You feel quick as a wolf.", "11:45:03"));

            await WaitUntilAsync(() => pipeline.Triggers.Snapshot.Timers.Count == 1);
            ActiveTimerSnapshot timer = pipeline.Triggers.Snapshot.Timers[0];
            Assert.Equal("Stackprime: Spirit of Wolf", timer.Text);
            Assert.Equal(900, timer.DurationSeconds);
            Assert.Equal(TimerKind.Countdown, timer.Kind);

            await AppendAsync(path, Line("Your speed returns to normal.", "11:46:00"));
            await WaitUntilAsync(() => pipeline.Triggers.Snapshot.Timers.Count == 0);
        }
        finally
        {
            await pipeline.StopLiveAsync();
        }
    }

    [Fact]
    public async Task Logs_with_triggers_disabled_are_not_processed()
    {
        string path = CreateLog("Stackprime");
        await using var pipeline = CreatePipeline();
        WatchedLogConfig log = pipeline.Logs.AddFile(path);
        log.IsTriggerEnabled = false;
        log.IsPrimaryDps = true; // DPS only
        pipeline.StartLive();
        try
        {
            await AppendAsync(path, Line("You feel quick as a wolf.", "11:45:01"));
            await Task.Delay(300);
            Assert.Empty(pipeline.Triggers.Snapshot.Timers);

            log.IsTriggerEnabled = true;
            await AppendAsync(path, Line("You feel quick as a wolf.", "11:45:02"));
            await WaitUntilAsync(() => pipeline.Triggers.Snapshot.Timers.Count == 1);
        }
        finally
        {
            await pipeline.StopLiveAsync();
        }
    }

    [Fact]
    public async Task Historical_imports_never_start_buff_timers()
    {
        string path = CreateLog("Stackprime",
            Line("You begin casting Spirit of Wolf.", "11:45:01"),
            Line("You feel quick as a wolf.", "11:45:03"),
            Line("Stackprime slashes a frost giant for 100 points of damage.", "11:45:04"));
        await using var pipeline = CreatePipeline();

        await pipeline.ImportHistoricalAsync(path);

        Assert.Empty(pipeline.Triggers.Snapshot.Timers);
        Assert.Equal(0, pipeline.Buffs.PendingCastCount);
        Assert.Single(pipeline.HistoricalDps.Snapshot.Completed); // the damage line was still imported
    }
}
