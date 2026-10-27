using EQCompanion.Core.Models;
using EQCompanion.Core.Pipeline;
using Xunit;
using static EQCompanion.Core.Tests.TestData;
using static EQCompanion.Core.Tests.TestHelpers;

namespace EQCompanion.Core.Tests;

public sealed class CompanionPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public CompanionPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Create(string fileName, params string[] lines)
    {
        string path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, lines.Length == 0 ? string.Empty : string.Join("\n", lines) + "\n");
        return path;
    }

    private static Task AppendAsync(string path, params string[] lines) =>
        File.AppendAllTextAsync(path, string.Join("\n", lines) + "\n");

    [Fact]
    public async Task Live_tailing_and_triggers_keep_running_while_a_historical_log_is_imported()
    {
        string livePath = Create("eqlog_Stackprime_Frostreaver.txt", Line("old line that must be skipped"));
        string historyPath = Create("eqlog_Oodles_Frostreaver.txt", SampleLog);

        await using var pipeline = new CompanionPipeline();
        var errors = new List<Exception>();
        pipeline.Error += errors.Add;

        WatchedLogConfig live = pipeline.Logs.AddFile(livePath);
        live.IsPrimaryDps = true;
        pipeline.Triggers.SetTriggers(new[]
        {
            new TriggerDefinition
            {
                Name = "Protective Skin",
                Pattern = @"^Your protective skin fades\.$",
                DisplayText = "${Character}: Protective Skin Faded",
            },
        });

        pipeline.StartLive();
        try
        {
            // 1. Live combat and a live trigger.
            await AppendAsync(livePath,
                Line("You slash a frost giant for 142 points of damage.", "11:45:01"),
                Line("Your protective skin fades.", "11:45:02"));

            await WaitUntilAsync(() => pipeline.LiveDps.Snapshot.Active.Count == 1);
            await WaitUntilAsync(() => pipeline.Triggers.Snapshot.Timers.Count == 1);
            Assert.Equal("Stackprime: Protective Skin Faded", pipeline.Triggers.Snapshot.Timers[0].Text);
            Assert.Equal(142, pipeline.LiveDps.Snapshot.Active[0].TotalDamage);

            // 2. Load a different character's log into the historical tracker while live is running.
            HistoricalImportResult result = await pipeline.ImportHistoricalAsync(historyPath);

            Assert.Equal("Oodles", result.CharacterName);
            EncounterSnapshot imported = Assert.Single(pipeline.HistoricalDps.Snapshot.Completed);
            Assert.Equal(802, imported.TotalDamage);
            Assert.Equal(592, imported.Participants.Single(p => p.Name == "Oodles").TotalDamage); // "You slash" became Oodles
            Assert.False(pipeline.IsImporting);

            // 3. The live tracker was not disturbed by the import...
            Assert.Equal(142, Assert.Single(pipeline.LiveDps.Snapshot.Active).TotalDamage);
            Assert.Empty(pipeline.LiveDps.Snapshot.Completed);

            // ...and live tailing still works afterwards.
            await AppendAsync(livePath, Line("Stackprime crushes a frost giant for 210 points of damage.", "11:45:03"));
            await WaitUntilAsync(() => pipeline.LiveDps.Snapshot.Active.Count == 1 &&
                                       pipeline.LiveDps.Snapshot.Active[0].TotalDamage == 352);

            // The historical result is untouched by live traffic.
            Assert.Equal(802, Assert.Single(pipeline.HistoricalDps.Snapshot.Completed).TotalDamage);
        }
        finally
        {
            await pipeline.StopLiveAsync();
        }

        Assert.Empty(errors);
        Assert.False(pipeline.IsLiveRunning);
    }

    [Fact]
    public async Task Only_one_historical_import_runs_at_a_time()
    {
        string historyPath = Create("eqlog_Oodles_Frostreaver.txt", Enumerable.Repeat(SampleLog[0], 50_000).ToArray());
        await using var pipeline = new CompanionPipeline();

        Task<HistoricalImportResult> first = pipeline.ImportHistoricalAsync(historyPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ImportHistoricalAsync(historyPath));

        await first;
        // The gate is released afterwards.
        await pipeline.ImportHistoricalAsync(historyPath);
    }

    [Fact]
    public async Task Imported_encounters_match_what_live_tailing_produces_for_the_same_lines()
    {
        string path = Create("eqlog_Stackprime_Frostreaver.txt", SampleLog);
        var live = new EQCompanion.Core.Services.LiveDpsTracker();
        await RunLinesAsync(SampleLog, Self, LogRoute.Dps, dpsSink: live);

        await using var pipeline = new CompanionPipeline();
        await pipeline.ImportHistoricalAsync(path);

        EncounterSnapshot a = Assert.Single(live.Snapshot.Completed);
        EncounterSnapshot b = Assert.Single(pipeline.HistoricalDps.Snapshot.Completed);
        Assert.Equal((a.TargetName, a.StartTime, a.EndTime, a.TotalDamage), (b.TargetName, b.StartTime, b.EndTime, b.TotalDamage));
        Assert.Equal(a.Participants, b.Participants);
    }
}
