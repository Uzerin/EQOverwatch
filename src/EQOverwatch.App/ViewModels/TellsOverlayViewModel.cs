using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Progress;

namespace EQOverwatch.App.ViewModels;

/// <summary>One unanswered tell on the Tells overlay.</summary>
public sealed record TellOverlayRow(string To, string From, string Text, string Ago);

/// <summary>One open dynamic zone on the Tells overlay: "Vex Thal 4:55:12", red under 15 minutes.</summary>
public sealed record DzOverlayRow(string Zone, string Remaining, bool Ending, string Members);

/// <summary>The Tells overlay: the newest unanswered tells to your characters, refreshed from the inbox.</summary>
public sealed partial class TellsOverlayViewModel : ObservableObject
{
    public const int MaxRows = 6;

    private readonly TellInbox _inbox;
    private readonly OverlayNoticeFeed? _notices;
    private readonly DynamicZoneTracker? _zones;

    /// <summary>Under this much time left a zone's timer turns red.</summary>
    public static readonly TimeSpan EndingSoon = TimeSpan.FromMinutes(15);
    private readonly Func<DateTime> _utcNow;
    private long _version = -1;
    private long _noticeVersion = -1;
    private int _noticeCount;
    private DateTime _lastBuild;

    public TellsOverlayViewModel(TellInbox inbox, Func<DateTime>? utcNow = null, OverlayNoticeFeed? notices = null, DynamicZoneTracker? zones = null)
    {
        _zones = zones;
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _notices = notices;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Rolls and linkdead players from the last 30 seconds, newest first.</summary>
    [ObservableProperty]
    private IReadOnlyList<OverlayNotice> _recentNotices = Array.Empty<OverlayNotice>();

    [ObservableProperty]
    private IReadOnlyList<TellOverlayRow> _rows = Array.Empty<TellOverlayRow>();

    /// <summary>Your open dynamic zones, the one closing soonest first.</summary>
    [ObservableProperty]
    private IReadOnlyList<DzOverlayRow> _zoneRows = Array.Empty<DzOverlayRow>();

    [ObservableProperty]
    private string _countText = "No unanswered tells";

    /// <summary>Rebuilds when the inbox changed, and every 30 seconds so "2m" ages.</summary>
    public void Tick()
    {
        DateTime now = _utcNow();
        if (_zones is not null)
        {
            List<DzOverlayRow> zones = _zones.Current(now)
                .Select(z => new DzOverlayRow(z.Zone, Clock(z.Remaining(now)), z.Remaining(now) < EndingSoon, string.Join(", ", z.Members)))
                .ToList();
            if (!zones.SequenceEqual(ZoneRows))
            {
                ZoneRows = zones;
            }
        }

        if (_notices is not null)
        {
            IReadOnlyList<OverlayNotice> current = _notices.Current(now);
            if (_notices.Version != _noticeVersion || current.Count != _noticeCount)
            {
                _noticeVersion = _notices.Version;
                _noticeCount = current.Count;
                RecentNotices = current;
            }
        }

        if (_inbox.Version == _version && now - _lastBuild < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _version = _inbox.Version;
        _lastBuild = now;
        List<TellEntry> waiting = _inbox.All().Where(t => t.IsUnanswered).ToList();
        CountText = waiting.Count == 0 ? "No unanswered tells" : $"{waiting.Count} unanswered";
        Rows = waiting.Take(MaxRows).Select(t => new TellOverlayRow(t.Character, t.From, t.Text, Ago(now - t.Utc))).ToList();
    }

    /// <summary>"4:55:12".</summary>
    public static string Clock(TimeSpan span) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}");

    public static string Ago(TimeSpan span) =>
        span.TotalMinutes < 1 ? "now"
        : span.TotalHours < 1 ? ((int)span.TotalMinutes).ToString(CultureInfo.CurrentCulture) + "m"
        : span.TotalDays < 1 ? ((int)span.TotalHours).ToString(CultureInfo.CurrentCulture) + "h"
        : ((int)span.TotalDays).ToString(CultureInfo.CurrentCulture) + "d";
}
