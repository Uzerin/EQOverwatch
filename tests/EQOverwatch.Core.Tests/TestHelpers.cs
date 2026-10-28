using System.Globalization;
using System.Threading.Channels;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Tests;

internal static class TestHelpers
{
    /// <summary>A log-clock instant on Wed Oct 21 2026 (the weekday used by every line in the test data).</summary>
    public static DateTime At(string hhmmss) =>
        DateTime.ParseExact($"2026-10-21 {hhmmss}", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static CombatEvent Hit(string source, string target, int amount, string time) =>
        new(At(time), CombatEventKind.Damage, source, target, amount, DamageKind.Melee, "hit");

    public static CombatEvent Miss(string source, string target, string time) =>
        new(At(time), CombatEventKind.Miss, source, target, 0, DamageKind.None, "hit");

    public static CombatEvent Death(string target, string killer, string time) =>
        new(At(time), CombatEventKind.Death, killer, target, 0, DamageKind.None, null);

    /// <summary>Pushes lines through the real consumer (<see cref="LogEventProcessor"/>) exactly as the pipeline would.</summary>
    public static async Task RunLinesAsync(
        IEnumerable<string> lines,
        string character,
        LogRoute routes,
        ITriggerLineSink? triggerSink = null,
        IDpsEventSink? dpsSink = null,
        DateTime? receivedUtc = null)
    {
        var channel = Channel.CreateUnbounded<LogEvent>();
        foreach (string line in lines)
        {
            channel.Writer.TryWrite(new LogEvent(character, line, receivedUtc ?? DateTime.UtcNow, routes));
        }

        channel.Writer.Complete();
        await LogEventProcessor.RunAsync(channel.Reader, triggerSink, dpsSink, onError: null, CancellationToken.None);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }
}
