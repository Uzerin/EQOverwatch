using EQOverwatch.Core.Updates;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>Start-up waits for the copy being replaced by an update, and sends a second launch to the open copy.</summary>
public class InstanceGateTests
{
    /// <summary>A fake clock that each sleep moves forward, and a list of other copies that changes with the time.</summary>
    private sealed class Sim
    {
        public DateTime Now = new(2026, 11, 16, 20, 35, 40, DateTimeKind.Utc);
        public DateTime Start;
        public Func<TimeSpan, IReadOnlyList<OtherInstance>> Running = _ => Array.Empty<OtherInstance>();

        public Sim() => Start = Now;

        public (InstanceGateResult, int) Run(int? updatedFrom) =>
            InstanceGate.Wait(() => Running(Now - Start), updatedFrom, InstanceGate.DefaultTimeout, () => Now, d => Now += d);
    }

    [Fact]
    public void With_no_other_copy_it_starts_at_once()
    {
        var sim = new Sim();
        Assert.Equal((InstanceGateResult.Proceed, 0), sim.Run(null));
        Assert.Equal(sim.Start, sim.Now);
    }

    [Fact]
    public void After_an_update_it_waits_for_the_old_copy_to_finish_saving_and_close()
    {
        // The old copy is still saving and closing its windows for 9 seconds after starting the new one.
        var sim = new Sim { };
        sim.Running = t => t < TimeSpan.FromSeconds(9) ? new[] { new OtherInstance(4242, HasVisibleWindow: t < TimeSpan.FromSeconds(1)) } : Array.Empty<OtherInstance>();

        Assert.Equal(InstanceGateResult.Proceed, sim.Run(4242).Item1);
        Assert.True(sim.Now - sim.Start >= TimeSpan.FromSeconds(9));
    }

    [Fact]
    public void An_update_from_an_older_version_without_the_hand_over_also_waits()
    {
        var sim = new Sim();
        sim.Running = t => t < TimeSpan.FromSeconds(3) ? new[] { new OtherInstance(77, HasVisibleWindow: t < TimeSpan.FromSeconds(1)) } : Array.Empty<OtherInstance>();
        Assert.Equal(InstanceGateResult.Proceed, sim.Run(null).Item1);
    }

    [Fact]
    public void A_second_copy_opened_by_accident_brings_the_open_one_forward()
    {
        var sim = new Sim();
        sim.Running = _ => new[] { new OtherInstance(500, HasVisibleWindow: true) };
        Assert.Equal((InstanceGateResult.AlreadyRunning, 500), sim.Run(null));
        Assert.True(sim.Now - sim.Start < TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void An_old_copy_that_never_closes_gives_up_after_twenty_seconds()
    {
        var sim = new Sim();
        sim.Running = _ => new[] { new OtherInstance(4242, HasVisibleWindow: false) };
        Assert.Equal((InstanceGateResult.StillRunning, 4242), sim.Run(4242));
        Assert.True(sim.Now - sim.Start >= InstanceGate.DefaultTimeout);
    }

    [Fact]
    public void The_hand_over_id_is_read_from_the_command_line()
    {
        Assert.Equal(4242, InstanceGate.UpdatedFrom(new[] { "--after-update", "4242" }));
        Assert.Null(InstanceGate.UpdatedFrom(Array.Empty<string>()));
        Assert.Null(InstanceGate.UpdatedFrom(new[] { "--after-update", "x" }));
    }

    [Fact]
    public void Crashes_are_appended_to_the_crash_log()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eqo-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            CrashLog.Write(dir, "UI thread", new InvalidOperationException("boom"), "1.0.62", new DateTime(2026, 11, 16, 20, 35, 51, DateTimeKind.Utc));
            CrashLog.Write(dir, "background thread", null, "1.0.62", new DateTime(2026, 11, 16, 20, 36, 0, DateTimeKind.Utc));
            string text = File.ReadAllText(Path.Combine(dir, CrashLog.FileName));
            Assert.Contains("2026-11-16 20:35:51 UTC  EQ Overwatch 1.0.62  (UI thread)", text);
            Assert.Contains("System.InvalidOperationException: boom", text);
            Assert.Contains("(background thread)", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
