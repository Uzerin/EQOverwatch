using EQCompanion.Core.Models;
using EQCompanion.Core.Pipeline;
using Xunit;
using static EQCompanion.Core.Tests.TestData;

namespace EQCompanion.Core.Tests;

public sealed class HistoricalLogImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));

    public HistoricalLogImporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteLog(IEnumerable<string> lines)
    {
        string path = Path.Combine(_dir, "eqlog_Stackprime_Frostreaver.txt");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public async Task Imports_only_combat_events_in_a_single_batch_and_normalizes_you()
    {
        string path = WriteLog(SampleLog);
        var sink = new RecordingDpsSink();
        var reports = new List<double>();

        HistoricalImportResult result = await new HistoricalLogImporter(sink)
            .ImportAsync(path, progress: new SyncProgress<double>(reports.Add));

        Assert.Equal(1, sink.BeginCount);
        Assert.Equal(1, sink.EndCount);
        Assert.Equal(4, sink.Events.Count); // 3 damage lines + 1 death; cast and fade lines ignored
        Assert.Equal(Self, sink.Events[0].Source);
        Assert.Equal(new DateTime(2026, 10, 21, 11, 45, 1), sink.Events[0].Timestamp);
        Assert.Equal(6, result.LinesRead);
        Assert.Equal("Stackprime", result.CharacterName);
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public async Task Date_filter_is_inclusive_and_uses_the_log_clock()
    {
        string path = WriteLog(SampleLog);
        var sink = new RecordingDpsSink();

        await new HistoricalLogImporter(sink).ImportAsync(
            path,
            startTime: new DateTime(2026, 10, 21, 11, 45, 2),
            endTime: new DateTime(2026, 10, 21, 11, 45, 3));

        Assert.Equal(new[] { 210, 450 }, sink.Events.Select(e => e.Amount));
        Assert.Equal(1, sink.EndCount);
    }

    [Fact]
    public async Task Cancellation_still_ends_the_batch()
    {
        string path = WriteLog(Enumerable.Repeat(SampleLog[0], 200_000));
        var sink = new RecordingDpsSink();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new HistoricalLogImporter(sink).ImportAsync(path, cancellationToken: cts.Token));

        Assert.Equal(1, sink.BeginCount);
        Assert.Equal(1, sink.EndCount);
    }
}
