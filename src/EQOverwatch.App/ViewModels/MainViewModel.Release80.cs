using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Experience;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Leveling;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Resists;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

// Release 1.0.80: shared mob stats (regen/HP, levels, resists, best kills), the Leveling tab's AA mode, time to level, best mobs, plat per
// hour and mob levels, the Experience tab's zone median, the DPS overlay's regen warning, the Mob Card's kill time, level history, and
// server boxes that follow the character you are playing.
public sealed partial class MainViewModel
{
    private static readonly TimeSpan MobStatsDownloadEvery = TimeSpan.FromHours(6);

    private readonly Dictionary<string, SharedMobStats> _sharedMobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _nextMobStatsUpload = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _nextMobStatsDownload = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _sharedLevelingAt = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, long> _mobStatsSharedThrough = new(StringComparer.OrdinalIgnoreCase);
    private IMobStatsService? _mobStatsService;
    private bool _mobStatsBusy;
    private string _followedCharacter = string.Empty;

    public ObservableCollection<BestMobView> LevelingBestMobs { get; } = new();

    /// <summary>The Leveling tab ranks by AA points (solo and group together).</summary>
    [ObservableProperty]
    private bool _levelingAa;

    [ObservableProperty]
    private LevelingZoneView? _selectedLevelingZone;

    [ObservableProperty]
    private string _levelingBestMobsTitle = string.Empty;

    /// <summary>"Next level in ~1:20 · end of band in ~6:40 (at Chardok's 42.0%/hour)", or empty.</summary>
    [ObservableProperty]
    private string _levelingEtaText = string.Empty;

    [ObservableProperty]
    private bool _useSharedMobStats = true;

    [ObservableProperty]
    private bool _shareMyMobStats = true;

    [ObservableProperty]
    private string _mobStatsShareSummary = string.Empty;

    /// <summary>"Shared mob stats from 2 h ago", or empty.</summary>
    [ObservableProperty]
    private string _mobStatsFreshness = string.Empty;

    /// <summary>The last mob stats upload or download (for tests).</summary>
    public Task MobStatsTask { get; private set; } = Task.CompletedTask;

    private LevelingMode LevelingMode_ => LevelingAa ? LevelingMode.Aa : LevelingGroup ? LevelingMode.Group : LevelingMode.Solo;

    partial void OnLevelingAaChanged(bool value)
    {
        OnPropertyChanged(nameof(LevelingSolo));
        OnPropertyChanged(nameof(LevelingGroupChoice));
        ShowLeveling();
    }

    partial void OnSelectedLevelingZoneChanged(LevelingZoneView? value) => ShowBestMobs();

    partial void OnUseSharedMobStatsChanged(bool value)
    {
        if (!_restoring)
        {
            RequestSave();
            _nextMobStatsDownload.Clear();
            ClearRegenCache();
        }
    }

    partial void OnShareMyMobStatsChanged(bool value)
    {
        if (!_restoring)
        {
            RequestSave();
            _nextMobStatsUpload.Clear();
        }
    }

    public void ConfigureMobStats(IMobStatsService service)
    {
        _mobStatsService = service;
        _nextMobStatsDownload.Clear();
        _nextMobStatsUpload.Clear();
    }

    private void RestoreRelease80(Core.Settings.AppSettings settings)
    {
        UseSharedMobStats = settings.UseSharedMobStats;
        ShareMyMobStats = settings.ShareMyMobStats;
        _mobStatsSharedThrough = new Dictionary<string, long>(settings.MobStatsSharedThrough ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    // ───────────── shared mob stats ─────────────

    /// <summary>Every tick: shares mob stats once a day per server and downloads everyone's every few hours.</summary>
    private void RefreshMobStats(DateTime nowUtc)
    {
        if (_mobStatsService is not { } service || _mobStatsBusy || (!UseSharedMobStats && !ShareMyMobStats))
        {
            return;
        }

        List<string> servers = Logs.Select(l => l.ServerName).Where(s => !string.IsNullOrEmpty(s)).Select(s => s.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> upload = ShareMyMobStats ? servers.Where(s => nowUtc >= _nextMobStatsUpload.GetValueOrDefault(s)).ToList() : new();
        List<string> download = UseSharedMobStats ? servers.Where(s => nowUtc >= _nextMobStatsDownload.GetValueOrDefault(s)).ToList() : new();
        if (upload.Count == 0 && download.Count == 0)
        {
            return;
        }

        MobStatsTask = SyncMobStatsAsync(service, upload, download, nowUtc);
    }

    private async Task SyncMobStatsAsync(IMobStatsService service, List<string> upload, List<string> download, DateTime nowUtc)
    {
        _mobStatsBusy = true;
        try
        {
            foreach (string server in upload)
            {
                _nextMobStatsUpload[server] = nowUtc + SharedDropsUploadEvery;
                (MobStatsUpload body, long through) = await Task.Run(() => BuildMobStatsUpload(server));
                if (body.Kills.Count + body.Levels.Count + body.Resists.Count == 0)
                {
                    continue;
                }

                EnsureSharingId();
                if (await service.UploadAsync(server, _levelingId, _levelingKey, body))
                {
                    _mobStatsSharedThrough[server] = through;
                    RequestSave();
                }
            }

            foreach (string server in download)
            {
                _nextMobStatsDownload[server] = nowUtc + MobStatsDownloadEvery;
                _sharedMobs[server] = await service.DownloadAsync(server);
            }

            ClearRegenCache();
            ReportSharing("Mob stats", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            ReportSharing("Mob stats", ex.Message);
        }
        finally
        {
            _mobStatsBusy = false;
        }

        UpdateMobStatsSummary();
        _bossHistoryDirty = true;
    }

    /// <summary>This PC's kills on a server since the last share, and its current /con level ranges and resist counts. No names.</summary>
    private (MobStatsUpload Body, long Through) BuildMobStatsUpload(string server)
    {
        long since = _mobStatsSharedThrough.GetValueOrDefault(server);
        List<SharedKill> kills = _bossHistory.Fights()
            .Where(f => f.Killed && f.Seconds > 0 && f.Damage > 0 && string.Equals(f.Server, server, StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsPlayerOrPet(f.Boss))
            .Select(f => new SharedKill(f.Boss, Math.Round(f.Seconds, 1), f.Damage, Math.Max(1, f.Players),
                new DateTimeOffset(DateTime.SpecifyKind(f.Start, DateTimeKind.Local)).ToUnixTimeSeconds()))
            .Where(k => k.Start > since)
            .OrderBy(k => k.Start)
            .Take(50_000)
            .ToList();
        // Only mobs the raid killed that are no known player or pet: /con works on players too, and no player names are shared.
        HashSet<string> killed = KilledMobs();
        bool IsMob(string name) => IsShareableMob(name, killed);
        List<SharedLevel> levels = _mobLevels.All().Where(l => !string.IsNullOrEmpty(l.Zone) && IsMob(l.Mob))
            .Select(l => new SharedLevel(l.Mob, l.Zone!, l.Lowest, l.Highest)).Take(20_000).ToList();
        var resists = new List<SharedResist>();
        foreach (string mob in _pipeline.ResistStore.Mobs().Where(IsMob))
        {
            foreach ((int type, ResistCount count) in _pipeline.ResistStore.Report(mob, raid: false).ByType)
            {
                if (count.Landed + count.Resisted > 0)
                {
                    resists.Add(new SharedResist(mob, type, count.Landed, count.Resisted));
                }
            }
        }

        return (new MobStatsUpload(kills, levels, resists.Take(20_000).ToList()), kills.Count > 0 ? kills[^1].Start : since);
    }

    /// <summary>Everyone's pooled stats (the same on every server but for best kills); null before any download.</summary>
    private SharedMobStats? SharedMobs(string? server = null)
    {
        if (!UseSharedMobStats)
        {
            return null;
        }

        return server is not null && _sharedMobs.TryGetValue(server, out SharedMobStats? on) ? on : _sharedMobs.Values.FirstOrDefault();
    }

    private void UpdateMobStatsSummary()
    {
        MobStatsShareSummary = ShareMyMobStats
            ? "Shared once a day: your kills (time, damage, attackers), /con levels by zone and resist counts. No names."
            : "Not sharing mob stats.";
        long updated = SharedMobs()?.Updated ?? 0;
        MobStatsFreshness = updated > 0
            ? "Shared mob stats from " + AgoText(DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(updated).UtcDateTime) + "."
            : string.Empty;
    }

    [RelayCommand]
    private async Task DeleteMySharedMobStats()
    {
        if (_mobStatsService is null || _levelingId.Length == 0 || !_dialogs.Confirm("Delete shared mob stats?",
                "Takes every kill, level range and resist count this PC shared off the service. Sharing mob stats is switched off."))
        {
            return;
        }

        try
        {
            foreach (string server in Logs.Select(l => l.ServerName).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await _mobStatsService.DeleteAsync(server, _levelingId, _levelingKey);
            }

            ShareMyMobStats = false;
            _mobStatsSharedThrough.Clear();
            MobStatsShareSummary = "Deleted from the service. Sharing is off.";
            ReportSharing("Mob stats", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            MobStatsShareSummary = "Couldn't delete it yet: " + ex.Message;
            ReportSharing("Mob stats", ex.Message);
        }
    }

    /// <summary>The Mob Card's resist line from everyone's shared counts, when your own logs have none.</summary>
    internal string? SharedResistLine(string mob)
    {
        IReadOnlyDictionary<int, (int Landed, int Resisted)>? counts = SharedMobs()?.Resists(mob);
        if (counts is null || counts.Count == 0)
        {
            return null;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        string[] parts = SpellResist.Main
            .Where(t => counts.TryGetValue(t, out var c) && c.Landed + c.Resisted > 0)
            .Select(t => $"{SpellResist.Name(t)} {(100.0 * counts[t].Resisted / (counts[t].Landed + counts[t].Resisted)).ToString("0", culture)}%")
            .ToArray();
        return parts.Length == 0 ? null : "Resisted (everyone): " + string.Join(" · ", parts);
    }

    /// <summary>"~0:55 to kill" from the mob's HP and regen and the median DPS of your raid's kills of it, or empty.</summary>
    internal string KillTimeText(string mob, MobRegen regen)
    {
        double[] dps = _bossHistory.Fights().Where(f => f.Killed && f.Seconds > 0 && f.Boss.Equals(mob, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Dps).OrderBy(d => d).ToArray();
        if (dps.Length == 0)
        {
            return string.Empty;
        }

        double median = dps[dps.Length / 2];
        double net = median - regen.RegenPerSecond;
        return net <= 0 ? " · out-heals your usual DPS" : " · ~" + Core.History.BossHistoryStore.FormatDuration(regen.HitPoints / net) + " to kill";
    }

    /// <summary>The fastest kill and best DPS anyone logged on the mob on this server, as "0:48 · 9,840 DPS", or empty.</summary>
    internal string ServerBestText(string mob, string? server)
    {
        ServerBest? best = server is null ? null : SharedMobs(server)?.Best(mob);
        return best is null ? string.Empty
            : $"{Core.History.BossHistoryStore.FormatDuration(best.FastestSeconds)} · {best.BestDps.ToString("N0", CultureInfo.CurrentCulture)} DPS";
    }

    // ───────────── Leveling ─────────────

    /// <summary>Your own leveling rows on a server, plus everyone's when shared leveling is on.</summary>
    private List<LevelingRow> LevelingRowsFor(string server)
    {
        SharedLeveling? shared = UseSharedLeveling ? _sharedLeveling.GetValueOrDefault(server) : null;
        return LevelingRanking.Rows(_leveling.Stretches(server), "you").Concat(shared?.Rows ?? Array.Empty<LevelingRow>()).ToList();
    }

    /// <summary>Every mob the raid has killed (the names that are surely mobs even without "a" or "the").</summary>
    private HashSet<string> KilledMobs() =>
        new(_bossHistory.Fights().Where(f => f.Killed).Select(f => f.Boss), StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a name that is surely a mob: it starts with "a", "an" or "the", or was killed, and is no player or pet.</summary>
    private bool IsSureMob(string name, HashSet<string> killed) =>
        !IsPlayerOrPet(name)
        && (name.StartsWith("a ", StringComparison.Ordinal) || name.StartsWith("an ", StringComparison.Ordinal)
            || name.StartsWith("the ", StringComparison.Ordinal) || killed.Contains(name));

    /// <summary>True for a name that may be shared: a mob your raid killed that is no known player, pet or character of yours.</summary>
    public bool IsShareableMob(string name, HashSet<string> killed) => killed.Contains(name) && !IsPlayerOrPet(name);

    /// <summary>True for a name that is a player or a pet, not a mob (pets and players are /con'd too).</summary>
    private bool IsPlayerOrPet(string name) =>
        name.Contains('`') || _pipeline.Roster.IsPet(name) || _pipeline.Roster.IsVerifiedPlayer(name)
        || Logs.Any(l => name.Contains(l.CharacterName, StringComparison.OrdinalIgnoreCase));

    /// <summary>"45–52": the levels of mobs seen in a zone, from your /cons and everyone's.</summary>
    private string ZoneMobLevels(string zone) =>
        ZoneMobRange(zone, KilledMobs()) is { } range
            ? $"{range.Lowest.ToString(CultureInfo.CurrentCulture)}–{range.Highest.ToString(CultureInfo.CurrentCulture)}"
            : "—";

    /// <summary>The levels of mobs seen in a zone (your /cons and everyone's), or null when none were seen.</summary>
    private (int Lowest, int Highest)? ZoneMobRange(string zone, HashSet<string> killed)
    {
        var mine = _mobLevels.All().Where(l => l.Zone is not null && l.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase) && IsSureMob(l.Mob, killed)).ToList();
        (int Lowest, int Highest)? shared = SharedMobs()?.ZoneRange(zone);
        int lo = Math.Min(mine.Count > 0 ? mine.Min(l => l.Lowest) : int.MaxValue, shared?.Lowest ?? int.MaxValue);
        int hi = Math.Max(mine.Count > 0 ? mine.Max(l => l.Highest) : 0, shared?.Highest ?? 0);
        return lo == int.MaxValue || hi == 0 ? null : (lo, hi);
    }

    /// <summary>Time to the next level and to the end of the band (or to the next AA) at the zone's rate.</summary>
    private string LevelingEta(string server, LevelingMode mode, ZoneRank? zone)
    {
        if (zone is null || zone.XpPerHour <= 0)
        {
            return string.Empty;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        if (mode == LevelingMode.Aa)
        {
            return $"Next AA in ~{FormatSpan(TimeSpan.FromHours(1 / zone.XpPerHour))} at {zone.Zone}'s {zone.XpPerHour.ToString("0.0", culture)} AA/hour.";
        }

        // How far into the level the character on this server you played last is (0 when not known).
        string? character = Logs.Where(l => string.Equals(l.ServerName, server, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => _pipeline.Activity.LastLines().FirstOrDefault(x => x.Character.Equals(l.CharacterName, StringComparison.OrdinalIgnoreCase)).LastLineUtc)
            .Select(l => l.CharacterName).FirstOrDefault();
        double progress = _pipeline.Experience.Rows(DateTime.UtcNow, (n, s) => _pipeline.Roster.GetLevel(n, s))
            .FirstOrDefault(r => r.Character.Equals(character, StringComparison.OrdinalIgnoreCase))?.ProgressPercent ?? 0;
        double toLevel = Math.Max(0, 100 - progress);
        int bandEnd = LevelingRanking.BandOf(LevelingLevel) + LevelingRanking.BandSize - 1;
        double toBand = toLevel + (100.0 * Math.Max(0, bandEnd - LevelingLevel));
        return $"Next level in ~{FormatSpan(TimeSpan.FromHours(toLevel / zone.XpPerHour))} · end of band in ~{FormatSpan(TimeSpan.FromHours(toBand / zone.XpPerHour))} " +
               $"at {zone.Zone}'s {zone.XpPerHour.ToString("0.0", culture)}%/hour.";
    }

    /// <summary>The selected zone's mobs, most experience per kill near your level first.</summary>
    private void ShowBestMobs()
    {
        LevelingBestMobs.Clear();
        if (SelectedLevelingZone is not { } zoneRow || SelectedLevelingServer is not { } server)
        {
            LevelingBestMobsTitle = string.Empty;
            return;
        }

        string zone = zoneRow.Zone;
        var levels = new Dictionary<string, (int Lo, int Hi)>(StringComparer.OrdinalIgnoreCase);
        void Add(string mob, int lo, int hi) =>
            levels[mob] = levels.TryGetValue(mob, out var had) ? (Math.Min(had.Lo, lo), Math.Max(had.Hi, hi)) : (lo, hi);
        HashSet<string> killed = KilledMobs();
        foreach (MobLevels l in _mobLevels.All().Where(l => l.Zone is not null && l.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase) && IsSureMob(l.Mob, killed)))
        {
            Add(l.Mob, l.Lowest, l.Highest);
        }

        foreach (SharedLevel l in SharedMobs()?.Levels.Where(l => l.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase) && !IsPlayerOrPet(l.Mob)) ?? Enumerable.Empty<SharedLevel>())
        {
            Add(l.Mob, l.Lowest, l.Highest);
        }

        // Experience per kill your characters on this server got near this level.
        var xp = new Dictionary<string, (double Total, int Kills)>(StringComparer.OrdinalIgnoreCase);
        foreach (WatchedLogConfig log in Logs.Where(l => string.Equals(l.ServerName, server, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (MobExperienceRow row in _pipeline.MobExperience.Rows(log.CharacterName, log.ServerName)
                         .Where(r => Math.Abs(r.Level - LevelingLevel) <= 2 && levels.ContainsKey(r.Mob)))
            {
                xp[row.Mob] = xp.TryGetValue(row.Mob, out var had) ? (had.Total + row.TotalPercent, had.Kills + row.Kills) : (row.TotalPercent, row.Kills);
            }
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        foreach ((string mob, (int lo, int hi)) in levels
                     .OrderByDescending(p => xp.TryGetValue(p.Key, out var x) && x.Kills > 0 ? x.Total / x.Kills : -1)
                     .ThenBy(p => Math.Abs(((p.Value.Lo + p.Value.Hi) / 2) - LevelingLevel))
                     .Take(5))
        {
            bool known = xp.TryGetValue(mob, out var x) && x.Kills > 0;
            LevelingBestMobs.Add(new BestMobView(mob, lo == hi ? lo.ToString(culture) : $"{lo.ToString(culture)}–{hi.ToString(culture)}",
                known ? (x.Total / x.Kills).ToString("0.00", culture) + "%" : "—", known ? x.Kills : 0));
        }

        LevelingBestMobsTitle = LevelingBestMobs.Count == 0
            ? $"No mobs seen in {zone} yet (/con them to learn their levels)."
            : $"Best mobs in {zone} at level {LevelingLevel.ToString(culture)}";
    }

    /// <summary>The Experience tab's zone median: everyone's XP per hour in the character's zone and level band, or "—".</summary>
    private string ZoneMedianFor(ExperienceRow row)
    {
        WatchedLogConfig? log = Logs.FirstOrDefault(l => l.CharacterName.Equals(row.Character, StringComparison.OrdinalIgnoreCase));
        if (log is null || row.Level <= 0 || _pipeline.Zones.Get(log.CharacterName, log.ServerName)?.Zone is not { } zone)
        {
            return "—";
        }

        LevelingMode mode = row.PartyKills >= row.SoloKills ? LevelingMode.Group : LevelingMode.Solo;
        ZoneRank? rank = LevelingRanking.Rank(LevelingRowsFor(log.ServerName.ToLowerInvariant()), LevelingRanking.BandOf(row.Level), mode)
            .FirstOrDefault(r => r.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase));
        return rank is null ? "—" : rank.XpPerHour.ToString("0.0", CultureInfo.CurrentCulture) + "%";
    }

    // ───────────── level history and following the played character ─────────────

    /// <summary>The levels a character reached, from its log.</summary>
    private IReadOnlyList<LevelEvent> LevelHistoryFor(string character, string server)
    {
        WatchedLogConfig? log = Logs.FirstOrDefault(l => l.CharacterName.Equals(character, StringComparison.OrdinalIgnoreCase)
                                                         && string.Equals(l.ServerName, server, StringComparison.OrdinalIgnoreCase));
        return log is null ? Array.Empty<LevelEvent>() : _leveling.Levels(log.FilePath);
    }

    /// <summary>
    /// When the character you played most recently (and is still logged in) changes, the Server boxes switch to its server. Picking a
    /// server by hand holds until the next change.
    /// </summary>
    private void FollowPlayedCharacter(DateTime nowUtc)
    {
        if (_pipeline.Activity.MostRecentActive(nowUtc) is not { } played)
        {
            return;
        }

        string key = played.Character + "|" + played.Server;
        if (key.Equals(_followedCharacter, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(played.Server))
        {
            return;
        }

        _followedCharacter = key;
        string server = played.Server.ToLowerInvariant();
        if (LevelingServers.Contains(server, StringComparer.OrdinalIgnoreCase))
        {
            SelectedLevelingServer = LevelingServers.First(s => s.Equals(server, StringComparison.OrdinalIgnoreCase));
        }

        if (FirstsServers.Contains(server, StringComparer.OrdinalIgnoreCase))
        {
            SelectedFirstsServer = FirstsServers.First(s => s.Equals(server, StringComparison.OrdinalIgnoreCase));
        }

        if (ToolsServers.FirstOrDefault(s => s.Equals(server, StringComparison.OrdinalIgnoreCase)) is { } tools)
        {
            _toolsServerPickedByUser = false;
            SelectedToolsServer = tools;
        }
    }
}
