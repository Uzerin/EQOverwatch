using System.Collections.ObjectModel;
using System.Net.Http;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Leveling;
using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.ResourceHunter;
using EQOverwatch.Core.Tlp;

namespace EQOverwatch.App.ViewModels;

/// <summary>A row of a TLP tab's unlock schedule.</summary>
public sealed record TlpUnlockView(string Expansion, string Date, string LevelCap, string Bonus, string Status, bool IsNext);

/// <summary>One server's sub-tab on the TLP tab: its rules, what is in effect now, and its unlock schedule.</summary>
public sealed record TlpServerView(string Key, string Name, IReadOnlyList<string> Rules, string Source, string Now, string Next,
    IReadOnlyList<TlpUnlockView> Unlocks, bool HasBonuses, IReadOnlyList<TlpChallenge> Challenges)
{
    public bool HasChallenges => Challenges.Count > 0;

    public bool NoBonuses => !HasBonuses;
}

/// <summary>A row of the Resource Hunter tables: two bonuses side by side (either may be missing that day).</summary>
public sealed record ResourcePairView(ResourceGroupView? Left, ResourceGroupView? Right);

// The TLP tab (each server's ruleset), the ruleset line on Leveling and Resource Hunter, Resource Hunter bonus sizes, the Legacy
// bonus column and challenge tags on fights.
public sealed partial class MainViewModel
{
    /// <summary>Every TLP server with a known ruleset: the servers your logs are on first.</summary>
    public ObservableCollection<TlpServerView> TlpTabs { get; } = new();

    [ObservableProperty]
    private TlpServerView? _selectedTlpTab;

    /// <summary>"Next: Planes of Power + The Legacy of Ykesha on Feb 4, 2027 (74 days) · level cap 65" for the Leveling tab's server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleset))]
    private string _rulesetNext = string.Empty;

    /// <summary>True when the Leveling tab's server has a known ruleset (the ruleset lines show).</summary>
    public bool HasRuleset => RulesetNext.Length > 0;

    /// <summary>"Now: level cap 60 · EXP 100% · Loot 125% · …".</summary>
    [ObservableProperty]
    private string _rulesetNow = string.Empty;

    /// <summary>"Experience bonus here ≈ +40% (…)" for the picked Resource Hunter zone.</summary>
    [ObservableProperty]
    private string _resourceBonusSize = string.Empty;

    /// <summary>True when any character has a legacy bonus (the Experience tab's column shows only then).</summary>
    [ObservableProperty]
    private bool _showLegacyColumn;

    /// <summary>The Resource Hunter tables in rows of two: Experience and AA, Skill and Faction, … No bonus and Unknown last.</summary>
    public ObservableCollection<ResourcePairView> ResourcePairs { get; } = new();

    private DateOnly _tlpBuiltFor;
    private string _tlpServersSeen = string.Empty;
    private string? _rulesetFor;
    private DateOnly _rulesetDay;
    private readonly Dictionary<long, string> _challengeTags = new();

    private static readonly (ResourceBonus Left, ResourceBonus Right)[] ResourcePairOrder =
    {
        (ResourceBonus.Experience, ResourceBonus.Aa), (ResourceBonus.Skill, ResourceBonus.Faction), (ResourceBonus.Loot, ResourceBonus.Coin),
        (ResourceBonus.RareSpawn, ResourceBonus.Respawn), (ResourceBonus.None, ResourceBonus.Unknown),
    };

    // ───────────── Daily reset, bonus alerts, unlock reminders ─────────────

    /// <summary>How often the bonuses are checked during the first hours after they are drawn (players and websites fill them in slowly).</summary>
    public static readonly TimeSpan ResourceBurstEvery = TimeSpan.FromMinutes(10);

    /// <summary>How long after the daily draw the bonuses are checked every <see cref="ResourceBurstEvery"/>.</summary>
    public static readonly TimeSpan ResourceBurstFor = TimeSpan.FromHours(3);

    private DateTime _resetBurstUntil = DateTime.MinValue;
    private readonly HashSet<string> _announcedBonuses = new(StringComparer.Ordinal);
    private Dictionary<string, string> _unlockReminders = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _remindersChecked;

    /// <summary>True in the first hours after midnight Pacific or after a log showed the bonuses drawn again.</summary>
    private bool InResetBurst(DateTime nowUtc)
    {
        DateTime pacific = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), PacificZone.Value);
        return pacific.TimeOfDay < ResourceBurstFor || nowUtc < _resetBurstUntil;
    }

    private static readonly Lazy<TimeZoneInfo> PacificZone = new(() =>
        TimeZoneInfo.TryFindSystemTimeZoneById("America/Los_Angeles", out TimeZoneInfo? zone) ? zone
        : TimeZoneInfo.TryFindSystemTimeZoneById("Pacific Standard Time", out zone) ? zone
        : TimeZoneInfo.Utc);

    // On the tailing thread: a log showed the bonuses drawn again. The old ones are gone; the new ones take players time to report,
    // so the next check is in ten minutes and checks stay frequent for a few hours.
    private void OnResourceReset(string server) => _resets.Enqueue(server.ToLowerInvariant());

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _resets = new();

    /// <summary>On the UI thread: applies the draws the logs showed since the last tick.</summary>
    private void ApplyResets(DateTime nowUtc)
    {
        while (_resets.TryDequeue(out string? server))
        {
            _resetBurstUntil = nowUtc + ResourceBurstFor;
            _nextResourceDownload[server] = (nowUtc + ResourceBurstEvery, ResourceHunterText.Day(nowUtc));
        }
    }

    /// <summary>
    /// Notes on the Tells overlay the Experience and AA zones of the day whose levels fit one of your active characters on the server,
    /// once per zone and day.
    /// </summary>
    private void AnnounceBonuses(string server, string day)
    {
        if (!ResourceHunterNotices)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        var active = _pipeline.Activity.LastLines().Where(a => now - a.LastLineUtc < TimeSpan.FromSeconds(60)).Select(a => a.Character)
            .Where(c => Logs.Any(l => l.CharacterName.Equals(c, StringComparison.OrdinalIgnoreCase) && l.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase)))
            .Select(c => (Name: c, Level: _pipeline.Roster.GetLevel(c, server))).Where(c => c.Level > 0).ToList();
        if (active.Count == 0)
        {
            return;
        }

        foreach (ResourceZoneBonus z in _pipeline.ResourceHunter.OnDay(server, day)
                     .Where(z => z.Bonus is ResourceBonus.Experience or ResourceBonus.Aa && z.MaxLevel > 0))
        {
            var fits = active.Where(c => c.Level >= z.MinLevel && c.Level <= z.MaxLevel).Select(c => c.Name).ToList();
            if (fits.Count == 0 || !_announcedBonuses.Add($"{server.ToLowerInvariant()}|{day}|{ResourceHunterText.ZoneKey(z.Zone)}|{z.Bonus}"))
            {
                continue;
            }

            CultureInfo culture = CultureInfo.CurrentCulture;
            _pipeline.Notices.Add("bonus",
                $"{z.Zone}: {ResourceHunterText.Label(z.Bonus)} ({z.MinLevel.ToString(culture)}–{z.MaxLevel.ToString(culture)}) for {string.Join(", ", fits)}",
                BoxAlertClock());
        }
    }

    /// <summary>A Windows notice once a day from 7 days before an unlock through its day, for servers your characters are on.</summary>
    private void RemindUnlocks(DateOnly today)
    {
        if (_remindersChecked == today)
        {
            return;
        }

        _remindersChecked = today;
        CultureInfo culture = CultureInfo.CurrentCulture;
        string day = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (TlpServer server in Logs.Select(l => TlpServers.Find(l.ServerName)).OfType<TlpServer>().Distinct())
        {
            if (server.Next(today) is not { Date: { } date } unlock || date.DayNumber - today.DayNumber is < 0 or > 7
                || _unlockReminders.GetValueOrDefault(server.Key) == day)
            {
                continue;
            }

            int days = date.DayNumber - today.DayNumber;
            string when = days switch { 0 => "Today", 1 => "Tomorrow", _ => $"In {days.ToString(culture)} days" };
            int? cap = server.LevelCap(today);
            string capText = unlock.LevelCap is { } c && c > (cap ?? 0) ? $" (level cap {c.ToString(culture)})" : string.Empty;
            _unlockReminders[server.Key] = day;
            LiveNoticeRequested?.Invoke(new NoticeInfo($"{when}: {unlock.Title}{(unlock.Estimated ? " (expected)" : string.Empty)}",
                $"{server.Name}{capText}, {date.ToString("MMM d", culture)}."), false);
            SaveUnlessRestoring();
        }
    }

    /// <summary>Every tick: rebuilds the TLP tabs on a new day or when new servers show up, and the ruleset line when the server changes.</summary>
    private void RefreshTlp(DateTime nowUtc)
    {
        DateOnly today = TlpServers.Today(nowUtc);
        ApplyResets(nowUtc);
        RemindUnlocks(today);
        string seen = string.Join("|", Logs.Select(l => l.ServerName.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));
        if (today != _tlpBuiltFor || seen != _tlpServersSeen)
        {
            _tlpBuiltFor = today;
            _tlpServersSeen = seen;
            BuildTlpTabs(today);
        }

        if (_rulesetFor != SelectedLevelingServer || _rulesetDay != today)
        {
            _rulesetFor = SelectedLevelingServer;
            _rulesetDay = today;
            (RulesetNext, RulesetNow) = RulesetLines(TlpServers.Find(SelectedLevelingServer), today);
            if (SelectedLevelingServer is { } server && TlpTabs.FirstOrDefault(t => TlpServers.Find(server)?.Key == t.Key) is { } tab)
            {
                SelectedTlpTab = tab;
            }
        }
    }

    private void BuildTlpTabs(DateOnly today)
    {
        string? keep = SelectedTlpTab?.Key;
        var mine = Logs.Select(l => TlpServers.Find(l.ServerName)?.Key).OfType<string>().ToHashSet(StringComparer.Ordinal);
        CultureInfo culture = CultureInfo.CurrentCulture;
        TlpTabs.Clear();
        foreach (TlpServer server in TlpServers.All.OrderBy(s => !mine.Contains(s.Key)))
        {
            TlpUnlock? next = server.Next(today);
            var open = server.Open(today).ToHashSet();
            var rows = server.Unlocks.Select(u => new TlpUnlockView(
                u.Title,
                u.Date is { } d ? d.ToString("MMM d, yyyy", culture) + (u.Estimated ? " ?" : string.Empty) : "—",
                u.LevelCap?.ToString(culture) ?? "—",
                u.Bonus,
                open.Contains(u) ? "Open" : ReferenceEquals(u, next) ? NextStatus(u, today) : string.Empty,
                ReferenceEquals(u, next))).ToList();
            (string nextLine, string nowLine) = RulesetLines(server, today);
            TlpTabs.Add(new TlpServerView(server.Key, server.Name, server.Rules, "Source: " + server.Source, nowLine, nextLine, rows, server.HasBonuses,
                server.ChallengeList ?? Array.Empty<TlpChallenge>()));
        }

        SelectedTlpTab = TlpTabs.FirstOrDefault(t => t.Key == keep)
                         ?? TlpTabs.FirstOrDefault(t => t.Key == TlpServers.Find(SelectedLevelingServer)?.Key)
                         ?? TlpTabs.FirstOrDefault();
    }

    private static string NextStatus(TlpUnlock unlock, DateOnly today) =>
        unlock.Date is { } d ? $"Next · {(d.DayNumber - today.DayNumber).ToString(CultureInfo.CurrentCulture)} days" : "Next";

    /// <summary>The "Next: …" and "Now: …" lines for a server (empty for servers without a known ruleset).</summary>
    public static (string Next, string Now) RulesetLines(TlpServer? server, DateOnly today)
    {
        if (server is null)
        {
            return (string.Empty, string.Empty);
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        int? cap = server.LevelCap(today);
        string next;
        if (server.Next(today) is not { } unlock)
        {
            next = "Every expansion on the schedule is open.";
        }
        else
        {
            string when = unlock.Date is { } d
                ? $" on {d.ToString("MMM d, yyyy", culture)}{(unlock.Estimated ? " (worked out from the server's cadence)" : string.Empty)} ({(d.DayNumber - today.DayNumber).ToString(culture)} days)"
                : " (date not published)";
            string capText = unlock.LevelCap is { } c && c > (cap ?? 0) ? $" · level cap {c.ToString(culture)}" : string.Empty;
            next = $"Next: {unlock.Title}{when}{capText}";
        }

        var now = new List<string>();
        if (cap is { } levelCap)
        {
            now.Add($"level cap {levelCap.ToString(culture)}");
        }

        if (server.ExperienceOn(today) is { } rate)
        {
            now.Add($"EXP {rate.ToString("0", culture)}%");
        }

        now.AddRange(server.CurrentBonuses(today).Where(b => !b.StartsWith("EXP ", StringComparison.Ordinal)));
        IReadOnlySet<int> open = server.OpenExpansions(today);
        if (server.Unlocks.Any(u => u.Expansions.Contains(13)) && !open.Contains(13))
        {
            now.Add("hot zones with The Serpent's Spine");
        }

        if (server.Unlocks.Any(u => u.Expansions.Contains(16)) && !open.Contains(16))
        {
            now.Add("mercenaries with Seeds of Destruction");
        }

        return (next, "Now: " + string.Join(" · ", now));
    }

    /// <summary>Puts a picture of the shown day's Resource Hunter tables on the clipboard, for Discord.</summary>
    [RelayCommand]
    private void CopyResourcePicture()
    {
        if (SelectedLevelingServer is not { } server || ResourcePairs.Count == 0)
        {
            ResourceHunterStatus = "Nothing to copy: no bonuses are known for this day yet.";
            return;
        }

        string day = SelectedResourceDay is { } d && d != ResourceDayToday ? d : ResourceHunterText.Day(DateTime.UtcNow);
        string shown = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dddd, MMM d, yyyy", CultureInfo.CurrentCulture);
        string name = TlpServers.Find(server)?.Name ?? server;
        try
        {
            _clipboard.SetResourceBonusImage($"Resource Hunter · {name} · {shown}", ResourcePairs.ToList(), ActivePalette, WindowTitle);
            ResourceHunterStatus = "Copied a picture of the bonus tables. Paste it into Discord.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            ResourceHunterStatus = "The picture could not be copied: " + ex.Message;
        }
    }

    // ───────────── Leveling data from other servers ─────────────

    /// <summary>How often the other TLP servers' shared leveling is downloaded.</summary>
    public static readonly TimeSpan OtherServersEvery = TimeSpan.FromHours(6);

    private readonly Dictionary<string, (DateTime At, SharedLeveling Data)> _otherLeveling = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Downloads the shared leveling of the TLP servers your characters are not on (at most every <see cref="OtherServersEvery"/>).</summary>
    private async Task DownloadOtherServersAsync(ILevelingService service, IEnumerable<string> mine, DateTime nowUtc)
    {
        if (!UseSharedLeveling)
        {
            return;
        }

        var own = mine.Select(s => TlpServers.Find(s)?.Key ?? s.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (TlpServer other in TlpServers.All.Where(s => !own.Contains(s.Key)))
        {
            if (_otherLeveling.TryGetValue(other.Key, out var had) && nowUtc - had.At < OtherServersEvery)
            {
                continue;
            }

            try
            {
                _otherLeveling[other.Key] = (nowUtc, await service.DownloadAsync(other.Key, _levelingId.Length > 0 ? _levelingId : "none"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _otherLeveling[other.Key] = (nowUtc, had.Data ?? new SharedLeveling(Array.Empty<LevelingRow>(), Array.Empty<string>(), 0));
            }
        }
    }

    /// <summary>
    /// The other servers' zones for a band, each server's experience scaled from its rate today to <paramref name="server"/>'s rate
    /// (servers without a published rate count as equal).
    /// </summary>
    private IReadOnlyList<ZoneRank> OtherServersRanking(string server, int band, LevelingMode mode, Func<string, bool> allowed)
    {
        DateOnly today = TlpServers.Today(DateTime.UtcNow);
        double? mineRate = TlpServers.Find(server)?.ExperienceOn(today);
        string mineKey = TlpServers.Find(server)?.Key ?? server.ToLowerInvariant();
        var rows = new List<LevelingRow>();
        foreach ((string key, (DateTime _, SharedLeveling data)) in _otherLeveling)
        {
            if (key.Equals(mineKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double? theirRate = TlpServers.Find(key)?.ExperienceOn(today);
            double factor = mineRate is { } m && theirRate is { } t && t > 0 ? m / t : 1;
            rows.AddRange(data.Rows.Select(r => r with { Xp = r.Xp * factor, Player = key + ":" + r.Player }));
        }

        return rows.Count == 0 ? Array.Empty<ZoneRank>() : LevelingRanking.Rank(rows, band, mode, allowed);
    }

    // ───────────── Experience scaling (Leveling) ─────────────

    private readonly HashSet<string> _levelingAdjustedZones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Your stretches on a server, with experience recorded under an older server rate scaled to today's rate (servers with a
    /// published rate only); the zones that were scaled are remembered for the Data column.
    /// </summary>
    private IReadOnlyList<LevelingStretch> ScaledStretches(string server, HashSet<string>? adjusted = null)
    {
        adjusted?.Clear();
        IReadOnlyList<LevelingStretch> stretches = _leveling.Stretches(server);
        if (TlpServers.Find(server) is not { } tlp || tlp.ExperienceOn(TlpServers.Today(DateTime.UtcNow)) is not { } now)
        {
            return stretches;
        }

        return stretches.Select(s =>
        {
            if (s.Xp <= 0 || tlp.ExperienceOn(TlpServers.Today(DateTimeOffset.FromUnixTimeSeconds(s.Start).UtcDateTime)) is not { } then || then <= 0
                || Math.Abs(then - now) < 0.001)
            {
                return s;
            }

            adjusted?.Add(s.Zone);
            return s with { Xp = s.Xp * now / then };
        }).ToList();
    }

    // ───────────── Resource Hunter: bonus size, two columns ─────────────

    /// <summary>Puts the day's tables in rows of two (a row is left out only when both of its bonuses are missing).</summary>
    private void FillResourcePairs()
    {
        ResourcePairs.Clear();
        ResourceGroupView? Group(ResourceBonus bonus) => ResourceGroups.FirstOrDefault(g => g.Bonus == bonus);
        foreach ((ResourceBonus left, ResourceBonus right) in ResourcePairOrder)
        {
            ResourceGroupView? l = Group(left);
            ResourceGroupView? r = Group(right);
            if (l is not null || r is not null)
            {
                ResourcePairs.Add(new ResourcePairView(l, r));
            }
        }
    }

    private static readonly (ResourceBonus Bonus, string Kind, string What)[] SizeKinds =
    {
        (ResourceBonus.Experience, "xp", "XP"), (ResourceBonus.Aa, "aa", "AA"), (ResourceBonus.Coin, "coin", "coin"),
    };

    private static bool Counts(string kind, LevelingStretch s) => kind switch
    {
        "xp" => s.Xp > 0,
        "aa" => s.Xp <= 0 && s.Aa > 0,
        _ => s.Copper > 0,
    };

    private static double Amount(string kind, LevelingStretch s) => kind switch
    {
        "xp" => s.Xp,
        "aa" => s.Aa,
        _ => s.Copper,
    };

    /// <summary>
    /// The bonus sizes your own leveling measured on a server: for each zone, kind and bonus day with <see cref="MinBonusKills"/> kills,
    /// the change in XP, AA or coin per kill against that zone's days without a bonus (needs <see cref="MinBonusDays"/> of them). The
    /// size changes every day, so each day stands on its own.
    /// </summary>
    public IReadOnlyList<ResourceSize> OwnBonusSizes(string server)
    {
        ResourceHunterStore store = _pipeline.ResourceHunter;
        var sizes = new List<ResourceSize>();
        var byZone = ScaledStretches(server).Where(s => s.Kills > 0)
            .Select(s => (Stretch: s, Day: ResourceHunterText.Day(DateTimeOffset.FromUnixTimeSeconds(s.Start).UtcDateTime)))
            .GroupBy(x => ResourceHunterText.ZoneKey(x.Stretch.Zone));
        foreach (var zone in byZone)
        {
            string name = zone.First().Stretch.Zone;
            var days = zone.Select(x => (x.Stretch, x.Day, Bonus: store.BonusOnDay(server, x.Day, name))).ToList();
            foreach ((ResourceBonus bonus, string kind, _) in SizeKinds)
            {
                var off = days.Where(d => d.Bonus == ResourceBonus.None && Counts(kind, d.Stretch)).ToList();
                if (off.Select(d => d.Day).Distinct().Count() < MinBonusDays || off.Sum(d => d.Stretch.Kills) == 0)
                {
                    continue;
                }

                double baseline = off.Sum(d => Amount(kind, d.Stretch)) / off.Sum(d => d.Stretch.Kills);
                if (baseline <= 0)
                {
                    continue;
                }

                foreach (var day in days.Where(d => d.Bonus == bonus && Counts(kind, d.Stretch)).GroupBy(d => d.Day))
                {
                    int kills = day.Sum(d => d.Stretch.Kills);
                    if (kills >= MinBonusKills)
                    {
                        double change = 100 * ((day.Sum(d => Amount(kind, d.Stretch)) / kills / baseline) - 1);
                        sizes.Add(new ResourceSize(day.Key, name, kind, change));
                    }
                }
            }
        }

        return sizes;
    }

    // Everyone's sizes for the picked zone (by zone key) and for today's zones (by server), from the EQ Overwatch server.
    private readonly Dictionary<string, (DateTime At, IReadOnlyList<ResourceSize> Sizes)> _zoneSizes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Day, IReadOnlyList<ResourceSize> Sizes)> _daySizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sharedSizes = new(StringComparer.Ordinal);

    /// <summary>Downloads everyone's sizes for a zone (at most every 10 minutes) and shows the details again when they arrive.</summary>
    private async void FetchZoneSizes(string server, string zone)
    {
        string key = server.ToLowerInvariant() + "|" + ResourceHunterText.ZoneKey(zone);
        if (_resourceService is not { } service || !ShareResourceBonuses
            || (_zoneSizes.TryGetValue(key, out var had) && DateTime.UtcNow - had.At < TimeSpan.FromMinutes(10)))
        {
            return;
        }

        _zoneSizes[key] = (DateTime.UtcNow, had.Sizes ?? Array.Empty<ResourceSize>());
        try
        {
            _zoneSizes[key] = (DateTime.UtcNow, await service.SizesAsync(server, null, zone));
            if (_resourcePickedZone == zone)
            {
                ResourceBonusSize = BonusSizeText(server, zone);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _resourceProblem = ex.Message;
        }
    }

    /// <summary>Shares your newly measured sizes of the last week and downloads everyone's for today (with the hourly share).</summary>
    private async Task ShareBonusSizesAsync(IResourceHunterService service, string server, string day, List<string> problems)
    {
        try
        {
            string oldest = ResourceHunterText.Day(DateTime.UtcNow.AddDays(-7));
            var fresh = OwnBonusSizes(server)
                .Where(x => string.CompareOrdinal(x.Day, oldest) >= 0)
                .Where(x => !_sharedSizes.Contains(SizeKey(server, x)))
                .ToList();
            if (fresh.Count > 0)
            {
                EnsureSharingId();
                if (await service.UploadSizesAsync(server, _levelingId, _levelingKey, fresh))
                {
                    _sharedSizes.UnionWith(fresh.Select(x => SizeKey(server, x)));
                }
            }

            _daySizes[server] = (day, await service.SizesAsync(server, day, null));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add(ex.Message);
        }
    }

    private static string SizeKey(string server, ResourceSize x) =>
        $"{server.ToLowerInvariant()}|{x.Day}|{ResourceHunterText.ZoneKey(x.Zone)}|{x.Kind}|{x.Percent.ToString("0.0", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The picked zone's bonus sizes: today's (everyone's median next to yours) and recent past days with their average. Everyone's
    /// size counts once <see cref="MinSizeReports"/> players reported it.
    /// </summary>
    private string BonusSizeText(string server, string zone)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string key = ResourceHunterText.ZoneKey(zone);
        string today = ResourceHunterText.Day(DateTime.UtcNow);
        string Percent(double change) => (change >= 0 ? "+" : string.Empty) + change.ToString("0", culture) + "%";
        var own = OwnBonusSizes(server).Where(x => ResourceHunterText.ZoneKey(x.Zone) == key).ToList();
        var shared = (_zoneSizes.TryGetValue(server.ToLowerInvariant() + "|" + key, out var got) ? got.Sizes : Array.Empty<ResourceSize>())
            .Concat(_daySizes.TryGetValue(server, out var daily) && daily.Day == today
                ? daily.Sizes.Where(x => ResourceHunterText.ZoneKey(x.Zone) == key)
                : Array.Empty<ResourceSize>())
            .Where(x => x.Reports >= MinSizeReports)
            .GroupBy(x => (x.Day, x.Kind)).Select(g => g.First()).ToList();
        var lines = new List<string>();
        foreach ((ResourceBonus bonus, string kind, _) in SizeKinds)
        {
            var mine = own.Where(x => x.Kind == kind).ToDictionary(x => x.Day, x => x.Percent);
            var theirs = shared.Where(x => x.Kind == kind).ToDictionary(x => x.Day, x => x);
            var days = mine.Keys.Concat(theirs.Keys).Distinct().OrderByDescending(d => d, StringComparer.Ordinal).ToList();
            if (days.Count == 0)
            {
                continue;
            }

            string label = ResourceHunterText.Label(bonus);
            string Day(string day) => theirs.TryGetValue(day, out ResourceSize? all)
                ? $"{Percent(all.Percent)} ({all.Reports.ToString(culture)} players{(mine.TryGetValue(day, out double m) ? " · yours " + Percent(m) : string.Empty)})"
                : $"{Percent(mine[day])} (yours)";
            if (days[0] == today)
            {
                lines.Add($"{label} bonus today ≈ {Day(today)}");
            }

            var past = days.Where(d => d != today).ToList();
            if (past.Count > 0)
            {
                string list = string.Join(" · ", past.Take(6).Select(d =>
                    DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MMM d", culture) + " " + Day(d)));
                double average = past.Average(d => theirs.TryGetValue(d, out ResourceSize? all) ? all.Percent : mine[d]);
                lines.Add($"{label} bonus on past days: {list} — average {Percent(average)}");
            }
        }

        return lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : $"Bonus size changes every day. It shows for a day once someone has {MinBonusKills.ToString(culture)} kills here on it and {MinBonusDays.ToString(culture)} days here without a bonus.";
    }

    /// <summary>Players needed before everyone's size for a day is shown.</summary>
    public const int MinSizeReports = 2;

    /// <summary>
    /// Today's estimated Experience bonus in a zone (percent): everyone's median for the zone, else your own, else the day's median
    /// over the server's Experience zones; null when nothing is known.
    /// </summary>
    private double? TodayExperienceSize(string server, string zone)
    {
        string today = ResourceHunterText.Day(DateTime.UtcNow);
        string key = ResourceHunterText.ZoneKey(zone);
        var day = _daySizes.TryGetValue(server, out var daily) && daily.Day == today ? daily.Sizes.Where(x => x.Kind == "xp").ToList() : new List<ResourceSize>();
        if (day.FirstOrDefault(x => ResourceHunterText.ZoneKey(x.Zone) == key) is { } zoneSize)
        {
            return zoneSize.Percent;
        }

        if (OwnBonusSizes(server).FirstOrDefault(x => x.Day == today && x.Kind == "xp" && ResourceHunterText.ZoneKey(x.Zone) == key) is { } mine)
        {
            return mine.Percent;
        }

        return day.Count > 0 ? day.Select(x => x.Percent).Order().ElementAt(day.Count / 2) : null;
    }

    /// <summary>Kills on a bonus day needed before that day's bonus size is shown.</summary>
    public const int MinBonusKills = 20;

    /// <summary>Days of leveling in a zone without a bonus needed before a bonus size can be measured there.</summary>
    public const int MinBonusDays = 3;

    // ───────────── Legacy bonus (Experience tab) ─────────────

    /// <summary>
    /// "100% (Nov 18)": the character's legacy bonus from its log; without one, the newest from another character on the same account
    /// and server ("100% (from Ream, Nov 18)"); a dash otherwise. Warns on a day an unlock raises the level cap.
    /// </summary>
    public string LegacyBonusText(string character)
    {
        string server = Logs.FirstOrDefault(l => l.CharacterName.Equals(character, StringComparison.OrdinalIgnoreCase))?.ServerName ?? string.Empty;
        CultureInfo culture = CultureInfo.CurrentCulture;
        string When(DateTime? seen) => seen is { } at ? at.ToLocalTime().ToString("MMM d", culture) : string.Empty;
        string reset = string.Empty;
        DateOnly today = TlpServers.Today(DateTime.UtcNow);
        if (TlpServers.Find(server) is { Legacy: true } tlp
            && tlp.Unlocks.FirstOrDefault(u => u.Date == today && !u.Estimated) is { LevelCap: { } newCap }
            && newCap > (tlp.LevelCap(today.AddDays(-1)) ?? newCap))
        {
            reset = $" · resets today until a character reaches {newCap.ToString(culture)}";
        }

        if (_pipeline.Progress.Legacy(character, server).LegacyBonus is { } own)
        {
            string seen = When(_pipeline.Progress.LegacyBonusSeen(character, server));
            return $"{own.ToString(culture)}%{(seen.Length > 0 ? $" ({seen})" : string.Empty)}{reset}";
        }

        string account = AccountOf(character, server);
        if (account.Length == 0)
        {
            return "—";
        }

        var best = Logs
            .Where(l => l.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase) && !l.CharacterName.Equals(character, StringComparison.OrdinalIgnoreCase)
                        && AccountOf(l.CharacterName, server).Equals(account, StringComparison.OrdinalIgnoreCase))
            .Select(l => (l.CharacterName, Bonus: _pipeline.Progress.Legacy(l.CharacterName, server).LegacyBonus, Seen: _pipeline.Progress.LegacyBonusSeen(l.CharacterName, server)))
            .Where(x => x.Bonus is not null)
            .OrderByDescending(x => x.Seen ?? DateTime.MinValue)
            .FirstOrDefault();
        if (best.Bonus is not { } shared)
        {
            return "—";
        }

        string when = When(best.Seen);
        return $"{shared.ToString(culture)}% (from {best.CharacterName}{(when.Length > 0 ? ", " + when : string.Empty)}){reset}";
    }

    // ───────────── Challenge tags (Encounter DPS) ─────────────

    /// <summary>
    /// Tags live fights on challenge servers where one of your characters was at least 10 levels below the original level cap of the
    /// zone's expansion ("Challenge: Ream 50 vs The Shadows of Luclin cap 60"). Decided once, while the fight is fresh.
    /// </summary>
    private void MarkChallengeRows()
    {
        ZoneDirectory? zones = null;
        foreach (EncounterRowViewModel row in Encounters)
        {
            if (_challengeTags.TryGetValue(row.Key, out string? known))
            {
                row.ChallengeText = known;
                continue;
            }

            if (row.IsImported || row.Snapshot is not { } fight || DateTime.Now - fight.EndTime > TimeSpan.FromMinutes(2))
            {
                continue;
            }

            string tag = string.Empty;
            foreach (ParticipantSnapshot p in fight.Participants)
            {
                if (!_ownCharacters.TryGetValue(p.Name, out var own) || TlpServers.Find(own.Server) is not { Challenges: true }
                    || _pipeline.Zones.Get(p.Name, own.Server)?.Zone is not { } zone)
                {
                    continue;
                }

                zones ??= LevelingZones();
                int level = _pipeline.Roster.GetLevel(p.Name, own.Server);
                if (LevelingRanking.ExpansionOf(zones, zone) is { } expansion && TlpServers.OriginalCap(expansion) is { } cap && level > 0 && level <= cap - 10)
                {
                    tag = $"Challenge: {p.Name} {level.ToString(CultureInfo.CurrentCulture)} vs {ZoneDirectory.ExpansionName(expansion)} cap {cap.ToString(CultureInfo.CurrentCulture)}";
                    break;
                }
            }

            row.ChallengeText = tag;
            if (!fight.IsActive)
            {
                _challengeTags[row.Key] = tag;
            }
        }
    }
}
