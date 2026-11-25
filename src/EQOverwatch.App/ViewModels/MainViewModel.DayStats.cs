using System.Globalization;
using System.IO;
using EQOverwatch.Core.History;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Session;

namespace EQOverwatch.App.ViewModels;

/// <summary>One character's row on the Session tab.</summary>
public sealed record SessionRowView(string Character, string Played, string Xp, string Platinum, int Kills, int SkillUps, IReadOnlyList<ZoneStretch> Zones, string PlatPerHour = "", string Day = "");

/// <summary>A zone on the Experience tab's XP by zone table.</summary>
public sealed record ZoneXpRowView(string Zone, string XpPerHour, string Xp, string Hours, int Gains);

/// <summary>One row of the Session tab's plat by zone table.</summary>
public sealed record ZonePlatRowView(string Zone, string Platinum, string PerHour, string Hours);

/// <summary>One zone stretch under the Session tab's timeline.</summary>
public sealed record ZoneRowView(string Zone, string From, string To, string Length);

/// <summary>One spell on the Spell Stats tab's Casts table.</summary>
public sealed record SpellStatRowView(string Character, string Spell, int Casts, int Fizzles, string FizzlePercent, int Interrupts, string InterruptPercent, int Blocked, string BlockedBy);

/// <summary>One spell on one mob on the Spell Stats tab's Resists table.</summary>
public sealed record ResistStatRowView(string Character, string Spell, string Mob, int Landed, int Resisted, string ResistPercent);

/// <summary>A boss fight under the selected raid on the Attendance tab, with who did nothing in it.</summary>
public sealed record IdleFightRowView(string Fight, string When, int IdleCount, string Idle);

// Spell alerts and the quiet-log alert, the Session and Spell Stats tabs (today's numbers from each log), and the idle list on Attendance.
public sealed partial class MainViewModel
{
    private const string AllSpellCharacters = "All characters";
    private static readonly TimeSpan DayScanEvery = TimeSpan.FromSeconds(30);

    private DayScanner? _dayScanner;
    private DateTime _lastDayScan;
    private int _dayScanRunning;
    private readonly QuietLogWatch _quietLogs = new();

    // ─────────────────────────── alert switches ───────────────────────────

    [ObservableProperty]
    private bool _takeHoldAlert = true;

    [ObservableProperty]
    private bool _resistStreakAlert = true;

    [ObservableProperty]
    private bool _feignDeathAlert = true;

    [ObservableProperty]
    private bool _rootMezAlert = true;

    [ObservableProperty]
    private bool _quietLogAlert = true;

    partial void OnTakeHoldAlertChanged(bool value)
    {
        _pipeline.SpellAlerts.TakeHoldAlert = value;
        SaveUnlessRestoring();
    }

    partial void OnResistStreakAlertChanged(bool value)
    {
        _pipeline.SpellAlerts.ResistAlert = value;
        SaveUnlessRestoring();
    }

    partial void OnFeignDeathAlertChanged(bool value)
    {
        _pipeline.SpellAlerts.FeignDeathAlert = value;
        SaveUnlessRestoring();
    }

    partial void OnRootMezAlertChanged(bool value)
    {
        _pipeline.SpellAlerts.RootMezAlert = value;
        SaveUnlessRestoring();
    }

    partial void OnQuietLogAlertChanged(bool value) => SaveUnlessRestoring();

    /// <summary>Says "X's log went quiet" for a watched character whose log stopped during a fight.</summary>
    private void CheckQuietLogs(DateTime nowUtc)
    {
        if (!QuietLogAlert)
        {
            return;
        }

        var watched = new HashSet<string>(Logs.Select(l => l.CharacterName), StringComparer.OrdinalIgnoreCase);
        bool fighting = _pipeline.LiveDps.Snapshot.Active.Count > 0;
        foreach (string character in _quietLogs.Check(nowUtc, _pipeline.Activity.LastLines().Where(l => watched.Contains(l.Character)), fighting))
        {
            string text = $"{character}'s log went quiet";
            _pipeline.Triggers.StartAlert(BoxAlertSourceId, BoxAlertSourceName, character, text, AggroBannerSeconds, nowUtc);
            _pipeline.Triggers.PublishAudio(BoxAlertSourceId, BoxAlertSourceName, character, text, nowUtc);
        }
    }

    // ─────────────────────────── Session and Spell Stats tabs ───────────────────────────

    [ObservableProperty]
    private IReadOnlyList<SessionRowView> _sessionRows = Array.Empty<SessionRowView>();

    [ObservableProperty]
    private SessionRowView? _selectedSessionRow;

    [ObservableProperty]
    private IReadOnlyList<ZoneStretch> _selectedZoneStretches = Array.Empty<ZoneStretch>();

    [ObservableProperty]
    private IReadOnlyList<ZoneRowView> _zoneRows = Array.Empty<ZoneRowView>();

    [ObservableProperty]
    private string _sessionStatus = "Reading today's logs…";

    [ObservableProperty]
    private IReadOnlyList<string> _spellStatCharacters = new[] { AllSpellCharacters };

    [ObservableProperty]
    private string _spellStatCharacter = AllSpellCharacters;

    [ObservableProperty]
    private IReadOnlyList<SpellStatRowView> _spellStatRows = Array.Empty<SpellStatRowView>();

    [ObservableProperty]
    private IReadOnlyList<ResistStatRowView> _resistStatRows = Array.Empty<ResistStatRowView>();

    private IReadOnlyList<SpellDayRow> _spellDay = Array.Empty<SpellDayRow>();
    private IReadOnlyList<ResistDayRow> _resistDay = Array.Empty<ResistDayRow>();

    partial void OnSelectedSessionRowChanged(SessionRowView? value) => ShowZones(value);

    partial void OnSpellStatCharacterChanged(string value) => FilterSpellStats();

    private sealed record DayResult(IReadOnlyList<SessionSummary> Sessions, IReadOnlyList<SpellDayRow> Spells, IReadOnlyList<ResistDayRow> Resists,
        IReadOnlyList<(DateTime Day, SessionSummary Summary)> Week, IReadOnlyList<(string Zone, double Xp, int Gains, double Hours)> ZoneXp,
        IReadOnlyList<ResistDayRow> WeekResists, IReadOnlyList<(string Zone, double Platinum, double Hours)>? ZonePlat = null);

    /// <summary>The Session tab's platinum by zone over the last seven days.</summary>
    [ObservableProperty]
    private IReadOnlyList<ZonePlatRowView> _zonePlatRows = Array.Empty<ZonePlatRowView>();

    public void ShowZonePlat(IReadOnlyList<(string Zone, double Platinum, double Hours)> zones)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ZonePlatRows = zones
            .Select(z => new ZonePlatRowView(z.Zone, z.Platinum.ToString("N1", culture),
                z.Hours >= 0.05 ? (z.Platinum / z.Hours).ToString("N0", culture) : "—", z.Hours.ToString("0.0", culture)))
            .ToList();
    }

    /// <summary>Your spells' landed and resisted per mob over the last seven days, for the mob card.</summary>
    private IReadOnlyList<ResistDayRow> _weekResists = Array.Empty<ResistDayRow>();

    /// <summary>A spell needs this many casts on a mob before the mob card shows its resist rate.</summary>
    public const int MobCardMinCasts = 5;

    /// <summary>The Experience tab's XP by zone over the last seven days.</summary>
    [ObservableProperty]
    private IReadOnlyList<ZoneXpRowView> _zoneXpRows = Array.Empty<ZoneXpRowView>();

    public void ShowZoneXp(IReadOnlyList<(string Zone, double Xp, int Gains, double Hours)> zones)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        ZoneXpRows = zones
            .Select(z => new ZoneXpRowView(z.Zone, (z.Xp / z.Hours).ToString("0.0", culture) + "%", z.Xp.ToString("0.0", culture) + "%",
                z.Hours.ToString("0.0", culture), z.Gains))
            .ToList();
    }

    public IReadOnlyList<string> SessionViews { get; } = new[] { "Today", "Last 7 days" };

    /// <summary>0: today; 1: the last seven days.</summary>
    [ObservableProperty]
    private int _sessionViewIndex;

    public bool SessionWeek => SessionViewIndex == 1;

    [ObservableProperty]
    private IReadOnlyList<SessionRowView> _sessionWeekRows = Array.Empty<SessionRowView>();

    public bool ShowSessionToday => !SessionWeek;

    partial void OnSessionViewIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SessionWeek));
        OnPropertyChanged(nameof(ShowSessionToday));
    }

    private DayResult? _dayResult;

    private void RefreshDayStats(DateTime nowUtc)
    {
        CheckQuietLogs(nowUtc);
        if (Interlocked.Exchange(ref _dayResult, null) is { } result)
        {
            ShowDayStats(result.Sessions, result.Spells, result.Resists, result.Week);
            ShowZoneXp(result.ZoneXp);
            ShowZonePlat(result.ZonePlat ?? Array.Empty<(string, double, double)>());
            _weekResists = result.WeekResists;
        }

        RebuildMobLevelsOnce();
        if (nowUtc - _lastDayScan < DayScanEvery || Logs.Count == 0 || Interlocked.CompareExchange(ref _dayScanRunning, 1, 0) != 0)
        {
            return;
        }

        _lastDayScan = nowUtc;
        _dayScanner ??= new DayScanner(name => _pipeline.Spells.Database.FindByName(name));
        DayScanner scanner = _dayScanner;
        var logs = Logs.Select(l => (l.FilePath, l.CharacterName)).ToList();
        DateTime nowLocal = _clock();
        _ = Task.Run(() =>
        {
            try
            {
                scanner.Update(logs, nowLocal);
                Interlocked.Exchange(ref _dayResult, new DayResult(scanner.Sessions(nowLocal), scanner.Spells(), scanner.Resists(), scanner.Week(nowLocal), scanner.ZoneXp(nowLocal), scanner.WeekResists(), scanner.ZonePlat(nowLocal))); // shown on the next tick
            }
            catch (Exception)
            {
                // read again next time
            }
            finally
            {
                Interlocked.Exchange(ref _dayScanRunning, 0);
            }
        });
    }

    private int _mobLevelRebuildRunning;

    /// <summary>
    /// Once, in the background: reads every watched log for /consider levels and the zone each was seen in, so the saved level ranges
    /// are kept per zone. Ranges saved before keep working as the fallback for mobs not seen in a zone.
    /// </summary>
    private void RebuildMobLevelsOnce()
    {
        if (_mobLevels.ZonesRebuilt || Logs.Count == 0 || Interlocked.CompareExchange(ref _mobLevelRebuildRunning, 1, 0) != 0)
        {
            return;
        }

        var paths = Logs.Select(l => l.FilePath).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MobLevelStore store = _mobLevels;
        _ = Task.Run(() =>
        {
            try
            {
                var sightings = new List<(string, int, string?)>();
                foreach (string path in paths)
                {
                    try
                    {
                        sightings.AddRange(MobLevelStore.ScanLog(path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // a log that cannot be read is skipped
                    }
                }

                store.ObserveAll(sightings);
            }
            finally
            {
                Interlocked.Exchange(ref _mobLevelRebuildRunning, 0);
            }
        });
    }

    /// <summary>Puts today's numbers on the Session and Spell Stats tabs (UI thread).</summary>
    public void ShowDayStats(IReadOnlyList<SessionSummary> sessions, IReadOnlyList<SpellDayRow> spells, IReadOnlyList<ResistDayRow> resists,
        IReadOnlyList<(DateTime Day, SessionSummary Summary)>? week = null)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string? selected = SelectedSessionRow?.Character;
        SessionRowView Row(SessionSummary s, string day) => new(
            s.Character,
            FormatPlayed(s.PlayedSeconds),
            s.XpPercent.ToString("0.0", culture) + "%",
            s.Platinum.ToString("N0", culture),
            s.Kills,
            s.SkillUps,
            s.Zones,
            PlatPerHour(s.Platinum, s.PlayedSeconds, culture),
            day);
        SessionRows = sessions.Select(s => Row(s, string.Empty)).ToList();
        SessionWeekRows = (week ?? Array.Empty<(DateTime, SessionSummary)>())
            .Select(w => Row(w.Summary, w.Day.ToString("ddd MMM d", culture)))
            .ToList();
        SelectedSessionRow = SessionRows.FirstOrDefault(r => r.Character.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? SessionRows.FirstOrDefault();
        SessionStatus = SessionRows.Count == 0
            ? "Nobody has played today yet."
            : $"Today since midnight. Played time leaves out gaps over {DayAccumulator.OfflineGap.TotalMinutes:0} minutes; kills are mobs your characters saw die.";

        _spellDay = spells;
        _resistDay = resists;
        var names = spells.Select(s => s.Character).Concat(resists.Select(r => r.Character))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        names.Insert(0, AllSpellCharacters);
        if (!names.SequenceEqual(SpellStatCharacters))
        {
            string keep = SpellStatCharacter;
            SpellStatCharacters = names;
            SpellStatCharacter = names.Contains(keep) ? keep : AllSpellCharacters;
        }

        var mobs = resists.Select(r => r.Mob).Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        mobs.Insert(0, AllResistMobs);
        if (!mobs.SequenceEqual(ResistMobs))
        {
            string keep = ResistMob;
            ResistMobs = mobs;
            ResistMob = mobs.Contains(keep) ? keep : AllResistMobs;
        }

        FilterSpellStats();
    }

    public const string AllResistMobs = "All mobs";

    /// <summary>The mobs in today's Resists table, with "All mobs" first.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private IReadOnlyList<string> _resistMobs = new[] { AllResistMobs };

    /// <summary>The mob the Resists table shows (276).</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private string _resistMob = AllResistMobs;

    partial void OnResistMobChanged(string value) => FilterSpellStats();

    private void FilterSpellStats()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        bool all = SpellStatCharacter is null or AllSpellCharacters;
        SpellStatRows = _spellDay
            .Where(s => all || s.Character.Equals(SpellStatCharacter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Casts).ThenBy(s => s.Character, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Spell, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SpellStatRowView(s.Character, s.Spell, s.Casts, s.Fizzles, s.FizzlePercent.ToString("0.0", culture) + "%",
                s.Interrupts, s.InterruptPercent.ToString("0.0", culture) + "%", s.Blocked, s.BlockedBy))
            .ToList();
        ResistStatRows = _resistDay
            .Where(r => all || r.Character.Equals(SpellStatCharacter, StringComparison.OrdinalIgnoreCase))
            .Where(r => ResistMob is null or AllResistMobs || r.Mob.Equals(ResistMob, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Resisted).ThenBy(r => r.Spell, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Mob, StringComparer.OrdinalIgnoreCase)
            .Select(r => new ResistStatRowView(r.Character, r.Spell, r.Mob, r.Landed, r.Resisted, r.ResistPercent.ToString("0", culture) + "%"))
            .ToList();
    }

    private void ShowZones(SessionRowView? row)
    {
        IReadOnlyList<ZoneStretch> zones = row?.Zones ?? Array.Empty<ZoneStretch>();
        SelectedZoneStretches = zones;
        ZoneRows = zones
            .Select(z => new ZoneRowView(
                z.Zone.Length > 0 ? z.Zone : "(before the first zone change)",
                z.Start.ToString("HH:mm", CultureInfo.CurrentCulture),
                z.End.ToString("HH:mm", CultureInfo.CurrentCulture),
                FormatPlayed(z.Seconds)))
            .ToList();
    }

    /// <summary>Platinum per hour played, or empty with under five minutes played.</summary>
    public static string PlatPerHour(double platinum, double playedSeconds, CultureInfo culture) =>
        playedSeconds < 300 ? string.Empty : (platinum / (playedSeconds / 3600)).ToString("N0", culture);

    internal static string FormatPlayed(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : $"{span.Minutes}m";
    }

    // ─────────────────────────── Attendance: idle in boss fights ───────────────────────────

    [ObservableProperty]
    private IReadOnlyList<IdleFightRowView> _idleFightRows = Array.Empty<IdleFightRowView>();

    private void RebuildIdleFights()
    {
        if (SelectedAttendanceRaid is not { } raid)
        {
            IdleFightRows = Array.Empty<IdleFightRowView>();
            return;
        }

        IEnumerable<EncounterSnapshot> fights = Encounters.Select(e => e.Snapshot).OfType<EncounterSnapshot>();
        IdleFightRows = RaidIdle.Find(raid.Record, fights)
            .OrderByDescending(f => f.Start)
            .Select(f => new IdleFightRowView(
                f.Boss,
                f.Start.ToString("HH:mm", CultureInfo.CurrentCulture),
                f.Idle.Count,
                f.Idle.Count == 0 ? "Everyone took part" : string.Join(", ", f.Idle)))
            .ToList();
    }
}
