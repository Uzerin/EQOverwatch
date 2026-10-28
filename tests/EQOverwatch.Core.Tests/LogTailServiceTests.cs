using System.Threading.Channels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Pipeline;
using Xunit;
using static EQOverwatch.Core.Tests.TestData;

namespace EQOverwatch.Core.Tests;

public sealed class LogTailServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqc-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public LogTailServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "eqlog_Stackprime_Frostreaver.txt");
        File.WriteAllText(_path, Line("old line that must be skipped") + "\n");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<LogEvent> ReadAsync(Channel<LogEvent> channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await channel.Reader.ReadAsync(cts.Token);
    }

    [Fact]
    public async Task Seeks_to_end_on_startup_and_applies_routing_flags()
    {
        var config = new WatchedLogConfig(_path) { IsPrimaryDps = true }; // triggers default to enabled
        var channel = Channel.CreateUnbounded<LogEvent>();
        var tail = new LogTailService(channel.Writer, TimeSpan.FromMilliseconds(10));
        try
        {
            tail.AddLog(config); // positions the file at its end right now
            tail.Start();

            await File.AppendAllTextAsync(_path, Line("You slash a frost giant for 142 points of damage.") + "\n");
            LogEvent evt = await ReadAsync(channel);

            Assert.Equal("Stackprime", evt.CharacterName);
            Assert.Equal(LogRoute.Triggers | LogRoute.Dps, evt.Routes);
            Assert.Equal("You slash a frost giant for 142 points of damage.", evt.Line[LogLineFormat.MessageOffset..]);
            Assert.False(evt.HasLogTimestamp); // timestamp parsing is deferred to the DPS consumer
        }
        finally
        {
            await tail.StopAsync();
        }
    }

    [Fact]
    public async Task Emits_a_line_only_after_its_newline_arrives()
    {
        var config = new WatchedLogConfig(_path);
        var channel = Channel.CreateUnbounded<LogEvent>();
        var tail = new LogTailService(channel.Writer, TimeSpan.FromMilliseconds(10));
        try
        {
            tail.AddLog(config);
            tail.Start();

            await File.AppendAllTextAsync(_path, Line("Your protective skin "));
            await Task.Delay(150);
            Assert.False(channel.Reader.TryRead(out _));

            await File.AppendAllTextAsync(_path, "fades.\r\n");
            LogEvent evt = await ReadAsync(channel);

            Assert.Equal("Your protective skin fades.", evt.Line[LogLineFormat.MessageOffset..]);
            Assert.Equal(LogRoute.Triggers, evt.Routes);
        }
        finally
        {
            await tail.StopAsync();
        }
    }

    [Fact]
    public async Task Skips_lines_while_no_route_is_enabled_and_resumes_when_one_is()
    {
        var config = new WatchedLogConfig(_path) { IsTriggerEnabled = false };
        var channel = Channel.CreateUnbounded<LogEvent>();
        var tail = new LogTailService(channel.Writer, TimeSpan.FromMilliseconds(10));
        try
        {
            tail.AddLog(config);
            tail.Start();

            await File.AppendAllTextAsync(_path, Line("nobody is listening") + "\n");
            await Task.Delay(150);
            Assert.False(channel.Reader.TryRead(out _));

            config.IsTriggerEnabled = true;
            await File.AppendAllTextAsync(_path, Line("now somebody is") + "\n");
            LogEvent evt = await ReadAsync(channel);

            Assert.Equal("now somebody is", evt.Line[LogLineFormat.MessageOffset..]);
        }
        finally
        {
            await tail.StopAsync();
        }
    }
}
