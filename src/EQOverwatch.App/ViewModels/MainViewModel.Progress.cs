using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Presentation;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Parsing;
using EQOverwatch.Core.Progress;

namespace EQOverwatch.App.ViewModels;

/// <summary>A tell on the Tells tab.</summary>
public sealed record TellRowView(string When, string To, string From, string Message, string Answered, bool IsUnanswered);

// Skills, tradeskills and faction (shown in each character's window) and the Tells tab, all read from the logs.
public sealed partial class MainViewModel
{
    public const string AllTellCharacters = "All characters";
    public const string TellsUnanswered = "Unanswered";
    public const string TellsAll = "All tells";
    private static readonly int[] TellKeepDayChoices = { 7, 30, 90, 0 };
    private static readonly TimeSpan ProgressFlushEvery = TimeSpan.FromSeconds(30);

    private DateTime _lastProgressFlush;
    private long _tellsVersionShown = -1;
    private bool _historyScanning;
    private SkillCaps? _skillCaps;
    private string? _skillCapsFolder;

    public IReadOnlyList<string> TellKeepChoices { get; } = new[] { "7 days", "30 days", "90 days", "Always" };

    public IReadOnlyList<string> TellShowChoices { get; } = new[] { TellsUnanswered, TellsAll };

    public ObservableCollection<string> TellCharacters { get; } = new() { AllTellCharacters };

    [ObservableProperty]
    private int _tellKeepIndex = 1;

    [ObservableProperty]
    private string _tellShow = TellsUnanswered;

    [ObservableProperty]
    private string _tellCharacter = AllTellCharacters;

    [ObservableProperty]
    private IReadOnlyList<TellRowView> _tellRows = Array.Empty<TellRowView>();

    [ObservableProperty]
    private string _tellStatus = string.Empty;

    /// <summary>Days tells are kept (0 = always).</summary>
    public int TellKeepDays => TellKeepDayChoices[Math.Clamp(TellKeepIndex, 0, TellKeepDayChoices.Length - 1)];

    private void RestoreTells(Core.Settings.AppSettings settings)
    {
        int index = Array.IndexOf(TellKeepDayChoices, settings.TellKeepDays);
        TellKeepIndex = index >= 0 ? index : 1;
        _pipeline.TellInbox.KeepDays = TellKeepDays;
    }

    partial void OnTellKeepIndexChanged(int value)
    {
        _pipeline.TellInbox.KeepDays = TellKeepDays;
        RebuildTells();
        if (!_restoring)
        {
            RequestSave();
        }
    }

    partial void OnTellShowChanged(string value) => RebuildTells();

    partial void OnTellCharacterChanged(string value) => RebuildTells();

    [RelayCommand]
    private void MarkAllTellsRead()
    {
        _pipeline.TellInbox.MarkAllRead();
        _ = Task.Run(_pipeline.TellInbox.Flush);
        RebuildTells();
    }

    /// <summary>Loads the saved numbers and reads the history of every watched log not read before.</summary>
    private void InitializeProgress()
    {
        if (_dataDirectory is not null)
        {
            _pipeline.Progress.SetStoragePath(Path.Combine(_dataDirectory, "progress.json"));
            _pipeline.TellInbox.SetStoragePath(Path.Combine(_dataDirectory, "tells.json"));
            _pipeline.DynamicZones.SetStoragePath(Path.Combine(_dataDirectory, "dynamiczones.json"));
            var unread = new List<(string Path, string Character, string Server)>();
            foreach (WatchedLogConfig log in Logs)
            {
                if (LogFileName.TryParse(log.FilePath, out string character, out string server)
                    && (!_pipeline.Progress.IsScanned(character, server) || !_pipeline.TellInbox.IsScanned(character, server)))
                {
                    unread.Add((log.FilePath, character, server));
                }
            }

            if (unread.Count > 0)
            {
                _ = ReadProgressHistoryAsync(unread);
            }
        }

        RebuildTells();
    }

    /// <summary>Reads each log once for skill-ups, combines, faction and tells.</summary>
    private async Task ReadProgressHistoryAsync(List<(string Path, string Character, string Server)> logs)
    {
        if (_historyScanning)
        {
            return;
        }

        _historyScanning = true;
        try
        {
            ProgressStore progress = _pipeline.Progress;
            TellInbox inbox = _pipeline.TellInbox;
            foreach ((string path, string character, string server) in logs)
            {
                await Task.Run(() =>
                {
                    ProgressStore.LogScan progressScan = progress.BeginScan(character, server);
                    TellInbox.LogScan tellScan = inbox.BeginScan(character, server);
                    long? last = LogHistoryReader.Read(
                        path,
                        message => ProgressStore.Wanted(message) || TellInbox.Wanted(message),
                        (message, time) =>
                        {
                            progressScan.Line(message, time);
                            tellScan.Line(message, time);
                        });
                    progressScan.Commit(last is not null);
                    tellScan.Commit(last is not null);
                });
            }
        }
        finally
        {
            _historyScanning = false;
        }

        // Each character's class and level follow the persona that was on at the end of its log.
        foreach ((_, string character, string server) in logs)
        {
            string persona = _pipeline.Progress.CurrentPersona(character, server);
            if (persona.Length > 0)
            {
                int level = _pipeline.Progress.Personas(character, server).FirstOrDefault(p => p.Persona == persona)?.Level ?? 0;
                _pipeline.Roster.LearnPlayer(character, persona, level, server);
            }
        }

        _ = Task.Run(() =>
        {
            _pipeline.Progress.Flush();
            _pipeline.TellInbox.Flush();
        });
        RebuildTells();

        // Inventory files read before the logs said which persona was on are filed again now that it is known.
        _characterData.ScanAll(EqFolderPath);
    }

    /// <summary>The persona (class code) that was on when a character's inventory file was written, and whether the log has said.</summary>
    private (string ClassCode, bool Sure) PersonaWhenWritten(string character, string server, DateTime utc)
    {
        string persona = _pipeline.Progress.PersonaAt(character, server, utc);
        bool read = _pipeline.Progress.IsScanned(character, server);
        if (persona.Length == 0)
        {
            persona = _pipeline.Roster.GetClass(character, server);
        }

        return (Core.Roster.EqClass.Abbreviate(persona), read);
    }

    private void RefreshProgress(DateTime nowUtc)
    {
        if (nowUtc - _lastProgressFlush >= ProgressFlushEvery)
        {
            _lastProgressFlush = nowUtc;
            _ = Task.Run(() =>
            {
                _pipeline.Progress.Flush();
                _pipeline.TellInbox.Flush();
            });
        }

        if (_pipeline.TellInbox.Version != _tellsVersionShown)
        {
            RebuildTells();
        }
    }

    private void RebuildTells()
    {
        TellInbox inbox = _pipeline.TellInbox;
        _tellsVersionShown = inbox.Version;
        IReadOnlyList<TellEntry> all = inbox.All();

        List<string> characters = new[] { AllTellCharacters }
            .Concat(all.Select(t => t.Character).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            .ToList();
        ObservableSync.SyncValues(TellCharacters, characters);
        if (!characters.Contains(TellCharacter))
        {
            TellCharacter = AllTellCharacters;
            return; // that rebuilds
        }

        DateTime today = _clock().Date;
        TellRows = all
            .Where(t => TellShow == TellsAll || t.IsUnanswered)
            .Where(t => TellCharacter == AllTellCharacters || t.Character.Equals(TellCharacter, StringComparison.OrdinalIgnoreCase))
            .Select(t =>
            {
                DateTime local = t.Utc.ToLocalTime();
                string when = local.Date == today
                    ? local.ToString("t", CultureInfo.CurrentCulture)
                    : local.ToString("MMM d, t", CultureInfo.CurrentCulture);
                return new TellRowView(when, t.Character, t.From, t.Text, t.Answered ? "Yes" : string.Empty, t.IsUnanswered);
            })
            .ToList();

        int unanswered = inbox.UnansweredCount();
        TellStatus = unanswered == 0
            ? "No unanswered tells."
            : $"{unanswered:N0} unanswered tell{(unanswered == 1 ? string.Empty : "s")}. A tell counts as answered once that character sends the player a tell back.";
    }

    // ───────────────────────── the character window's Skills, Tradeskills and Faction ─────────────────────────

    /// <summary>The character's skills (A to Z) with the most each can be for its class and level.</summary>
    private IReadOnlyList<SkillRowView> SkillRowsFor(string character, string server, string persona)
    {
        SkillCaps caps = CurrentSkillCaps();
        PersonaInfo? info = _pipeline.Progress.Personas(character, server).FirstOrDefault(p => p.Persona == persona);
        string className = persona.Length > 0 ? persona : _pipeline.Roster.GetClass(character, server);
        int level = info is { Level: > 0 } ? info.Level : _pipeline.Roster.GetLevel(character, server);
        return _pipeline.Progress.Skills(character, server, DateTime.UtcNow, persona)
            .Select(s =>
            {
                int max = caps.Cap(className, s.Skill, level);
                return new SkillRowView(
                    s.Skill,
                    s.Value,
                    max > 0 ? max.ToString(CultureInfo.CurrentCulture) : "—",
                    s.GainedToday > 0 ? "+" + s.GainedToday.ToString(CultureInfo.CurrentCulture) : string.Empty,
                    DescribeWhen(s.LastUtc),
                    max > 0 && s.Value >= max);
            })
            .ToList();
    }

    private IReadOnlyList<TradeskillRowView> TradeskillRowsFor(string character, string server) =>
        _pipeline.Progress.Tradeskills(character, server)
            .Select(t => new TradeskillRowView(t.Recipe, t.Combines, t.Made, t.Failed,
                t.SuccessPercent.ToString("0", CultureInfo.CurrentCulture) + "%", DescribeWhen(t.LastUtc),
                t.SkillUps, t.SkillUps == 0 ? "—" : ((double)t.Combines / t.SkillUps).ToString("0.0", CultureInfo.CurrentCulture)))
            .ToList();

    private IReadOnlyList<FactionRowView> FactionRowsFor(string character, string server, string persona)
    {
        // The persona's faction file gives the standing when it was written; the log's hits since then move it on.
        string code = EQOverwatch.Core.Roster.EqClass.Abbreviate(persona);
        CharacterData? data = _characterData.Find(character, server);
        FactionFileData? file = data?.Factions is { } files
            ? (code.Length > 0 && files.TryGetValue(code, out FactionFileData? mine) ? mine : files.Count == 1 ? files.Values.First() : null)
            : null;
        var standings = new Dictionary<string, (FactionStanding Row, long Change, int Limit)>(StringComparer.OrdinalIgnoreCase);
        if (file is not null)
        {
            IReadOnlyDictionary<string, (long Change, int Limit)> changes = _pipeline.Progress.FactionChangesSince(character, server, persona, file.CapturedUtc);
            foreach (FactionStanding row in file.Rows)
            {
                (long change, int limit) = changes.TryGetValue(row.Name, out (long, int) c) ? c : (0, 0);
                standings[row.Name] = (row, change, limit);
            }
        }

        string Standing(string faction)
        {
            if (!standings.TryGetValue(faction, out (FactionStanding Row, long Change, int Limit) s))
            {
                return string.Empty;
            }

            long max = s.Row.Value + (long)s.Row.PointsToMax;
            long value = s.Limit > 0 ? max : Math.Min(max, s.Row.Value + s.Change);
            return $"{value.ToString("N0", CultureInfo.CurrentCulture)} ({CharacterOutputParser.StandingName(value)})";
        }

        IReadOnlyList<FactionProgress> logged = _pipeline.Progress.Factions(character, server, persona);
        var rows = logged
            .Select(f => new FactionRowView(
                f.Faction,
                f.Session == 0 ? string.Empty : Signed(f.Session),
                Signed(f.AllTime),
                f.Hits.ToString("N0", CultureInfo.CurrentCulture),
                f.UsualHit == 0 ? string.Empty : Signed(f.UsualHit),
                f.ComesFrom,
                f.Limit > 0 ? "Can't get better" : f.Limit < 0 ? "Can't get worse" : string.Empty,
                Standing(f.Faction)))
            .ToList();

        // Factions the log never mentioned are listed from the file when they are not at 0.
        var seen = new HashSet<string>(logged.Select(f => f.Faction), StringComparer.OrdinalIgnoreCase);
        rows.AddRange(standings.Values
            .Where(s => s.Row.Value != 0 && !seen.Contains(s.Row.Name))
            .Select(s => new FactionRowView(s.Row.Name, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Standing(s.Row.Name))));
        return rows.OrderBy(r => r.Faction, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private CharacterProgressSource ProgressSourceFor(string character, string server) => new(
        persona => SkillRowsFor(character, server, persona),
        () => TradeskillRowsFor(character, server),
        persona => FactionRowsFor(character, server, persona),
        () => _pipeline.Progress.Personas(character, server)
            .Select(p => new PersonaChoice(
                p.Persona,
                (p.Persona.Length > 0 ? p.Persona : _pipeline.Roster.GetClass(character, server) is { Length: > 0 } known ? known : "Unknown class") + (p.Level > 0 ? " " + p.Level.ToString(CultureInfo.CurrentCulture) : string.Empty)
                    + (p.IsCurrent ? " (current)" : string.Empty),
                p.IsCurrent))
            .ToList(),
        persona => CreateFocusViewModel(character, server, persona),
        () => _pipeline.Progress.Flags(character, server)
            .Select(f => new FlagRowView(f.Text, f.Utc.ToLocalTime().ToString("ddd MMM d yyyy, HH:mm", CultureInfo.CurrentCulture)))
            .ToList(),
        () => _pipeline.Progress.KeyRingAdds(character, server).Select(k => k.Text).ToList(),
        () => _pipeline.Progress.AchievementsDone(character, server).Select(a => a.Text).ToList(),
        () => AccountOf(character, server),
        account => SetAccount(character, server, account),
        () => AccountNamesOn(server),
        () => LevelHistoryFor(character, server),
        () => ChallengeRowsFor(character, server),
        new AaSource(
            persona => AaRowsFor(character, server, persona),
            (persona, name, rank) => { _pipeline.Progress.SetAa(character, server, persona, name, rank, DateTime.UtcNow); _ = Task.Run(_pipeline.Progress.Flush); },
            (persona, name) => { _pipeline.Progress.RemoveAa(character, server, persona, name, DateTime.UtcNow); _ = Task.Run(_pipeline.Progress.Flush); },
            () => AaCatalogFor().Select(a => a.Name).ToList()));

    private IReadOnlyList<EQOverwatch.Core.Spells.AaInfo>? _aaCatalog;
    private string? _aaCatalogFolder;

    /// <summary>The ability names and descriptions of the EverQuest folder's dbstr_us.txt, read once per folder.</summary>
    private IReadOnlyList<EQOverwatch.Core.Spells.AaInfo> AaCatalogFor()
    {
        string folder = EqFolderPath ?? string.Empty;
        if (_aaCatalog is null || _aaCatalogFolder != folder)
        {
            _aaCatalog = EQOverwatch.Core.Spells.AaCatalog.Load(folder);
            _aaCatalogFolder = folder;
        }

        return _aaCatalog;
    }

    private IReadOnlyList<AaRowView> AaRowsFor(string character, string server, string persona)
    {
        var descriptions = AaCatalogFor().ToDictionary(a => a.Name, a => a.Description, StringComparer.OrdinalIgnoreCase);
        return _pipeline.Progress.Aas(character, server, persona)
            .Select(a => new AaRowView(
                a.Name,
                a.Rank,
                a.Source == EQOverwatch.Core.Progress.AaProgress.Added ? "—" : a.Points.ToString(CultureInfo.CurrentCulture),
                a.LearnedUtc is { } at ? at.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture) : "—",
                a.Source,
                descriptions.GetValueOrDefault(a.Name) ?? string.Empty))
            .ToList();
    }

    /// <summary>
    /// The challenges of the character's server with this character's standing: done (an achievement of the character's own log names
    /// the challenge's boss), still possible (at or under the level), or too high.
    /// </summary>
    public IReadOnlyList<ChallengeRowView> ChallengeRowsFor(string character, string server)
    {
        if (Core.Tlp.TlpServers.Find(server)?.ChallengeList is not { Count: > 0 } challenges)
        {
            return Array.Empty<ChallengeRowView>();
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        IReadOnlyList<Core.Progress.CharacterFlag> done = _pipeline.Progress.AchievementsDone(character, server);
        int level = _pipeline.Roster.GetLevel(character, server);
        return challenges.Select(c =>
        {
            Core.Progress.CharacterFlag? hit = done.FirstOrDefault(a => c.Matches(a.Text));
            string status = hit is not null ? $"Done ({hit.Utc.ToLocalTime().ToString("MMM d, yyyy", culture)})"
                : level <= 0 || level <= c.Level ? "Still possible"
                : "Too high";
            return new ChallengeRowView(c.Level, c.Expansions.Length > 0 ? $"{c.Goal} ({c.Expansions})" : c.Goal, c.Reward, status, hit is not null);
        }).ToList();
    }

    /// <summary>The game's skillcaps.txt, read again when the EverQuest folder changes.</summary>
    private SkillCaps CurrentSkillCaps()
    {
        string folder = EqFolderPath ?? string.Empty;
        if (_skillCaps is null || !string.Equals(folder, _skillCapsFolder, StringComparison.OrdinalIgnoreCase))
        {
            _skillCapsFolder = folder;
            _skillCaps = folder.Length == 0 ? SkillCaps.Empty : SkillCaps.Load(Path.Combine(folder, "Resources", "skillcaps.txt"));
        }

        return _skillCaps;
    }

    private string DescribeWhen(DateTime utc)
    {
        if (utc.Year < 2000)
        {
            return string.Empty;
        }

        DateTime local = utc.ToLocalTime();
        return local.Date == _clock().Date ? local.ToString("t", CultureInfo.CurrentCulture) : local.ToString("MMM d", CultureInfo.CurrentCulture);
    }

    private static string Signed(long value) => (value > 0 ? "+" : string.Empty) + value.ToString("N0", CultureInfo.CurrentCulture);
}
