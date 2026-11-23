using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
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

    /// <summary>Every tick: rebuilds the TLP tabs on a new day or when new servers show up, and the ruleset line when the server changes.</summary>
    private void RefreshTlp(DateTime nowUtc)
    {
        DateOnly today = TlpServers.Today(nowUtc);
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

    /// <summary>
    /// How much a bonus seems to add in a zone, from your own leveling there: XP, AA or coin per kill on bonus days against days with
    /// no bonus (needs <see cref="MinBonusDays"/> days of each).
    /// </summary>
    private string BonusSizeText(string server, string zone)
    {
        ResourceHunterStore store = _pipeline.ResourceHunter;
        string key = ResourceHunterText.ZoneKey(zone);
        var days = ScaledStretches(server)
            .Where(s => ResourceHunterText.ZoneKey(s.Zone) == key && s.Kills > 0)
            .Select(s => (Stretch: s, Day: ResourceHunterText.Day(DateTimeOffset.FromUnixTimeSeconds(s.Start).UtcDateTime)))
            .Select(x => (x.Stretch, x.Day, Bonus: store.BonusOnDay(server, x.Day, zone)))
            .ToList();
        CultureInfo culture = CultureInfo.CurrentCulture;
        var lines = new List<string>();
        void Measure(ResourceBonus bonus, string what, Func<LevelingStretch, bool> counts, Func<LevelingStretch, double> amount)
        {
            var on = days.Where(d => d.Bonus == bonus && counts(d.Stretch)).ToList();
            var off = days.Where(d => d.Bonus == ResourceBonus.None && counts(d.Stretch)).ToList();
            int onDays = on.Select(d => d.Day).Distinct().Count();
            int offDays = off.Select(d => d.Day).Distinct().Count();
            if (onDays < MinBonusDays || offDays < MinBonusDays)
            {
                return;
            }

            double perKillOn = on.Sum(d => amount(d.Stretch)) / on.Sum(d => d.Stretch.Kills);
            double perKillOff = off.Sum(d => amount(d.Stretch)) / off.Sum(d => d.Stretch.Kills);
            if (perKillOff <= 0)
            {
                return;
            }

            double change = (perKillOn / perKillOff) - 1;
            lines.Add($"{ResourceHunterText.Label(bonus)} bonus here ≈ {(change >= 0 ? "+" : string.Empty)}{(change * 100).ToString("0", culture)}% " +
                      $"(your {what} per kill, {onDays.ToString(culture)} bonus days vs {offDays.ToString(culture)} normal days)");
        }

        Measure(ResourceBonus.Experience, "XP", s => s.Xp > 0, s => s.Xp);
        Measure(ResourceBonus.Aa, "AA", s => s.Xp <= 0 && s.Aa > 0, s => s.Aa);
        Measure(ResourceBonus.Coin, "coin", s => s.Copper > 0, s => s.Copper);
        return lines.Count > 0
            ? string.Join("\n", lines)
            : $"Bonus size: shows once your own leveling here has {MinBonusDays.ToString(culture)} days with the bonus and {MinBonusDays.ToString(culture)} without.";
    }

    /// <summary>Days of your own leveling needed with and without a bonus before its size is shown.</summary>
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
