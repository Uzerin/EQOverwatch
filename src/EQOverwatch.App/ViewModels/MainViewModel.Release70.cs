using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Attendance;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>A raid group on the Attendance tab's Groups view.</summary>
public sealed record RaidGroupCard(string Title, string Members, string Warning, bool HasWarning);

/// <summary>A guild member who was not in the selected raid.</summary>
public sealed record GuildMissRow(string Name, int Level, string Class, string Rank, string LastOn);

// Files EverQuest just wrote are read at once; /played on the Characters tab; the Attendance tab's Groups and Missed views.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan PlayedRefreshEvery = TimeSpan.FromSeconds(10);
    private readonly List<WeakReference<CharacterDataViewModel>> _openProfiles = new();
    private readonly Dictionary<string, (string Guild, DateTime Taken, List<GuildMember> Members)?> _guilds = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPlayedRefresh;

    private void RefreshRelease70(DateTime nowUtc)
    {
        ReadWrittenFiles();
        RunLearnedBossTimers(nowUtc);
        RefreshChat();
        RefreshRelease72(nowUtc);
        if (nowUtc - _lastPlayedRefresh >= PlayedRefreshEvery)
        {
            _lastPlayedRefresh = nowUtc;
            RefreshAccountColumns(nowUtc);
            foreach (WatchedLogConfig log in Logs)
            {
                log.PlayedText = _pipeline.Progress.Played(log.CharacterName, log.ServerName) is { } played ? FormatMinutes(played.Minutes) : "—";
                log.CreatedText = _pipeline.Progress.Legacy(log.CharacterName, log.ServerName).Born is { } born
                    ? born.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
                    : "—";
            }
        }
    }

    /// <summary>"13d 22h 59m", "5h 02m" or "12m".</summary>
    public static string FormatMinutes(long minutes)
    {
        long d = minutes / 1440, h = minutes % 1440 / 60, m = minutes % 60;
        return d > 0 ? $"{d}d {h}h {m:00}m" : h > 0 ? $"{h}h {m:00}m" : $"{m}m";
    }

    /// <summary>Reads the files the logs just said were written: character files, raid dumps and guild dumps.</summary>
    internal void ReadWrittenFiles()
    {
        IReadOnlyList<(string Character, string FileName)> written = _pipeline.OutputFiles.TakeAll();
        string folder = EQOverwatch.Core.Discovery.EqDirectoryScanner.Normalize(EqFolderPath);
        if (written.Count == 0 || folder.Length == 0)
        {
            return;
        }

        bool characters = false, raids = false;
        var refreshed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string _, string fileName) in written)
        {
            string path = Path.Combine(folder, fileName);
            if (CharacterOutputParser.TryParseFileName(fileName, out OutputFileInfo info))
            {
                if (_characterData.Import(path))
                {
                    characters = true;
                    refreshed.Add(info.Name);
                }
            }
            else if (RaidRosterStore.TryParseFileName(fileName, out _, out _))
            {
                raids = true;
            }
            else if (GuildRoster.TryParseFileName(fileName, out _, out string server, out _))
            {
                _guilds.Remove(server);
                raids = true;
            }
        }

        if (characters)
        {
            RefreshCharacterInfo();
            RebuildCharacterTools(); // Items & Scrolls shows the new file without "Read new files"
            foreach (WeakReference<CharacterDataViewModel> reference in _openProfiles)
            {
                if (reference.TryGetTarget(out CharacterDataViewModel? vm) && refreshed.Contains(vm.CharacterName))
                {
                    vm.Reload();
                }
            }
        }

        if (raids)
        {
            RefreshAttendanceCommand.Execute(null);
            LearnDumpedPlayers();
        }
    }

    /// <summary>Class and level from the newest guild and raid roster dumps go into the player roster (Class column, overlay labels).</summary>
    private void LearnDumpedPlayers() =>
        DumpRosterImport.Learn(_pipeline.Roster, EQOverwatch.Core.Discovery.EqDirectoryScanner.Normalize(EqFolderPath), _raidRoster.Records());

    // ─────────────────────────── learned boss timers ───────────────────────────

    public static readonly Guid LearnedBossSourceId = new("3c9e1f72-8a4d-4b6e-9d05-7f2a1c6e8b34");
    private const string LearnedBossSourceName = "Learned boss timers";

    [ObservableProperty]
    private bool _learnedBossTimers = true;

    [ObservableProperty]
    private string _learnedBossStatus = "Timings are learned from each boss's killed fights; a boss needs 2 logged kills.";

    partial void OnLearnedBossTimersChanged(bool value) => SaveUnlessRestoring();

    // Per live fight: the last boss cast already counted from, and whether the first-cast bars were started.
    private readonly Dictionary<int, (DateTime LastCast, bool FirstsStarted)> _learnedFights = new();

    /// <summary>Starts a bar to each learned ability of the boss being fought: from the fight's start for its first cast, then from each cast.</summary>
    internal void RunLearnedBossTimers(DateTime nowUtc)
    {
        if (!LearnedBossTimers)
        {
            return;
        }

        IReadOnlyList<EncounterSnapshot> active = _pipeline.LiveDps.Snapshot.Active;
        foreach (int gone in _learnedFights.Keys.Where(id => active.All(a => a.Id != id)).ToList())
        {
            _learnedFights.Remove(gone);
        }

        if (active.Count == 0)
        {
            return;
        }

        var ruled = new HashSet<string>(_pipeline.RaidBosses.Rules.Select(r => r.SpellName), StringComparer.OrdinalIgnoreCase);
        DateTime nowLocal = _clock();
        foreach (EncounterSnapshot fight in active)
        {
            IReadOnlyList<LearnedAbility> abilities = _pipeline.BossAbilities.For(fight.TargetName);
            if (abilities.Count == 0)
            {
                continue;
            }

            (DateTime lastCast, bool firsts) = _learnedFights.TryGetValue(fight.Id, out var state) ? state : (DateTime.MinValue, false);
            if (!firsts)
            {
                foreach (LearnedAbility a in abilities.Where(a => a.FirstSeconds is not null && !ruled.Contains(a.Spell)))
                {
                    double left = a.FirstSeconds!.Value - (nowLocal - fight.StartTime).TotalSeconds;
                    if (left > 1 && !fight.CastRows.Any(c => c.Spell.Equals(a.Spell, StringComparison.OrdinalIgnoreCase)))
                    {
                        _pipeline.Triggers.StartTimer(LearnedBossSourceId, LearnedBossSourceName, fight.TargetName, a.Spell, left, nowUtc, TimerOverlayCategory.RaidTriggers);
                    }
                }

                firsts = true;
            }

            foreach (BossCastSnapshot cast in fight.CastRows.Where(c => c.Time > lastCast).OrderBy(c => c.Time))
            {
                lastCast = cast.Time;
                if (ruled.Contains(cast.Spell)
                    || abilities.FirstOrDefault(a => a.Spell.Equals(cast.Spell, StringComparison.OrdinalIgnoreCase)) is not { EverySeconds: { } every })
                {
                    continue;
                }

                double left = every - (nowLocal - cast.Time).TotalSeconds;
                if (left > 1)
                {
                    _pipeline.Triggers.StartTimer(LearnedBossSourceId, LearnedBossSourceName, fight.TargetName, cast.Spell, left, nowUtc, TimerOverlayCategory.RaidTriggers);
                }
            }

            _learnedFights[fight.Id] = (lastCast, firsts);
        }
    }

    // ─────────────────────────── Attendance: Groups and Missed views ───────────────────────────

    public IReadOnlyList<string> RosterViews { get; } = new[] { "Everyone", "Groups", "Guild members not there" };

    [ObservableProperty]
    private int _rosterViewIndex;

    [ObservableProperty]
    private IReadOnlyList<RaidGroupCard> _raidGroupCards = Array.Empty<RaidGroupCard>();

    [ObservableProperty]
    private IReadOnlyList<GuildMissRow> _guildMissRows = Array.Empty<GuildMissRow>();

    [ObservableProperty]
    private string _guildStatus = string.Empty;

    public bool ShowRosterList => RosterViewIndex == 0;

    public bool ShowRosterGroups => RosterViewIndex == 1;

    public bool ShowRosterMissed => RosterViewIndex == 2;

    partial void OnRosterViewIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ShowRosterList));
        OnPropertyChanged(nameof(ShowRosterGroups));
        OnPropertyChanged(nameof(ShowRosterMissed));
    }

    /// <summary>The newest guild dump for the server, read once until a new one is written.</summary>
    private (string Guild, DateTime Taken, List<GuildMember> Members)? GuildFor(string server)
    {
        if (!_guilds.TryGetValue(server, out var guild))
        {
            guild = GuildRoster.ReadNewest(EQOverwatch.Core.Discovery.EqDirectoryScanner.Normalize(EqFolderPath), server);
            _guilds[server] = guild;
        }

        return guild;
    }

    /// <summary>Fills the Groups and Missed views (and the guild rank of each raider) for the selected raid.</summary>
    private void RebuildRaidViews(RaidAttendance? raid, IReadOnlyList<RosterDisplayRow> rows)
    {
        if (raid is null)
        {
            RaidGroupCards = Array.Empty<RaidGroupCard>();
            GuildMissRows = Array.Empty<GuildMissRow>();
            GuildStatus = string.Empty;
            return;
        }

        RaidGroupCards = RaidGroups.Of(raid.Members)
            .Select(g => new RaidGroupCard(
                g.Group == 0 ? "Not in a group" : $"Group {g.Group}",
                string.Join("\n", g.Members.Select(m => $"{m.Name} · {m.Class}")),
                g.Group != 0 && !g.HasHealer ? "No healer" : string.Empty,
                g.Group != 0 && !g.HasHealer))
            .ToList();

        var guild = GuildFor(raid.Server);
        GuildMissRows = guild is { } gd
            ? RaidGroups.Missed(raid, gd.Members)
                .Select(m => new GuildMissRow(m.Name, m.Level, m.Class, m.Rank, m.LastOn?.ToString("MMM d", CultureInfo.CurrentCulture) ?? string.Empty))
                .ToList()
            : Array.Empty<GuildMissRow>();
        GuildStatus = guild is { } g2
            ? $"{g2.Guild} roster from {g2.Taken.ToString("MMM d HH:mm", CultureInfo.CurrentCulture)}: members on in the 30 days before the raid who were not in it."
            : "No guild roster yet: type /outputfile guild in game.";
    }

    /// <summary>A raider's guild rank from the newest guild dump, or empty.</summary>
    private string GuildRankOf(string server, string name) =>
        GuildFor(server) is { } guild && guild.Members.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } member
            ? member.Rank
            : string.Empty;
}
