namespace EQOverwatch.Core.Progress;

/// <summary>One short notice for the Tells overlay: its text, when it came, and its kind ("roll" or "linkdead").</summary>
public sealed record OverlayNotice(string Kind, string Text, DateTime Utc);

/// <summary>
/// The short notices shown on the Tells overlay for <see cref="Lifetime"/>: /random rolls and players gone linkdead. Every character's log
/// sees the same roll, so one already shown in the last few seconds is not added again.
/// </summary>
public sealed class OverlayNoticeFeed
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SameLine = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly List<OverlayNotice> _notices = new();
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    /// <summary>Adds a notice unless the same text came in the last few seconds; returns whether it was added.</summary>
    public bool Add(string kind, string text, DateTime utc)
    {
        lock (_gate)
        {
            _notices.RemoveAll(n => utc - n.Utc > Lifetime);
            if (_notices.Any(n => n.Text == text && (utc - n.Utc).Duration() < SameLine))
            {
                return false;
            }

            _notices.Add(new OverlayNotice(kind, text, utc));
        }

        Interlocked.Increment(ref _version);
        return true;
    }

    /// <summary>The notices still showing at <paramref name="utc"/>, newest first.</summary>
    public IReadOnlyList<OverlayNotice> Current(DateTime utc)
    {
        lock (_gate)
        {
            return Enumerable.Reverse(_notices).Where(n => utc - n.Utc < Lifetime && n.Utc <= utc + SameLine).OrderByDescending(n => n.Utc).ToList();
        }
    }
}
