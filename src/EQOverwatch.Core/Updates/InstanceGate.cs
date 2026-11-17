namespace EQOverwatch.Core.Updates;

/// <summary>Another EQOverwatch.exe running from the same folder, and whether it shows a window (an open app, not one closing down).</summary>
public sealed record OtherInstance(int Id, bool HasVisibleWindow);

public enum InstanceGateResult
{
    /// <summary>No other copy is running: start normally.</summary>
    Proceed,

    /// <summary>Another copy is open and in use (a second launch by accident): bring its window to the front and close this one.</summary>
    AlreadyRunning,

    /// <summary>Another copy is still running after the wait: tell the user and close this one.</summary>
    StillRunning,
}

/// <summary>
/// Run at start-up before any file is touched. After Install update the old copy is still saving and closing when the new one starts,
/// so the new one waits for it to exit. A copy opened by accident while one is already open (one with a window, after a short grace
/// period) is told to bring that one forward instead.
/// </summary>
public static class InstanceGate
{
    /// <summary>The longest the new copy waits for the old one to close.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Without the update hand-over, another copy that still shows a window after this long is an open app, not one closing.</summary>
    public static readonly TimeSpan OpenAppGrace = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <param name="others">The other copies running from this folder right now.</param>
    /// <param name="updatedFrom">The process id of the copy that installed the update and started this one, or null.</param>
    /// <param name="timeout">The longest to wait.</param>
    /// <param name="clock">The current time.</param>
    /// <param name="sleep">Waits a moment between checks.</param>
    public static (InstanceGateResult Result, int OtherId) Wait(
        Func<IReadOnlyList<OtherInstance>> others, int? updatedFrom, TimeSpan timeout, Func<DateTime> clock, Action<TimeSpan> sleep)
    {
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sleep);

        DateTime start = clock();
        while (true)
        {
            IReadOnlyList<OtherInstance> running = others();
            if (running.Count == 0)
            {
                return (InstanceGateResult.Proceed, 0);
            }

            TimeSpan waited = clock() - start;
            OtherInstance? open = running.FirstOrDefault(o => o.HasVisibleWindow && o.Id != updatedFrom);
            if (open is not null && (updatedFrom is not null || waited >= OpenAppGrace))
            {
                return (InstanceGateResult.AlreadyRunning, open.Id);
            }

            if (waited >= timeout)
            {
                return (InstanceGateResult.StillRunning, running[0].Id);
            }

            sleep(Poll);
        }
    }

    /// <summary>The id after "--after-update" on the command line, or null.</summary>
    public static int? UpdatedFrom(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i].Equals(AfterUpdateArgument, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id))
            {
                return id;
            }
        }

        return null;
    }

    public const string AfterUpdateArgument = "--after-update";
}

/// <summary>Writes unhandled errors to crash.log in the settings folder, so a crash can be looked into afterwards.</summary>
public static class CrashLog
{
    public const string FileName = "crash.log";

    /// <summary>Past this size the log starts over.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>Appends one entry. Never throws: a failure to write the log must not hide the original error.</summary>
    public static void Write(string folder, string source, Exception? error, string version, DateTime nowUtc)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, FileName);
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                File.Delete(path);
            }

            string entry =
                $"===== {nowUtc.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)} UTC  " +
                $"EQ Overwatch {version}  ({source})" + Environment.NewLine +
                (error?.ToString() ?? "(no details)") + Environment.NewLine + Environment.NewLine;
            File.AppendAllText(path, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // nothing more can be done
        }
    }
}
