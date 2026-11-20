using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Firsts;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Leveling;
using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>One mob in the Server Firsts view: its first kill (or open), who and how sure.</summary>
public sealed record ServerFirstRow(string Mob, string Expansion, string Date, string Guild, string Status, string Witnesses, string Players, ServerFirstEntry Entry);

/// <summary>A guild's count of Server Firsts.</summary>
public sealed record GuildFirstsRow(string Guild, int Firsts);

/// <summary>How one class does on the selected boss.</summary>
public sealed record BenchmarkRow(string Class, int Fights, string MedianDps, string TopDps, string MedianHps, string MedianTaken);

/// <summary>A request to show a Windows notice (Server First, and other one-off news).</summary>
public sealed record NoticeInfo(string Title, string Text);

// Server First: raid fights shared with names and guilds (opt-in, with a warning), the first kill of each boss per server, open raid
// targets, guild counts and class benchmarks; and the Sharing section of Settings that holds every sharing switch.
public sealed partial class MainViewModel
{
    public const string AllGuilds = "All guilds";

    public const string ServerFirstWarning =
        "Server First shares the full record of qualifying fights: the mob, the kill time (UTC), the length, and every participant's name, " +
        "guild (when known), class, damage, healing and damage taken. Anyone using EQ Overwatch on this server can see it. " +
        "Your other sharing stays anonymous.\n\nA fight qualifies when more than 12 players take part on a named boss that took far more " +
        "damage than the trash before it.\n\nTurn Server First sharing on?";

    private static readonly TimeSpan FirstsDownloadEvery = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, ServerFirsts> _firsts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _nextFirstsDownload = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _firstsSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Server, FirstFight Fight)> _firstsPending = new();
    private readonly HashSet<string> _firstsNoticed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sharingErrors = new(StringComparer.OrdinalIgnoreCase);
    private IServerFirstService? _firstsService;
    private bool _firstsBusy;
    private int _firstsShared;

    public ObservableCollection<string> FirstsServers { get; } = new();
    public ObservableCollection<string> FirstsGuilds { get; } = new() { AllGuilds };
    public ObservableCollection<ServerFirstRow> ServerFirstRows { get; } = new();
    public ObservableCollection<GuildFirstsRow> GuildFirstsRows { get; } = new();
    public ObservableCollection<BenchmarkRow> BenchmarkRows { get; } = new();

    /// <summary>Share qualifying raid fights for Server First, with names and guilds (off until switched on, after a warning).</summary>
    [ObservableProperty]
    private bool _shareServerFirsts;

    [ObservableProperty]
    private string? _selectedFirstsServer;

    [ObservableProperty]
    private string _selectedFirstsGuild = AllGuilds;

    [ObservableProperty]
    private ServerFirstRow? _selectedFirstRow;

    [ObservableProperty]
    private string _firstsStatus = "Server Firsts load in a moment.";

    [ObservableProperty]
    private string _benchmarkTitle = "Select a boss to see how each class does on it.";

    /// <summary>Mob History shows the mobs (false) or the Server Firsts (true).</summary>
    [ObservableProperty]
    private bool _showServerFirsts;

    /// <summary>The Mobs button: the other side of <see cref="ShowServerFirsts"/>.</summary>
    public bool ShowMobsView
    {
        get => !ShowServerFirsts;
        set => ShowServerFirsts = !value;
    }

    partial void OnShowServerFirstsChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMobsView));
        if (value)
        {
            ShowServerFirsts_();
        }
    }

    [ObservableProperty]
    private string _levelingShareSummary = string.Empty;

    [ObservableProperty]
    private string _firstsShareSummary = string.Empty;

    /// <summary>"#3FB950" when the last sharing calls worked, "#E5534B" when one failed, gray before any.</summary>
    [ObservableProperty]
    private string _sharingLightColor = "#7D8590";

    [ObservableProperty]
    private string _sharingLightTip = "Sharing: nothing sent or received yet.";

    /// <summary>A Windows notice to show (a Server First of yours).</summary>
    public event Action<NoticeInfo>? NoticeRequested;

    /// <summary>The last Server First upload or download (for tests).</summary>
    public Task FirstsTask { get; private set; } = Task.CompletedTask;

    public void ConfigureServerFirsts(IServerFirstService service)
    {
        _firstsService = service;
        _nextFirstsDownload.Clear();
    }

    partial void OnShareServerFirstsChanged(bool value)
    {
        if (_restoring)
        {
            return;
        }

        if (value && !_dialogs.Confirm("Share Server Firsts?", ServerFirstWarning))
        {
            _restoring = true;
            ShareServerFirsts = false;
            _restoring = false;
            return;
        }

        RequestSave();
        UpdateShareSummaries();
    }

    partial void OnSelectedFirstsServerChanged(string? value) => ShowServerFirsts_();

    partial void OnSelectedFirstsGuildChanged(string value) => ShowServerFirsts_();

    partial void OnSelectedFirstRowChanged(ServerFirstRow? value) => ShowBenchmarks();

    /// <summary>Notes live fights that just ended; qualifying ones are shared when Server First is on.</summary>
    private void QueueServerFirsts(IReadOnlyList<EncounterSnapshot> completed)
    {
        IReadOnlyList<BossFight>? history = null;
        foreach (EncounterSnapshot fight in completed)
        {
            string key = fight.TargetName + "|" + fight.StartTime.Ticks.ToString(CultureInfo.InvariantCulture);
            if (fight.IsActive || string.IsNullOrEmpty(fight.Server) || !_firstsSeen.Add(key) || !ShareServerFirsts)
            {
                continue;
            }

            history ??= _bossHistory.Fights();
            IEnumerable<long> earlier = history
                .Where(f => f.Killed && !f.Boss.Equals(fight.TargetName, StringComparison.OrdinalIgnoreCase)
                            && f.Start < fight.StartTime && f.Start >= fight.StartTime - ServerFirstRules.TrashWindow
                            && (string.IsNullOrEmpty(f.Server) || f.Server.Equals(fight.Server, StringComparison.OrdinalIgnoreCase)))
                .Select(f => f.Damage);
            EncounterSnapshot classed = fight.WithClassesOnServer(ClassLabelOnServer);
            if (ServerFirstRules.Qualifies(classed, earlier))
            {
                string server = fight.Server.ToLowerInvariant();
                _firstsPending.Add((server, ServerFirstRules.Build(classed, name => _pipeline.Guilds.Get(name, server))));
            }
        }

        if (_firstsSeen.Count > 5000)
        {
            _firstsSeen.Clear();
        }
    }

    /// <summary>Every tick: shares pending fights and downloads each server's firsts now and then.</summary>
    private void RefreshServerFirsts(DateTime nowUtc)
    {
        List<string> servers = Logs.Select(l => l.ServerName).Where(s => !string.IsNullOrEmpty(s)).Select(s => s.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        if (!servers.SequenceEqual(FirstsServers, StringComparer.OrdinalIgnoreCase))
        {
            string? keep = SelectedFirstsServer;
            FirstsServers.Clear();
            foreach (string s in servers)
            {
                FirstsServers.Add(s);
            }

            SelectedFirstsServer = keep is not null && servers.Contains(keep, StringComparer.OrdinalIgnoreCase) ? keep : servers.FirstOrDefault();
        }

        if (_firstsService is null || _firstsBusy)
        {
            return;
        }

        List<string> due = servers.Where(s => nowUtc >= _nextFirstsDownload.GetValueOrDefault(s)).ToList();
        if (_firstsPending.Count == 0 && due.Count == 0)
        {
            return;
        }

        FirstsTask = SyncServerFirstsAsync(_firstsService, due, nowUtc);
    }

    private async Task SyncServerFirstsAsync(IServerFirstService service, List<string> due, DateTime nowUtc)
    {
        _firstsBusy = true;
        try
        {
            if (_firstsPending.Count > 0 && ShareServerFirsts)
            {
                EnsureSharingId();
                foreach (IGrouping<string, (string Server, FirstFight Fight)> batch in _firstsPending.ToList().GroupBy(p => p.Server))
                {
                    List<FirstFight> fights = batch.Select(p => p.Fight).Take(50).ToList();
                    if (await service.UploadAsync(batch.Key, _levelingId, _levelingKey, fights))
                    {
                        _firstsShared += fights.Count;
                        _firstsPending.RemoveAll(p => p.Server == batch.Key && fights.Contains(p.Fight));
                        if (!due.Contains(batch.Key))
                        {
                            due.Add(batch.Key); // see at once whether one of them is a Server First
                        }
                    }
                }
            }
            else if (!ShareServerFirsts)
            {
                _firstsPending.Clear();
            }

            foreach (string server in due)
            {
                _nextFirstsDownload[server] = nowUtc + FirstsDownloadEvery;
                ServerFirsts firsts = await service.DownloadAsync(server, _levelingId.Length > 0 ? _levelingId : "none");
                _firsts[server] = firsts;
                foreach (ServerFirstEntry mine in firsts.Entries.Where(e => e.Mine && e.Verified && !e.IsOpen))
                {
                    if (_firstsNoticed.Add(server + "|" + mine.Mob) && DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(mine.FirstKill).UtcDateTime < TimeSpan.FromHours(12))
                    {
                        NoticeRequested?.Invoke(new NoticeInfo("Server First!", $"{mine.Mob} on {server}: no one had killed it before."));
                    }
                }
            }

            ReportSharing("Server First", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            ReportSharing("Server First", ex.Message);
        }
        finally
        {
            _firstsBusy = false;
        }

        ShowServerFirsts_();
        UpdateShareSummaries();
    }

    /// <summary>The id and key every sharing kind but drops uses (a random id, never a name).</summary>
    private void EnsureSharingId()
    {
        if (_levelingId.Length == 0 || _levelingKey.Length == 0)
        {
            (_levelingId, _levelingKey) = Core.Drops.HttpSharedDropsService.NewInstall();
            RequestSave();
        }
    }

    /// <summary>The firsts of a server (empty before they are downloaded).</summary>
    public ServerFirsts FirstsOn(string? server) =>
        server is not null && _firsts.TryGetValue(server.ToLowerInvariant(), out ServerFirsts? found) ? found : ServerFirsts.Empty;

    /// <summary>True while a raid fights a named boss nobody on the server has killed yet (the DPS overlay's badge).</summary>
    public bool ServerFirstPossible(EncounterSnapshot fight)
    {
        if (!fight.IsActive || string.IsNullOrEmpty(fight.Server) || !ServerFirstRules.IsNamed(fight.TargetName)
            || ServerFirstRules.Players(fight).Count < ServerFirstRules.MinPlayers || !_firsts.ContainsKey(fight.Server.ToLowerInvariant()))
        {
            return false;
        }

        ServerFirstEntry? entry = FirstsOn(fight.Server).Find(fight.TargetName);
        return entry is null || entry.IsOpen;
    }

    /// <summary>The Mob Card's Server First line, or null.</summary>
    internal string? ServerFirstLine(string mob, string? server)
    {
        ServerFirstEntry? entry = FirstsOn(server).Find(mob);
        if (entry is null)
        {
            return null;
        }

        if (entry.IsOpen)
        {
            return "Not killed yet on this server";
        }

        string guild = entry.Fight?.Guild is { Length: > 0 } g ? " · " + g : string.Empty;
        return $"Server first: {HttpServerFirstService.DateText(entry.FirstKill)}{guild}{(entry.Verified ? string.Empty : " (earliest known)")}";
    }

    /// <summary>"Top 10% WAR" for a player on a boss, from the server's benchmarks (5 or more fights), or empty.</summary>
    internal string BenchmarkBadge(EncounterSnapshot fight, ParticipantSnapshot player)
    {
        if (string.IsNullOrEmpty(player.Class) || player.TotalDamage <= 0)
        {
            return string.Empty;
        }

        ClassBenchmark? bench = FirstsOn(fight.Server).Benchmark(fight.TargetName, player.Class);
        return bench is { Fights: >= 5 } ? bench.Badge(player.Dps) : string.Empty;
    }

    private void ShowServerFirsts_()
    {
        ServerFirsts firsts = FirstsOn(SelectedFirstsServer);
        ZoneDirectory zones = LevelingZones();
        Dictionary<string, string> mobZones = _mobLevels.All().Where(l => l.Zone is not null)
            .GroupBy(l => l.Mob, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Zone!, StringComparer.OrdinalIgnoreCase);
        var guilds = firsts.Entries.Where(e => !e.IsOpen && e.Fight is not null).Select(e => e.Fight!.Guild).Where(g => g.Length > 0)
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase).Select(g => new GuildFirstsRow(g.Key, g.Count()))
            .OrderByDescending(g => g.Firsts).ThenBy(g => g.Guild, StringComparer.OrdinalIgnoreCase).ToList();

        GuildFirstsRows.Clear();
        foreach (GuildFirstsRow g in guilds)
        {
            GuildFirstsRows.Add(g);
        }

        string guildChoice = SelectedFirstsGuild;
        List<string> guildNames = new[] { AllGuilds }.Concat(guilds.Select(g => g.Guild)).ToList();
        if (!guildNames.SequenceEqual(FirstsGuilds))
        {
            FirstsGuilds.Clear();
            foreach (string g in guildNames)
            {
                FirstsGuilds.Add(g);
            }
        }

        if (!FirstsGuilds.Contains(guildChoice))
        {
            guildChoice = AllGuilds;
        }

        string? keepMob = SelectedFirstRow?.Mob;
        ServerFirstRows.Clear();
        foreach (ServerFirstEntry e in firsts.Entries
                     .Where(e => guildChoice == AllGuilds || (e.Fight?.Guild ?? string.Empty).Equals(guildChoice, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => FirstsExpansion(zones, mobZones, e))
                     .ThenBy(e => e.IsOpen)
                     .ThenBy(e => e.FirstKill))
        {
            int expansion = FirstsExpansion(zones, mobZones, e);
            ServerFirstRows.Add(new ServerFirstRow(
                e.Mob,
                expansion is > 0 and < int.MaxValue ? ZoneDirectory.ExpansionName(expansion) : "—",
                e.IsOpen ? "Open" : HttpServerFirstService.DateText(e.FirstKill),
                e.Fight?.Guild ?? string.Empty,
                e.IsOpen ? "Not killed yet" : e.Verified ? "Server first" : "Earliest known",
                e.IsOpen ? "—" : e.Witnesses.ToString(CultureInfo.CurrentCulture),
                e.Fight is null ? "—" : e.Fight.Players.Count.ToString(CultureInfo.CurrentCulture),
                e));
        }

        SelectedFirstRow = ServerFirstRows.FirstOrDefault(r => r.Mob == keepMob);
        int kills = firsts.Entries.Count(e => !e.IsOpen);
        string age = firsts.Updated > 0 ? $" Updated {AgoText(DateTime.UtcNow - DateTimeOffset.FromUnixTimeSeconds(firsts.Updated).UtcDateTime)}." : string.Empty;
        FirstsStatus = SelectedFirstsServer is null ? "Add a character's log to see its server's firsts."
            : firsts.Entries.Count == 0 ? $"No Server Firsts shared on {SelectedFirstsServer} yet.{age}"
            : $"{kills.ToString(CultureInfo.CurrentCulture)} boss(es) killed and {(firsts.Entries.Count - kills).ToString(CultureInfo.CurrentCulture)} open on {SelectedFirstsServer}.{age}";
        ShowBenchmarks();
    }

    /// <summary>The expansion of a boss, from a zone your /con levels saw it in; MaxValue when not known.</summary>
    private static int FirstsExpansion(ZoneDirectory zones, Dictionary<string, string> mobZones, ServerFirstEntry entry) =>
        mobZones.TryGetValue(entry.Mob, out string? zone) && LevelingRanking.ExpansionOf(zones, zone) is { } e ? e : int.MaxValue;

    private void ShowBenchmarks()
    {
        BenchmarkRows.Clear();
        if (SelectedFirstRow is not { } row)
        {
            BenchmarkTitle = "Select a boss to see how each class does on it.";
            return;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        List<ClassBenchmark> benches = FirstsOn(SelectedFirstsServer).Benchmarks
            .Where(b => b.Mob.Equals(row.Mob, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b.DpsDeciles.Count > 4 ? b.DpsDeciles[4] : 0).ToList();
        foreach (ClassBenchmark b in benches)
        {
            BenchmarkRows.Add(new BenchmarkRow(
                b.Class, b.Fights,
                b.DpsDeciles.Count > 4 ? b.DpsDeciles[4].ToString("N0", culture) : "—",
                b.DpsDeciles.Count > 8 ? b.DpsDeciles[8].ToString("N0", culture) : "—",
                b.MedianHps > 0 ? b.MedianHps.ToString("N0", culture) : "—",
                b.MedianTakenPerSecond > 0 ? b.MedianTakenPerSecond.ToString("N0", culture) : "—"));
        }

        BenchmarkTitle = benches.Count == 0 ? $"No class numbers for {row.Mob} yet." : $"Classes on {row.Mob} (per second, from every shared kill)";
    }

    /// <summary>Asks the window to show a fight as a picture (the selected Server First).</summary>
    public event Action<EncounterSnapshot>? ShowFightPictureRequested;

    [RelayCommand]
    private void ViewFirstFight()
    {
        if (SelectedFirstRow?.Entry.Fight is not { } fight)
        {
            return;
        }

        EncounterSnapshot snapshot = ServerFirstRules.ToSnapshot(fight);
        var withBadges = snapshot with
        {
            Participants = snapshot.Participants.Select(p => BenchmarkBadge(snapshot with { Server = SelectedFirstsServer ?? string.Empty }, p) is { Length: > 0 } badge
                ? p with { Class = p.Class + " · " + badge.Replace(" " + p.Class, string.Empty, StringComparison.Ordinal) }
                : p).ToList(),
        };
        ShowFightPictureRequested?.Invoke(withBadges with { Server = SelectedFirstsServer ?? string.Empty });
    }

    // ───────────── the Sharing section of Settings ─────────────

    /// <summary>Notes how the last call of a sharing kind went (null: it worked) and updates the light in the top bar.</summary>
    private void ReportSharing(string kind, string? error)
    {
        if (error is null)
        {
            _sharingErrors.Remove(kind);
        }
        else
        {
            _sharingErrors[kind] = error;
        }

        SharingLightColor = _sharingErrors.Count == 0 ? "#3FB950" : "#E5534B";
        SharingLightTip = _sharingErrors.Count == 0
            ? "Sharing: the last uploads and downloads worked."
            : "Sharing problem: " + string.Join("; ", _sharingErrors.Select(p => $"{p.Key}: {p.Value}"));
    }

    private void UpdateShareSummaries()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        int stretches = _leveling.Servers().Sum(s => _leveling.Stretches(s).Count - _leveling.Unshared(s).Count);
        LevelingShareSummary = ShareMyLeveling
            ? $"Shared: {stretches.ToString(culture)} leveling stretches (zone, level, time, experience, kills). No names."
            : "Not sharing leveling.";
        FirstsShareSummary = ShareServerFirsts
            ? $"Shared this session: {_firstsShared.ToString(culture)} raid fight(s), with names and guilds."
            : "Not sharing Server Firsts.";
    }

    [RelayCommand]
    private async Task DeleteMySharedLeveling()
    {
        if (_levelingService is null || _levelingId.Length == 0 || !_dialogs.Confirm("Delete shared leveling?",
                "Takes everything this PC shared for leveling off the service, on every server. Your own Leveling tab keeps your logs' numbers."))
        {
            return;
        }

        try
        {
            foreach (string server in _leveling.Servers())
            {
                await _levelingService.DeleteAsync(server, _levelingId, _levelingKey);
                _leveling.ForgetShared(server);
            }

            ShareMyLeveling = false;
            LevelingShareSummary = "Deleted from the service. Sharing is off.";
            ReportSharing("Leveling", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LevelingShareSummary = "Couldn't delete it yet: " + ex.Message;
            ReportSharing("Leveling", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteMySharedFirsts()
    {
        if (_firstsService is null || _levelingId.Length == 0 || !_dialogs.Confirm("Delete shared Server First fights?",
                "Takes every raid fight this PC shared for Server First off the service, on every server, names and all."))
        {
            return;
        }

        try
        {
            foreach (string server in FirstsServers)
            {
                await _firstsService.DeleteAsync(server, _levelingId, _levelingKey);
            }

            ShareServerFirsts = false;
            _firstsPending.Clear();
            FirstsShareSummary = "Deleted from the service. Sharing is off.";
            _nextFirstsDownload.Clear();
            ReportSharing("Server First", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            FirstsShareSummary = "Couldn't delete it yet: " + ex.Message;
            ReportSharing("Server First", ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteMySharedDrops()
    {
        if (_dialogs.Confirm("Delete shared drops?", "Takes every kill and drop this PC shared off the service. Sharing drops is switched off."))
        {
            ShareMyDrops = false; // switching it off takes the drops down
        }
    }
}
