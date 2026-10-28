using EQOverwatch.Core.Models;
using EQOverwatch.Core.Pipeline;

namespace EQOverwatch.Core.Tests;

internal static class TestData
{
    public const string Self = "Stackprime";

    public static string Line(string message, string time = "11:45:01") => $"[Wed Oct 21 {time} 2026] {message}";

    public static readonly string[] SampleLog =
    {
        Line("You slash a frost giant for 142 points of damage.", "11:45:01"),
        Line("Stackprime crushes a frost giant for 210 points of damage.", "11:45:02"),
        Line("Oodles hit a frost giant for 450 points of non-melee damage.", "11:45:03"),
        Line("Ream begins to cast a spell. <Complete Heal>", "11:45:04"),
        Line("Your protective skin fades.", "11:45:05"),
        Line("a frost giant has been slain by Stackprime!", "11:45:15"),
    };
}

internal sealed class RecordingDpsSink : IDpsEventSink
{
    public List<CombatEvent> Events { get; } = new();
    public int BeginCount { get; private set; }
    public int EndCount { get; private set; }

    public void BeginBatch() => BeginCount++;
    public void OnCombatEvent(in CombatEvent combatEvent) => Events.Add(combatEvent);
    public void EndBatch() => EndCount++;
}

internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
