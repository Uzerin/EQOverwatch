using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Drops;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Resists;
using EQOverwatch.Core.Spells;

namespace EQOverwatch.App.ViewModels;

/// <summary>What the mob card shows for a mob you /con: a heading and the lines the app has something for.</summary>
public sealed record MobCardInfo(string Mob, string Heading, IReadOnlyList<string> Lines, IReadOnlyList<MobCardSpell>? Spells = null)
{
    /// <summary>A raid boss's spells (empty for other mobs).</summary>
    public IReadOnlyList<MobCardSpell> SpellRows => Spells ?? Array.Empty<MobCardSpell>();
}

/// <summary>
/// One row of a raid boss's spells on the mob card: the spell, its resist type and adjustment (from the spell file), the shortest time
/// between two casts and how many casts were seen. <see cref="IsHard"/> marks an adjustment of −100 or lower (very hard to resist).
/// </summary>
public sealed record MobCardSpell(string Spell, string Resist, string Adjust, bool IsHard, string MinGap, string Seen);

// Alerts for your boxes (a mob attacking a character that is not a tank, a character that can't reach its target) and the mob card shown
// when you /con a mob.
public partial class MainViewModel
{
    /// <summary>The source of the box alerts' banners and speech in the trigger engine.</summary>
    public static readonly Guid BoxAlertSourceId = new("7d1f0a3c-5b2e-4c86-9e4a-0b6f3d8c2a71");

    private const string BoxAlertSourceName = "Box alerts";

    /// <summary>The same character's aggro alert is not repeated sooner than this.</summary>
    public static readonly TimeSpan AggroQuiet = TimeSpan.FromSeconds(15);

    /// <summary>The same character's range alert is not repeated sooner than this.</summary>
    public static readonly TimeSpan RangeQuiet = TimeSpan.FromSeconds(20);

    private const double AggroBannerSeconds = 6;

    private readonly object _boxAlertGate = new();
    private readonly Dictionary<string, DateTime> _lastBoxAlert = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyDictionary<string, (string Server, bool Tank)> _ownCharacters =
        new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clock the box alerts use (replaceable in tests).</summary>
    public Func<DateTime> BoxAlertClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Speak and show "X is being attacked by Y" when a mob attacks one of your characters that is not a tank.</summary>
    [ObservableProperty]
    private bool _aggroAlert = true;

    /// <summary>Speak "X out of range" or "X can't see the target" when one of your characters can't reach its target in a fight.</summary>
    [ObservableProperty]
    private bool _rangeAlert = true;

    /// <summary>Show how far along a fight is on the DPS overlay, from the recorded kills of the same mob.</summary>
    [ObservableProperty]
    private bool _showFightProgress = true;

    /// <summary>The mob card overlay: shows what the app knows about a mob when you /con it.</summary>
    [ObservableProperty]
    private bool _isMobCardOverlayVisible = true;

    /// <summary>Raised (on the tailing thread) with the card for a mob one of your characters just /con'd.</summary>
    public event Action<MobCardInfo>? MobCardReady;

    partial void OnAggroAlertChanged(bool value) => SaveUnlessRestoring();

    partial void OnRangeAlertChanged(bool value) => SaveUnlessRestoring();

    partial void OnShowFightProgressChanged(bool value) => SaveUnlessRestoring();

    partial void OnIsMobCardOverlayVisibleChanged(bool value) => OnOverlayVisibilityChanged(OverlayKind.MobCard, value);

    private void SaveUnlessRestoring()
    {
        if (!_restoring)
        {
            RequestSave();
        }
    }

    private void InitializeBoxAlerts()
    {
        _pipeline.LiveDps.PlayerAttacked += OnPlayerAttacked;
        _pipeline.LiveDps.TargetOutOfReach += OnTargetOutOfReach;
        _pipeline.LiveDps.MobConsidered += OnMobConsidered;
        RefreshOwnCharacters();
    }

    /// <summary>The estimate shown on the DPS overlay for a fight in progress, or null (switched off, or too few recorded kills).</summary>
    public FightEstimate? FightProgressFor(EncounterSnapshot encounter) =>
        ShowFightProgress ? FightProgress.Estimate(_bossHistory.Fights(), encounter.TargetName, server: null, encounter.TotalDamage, encounter.DurationSeconds) : null;

    private readonly object _regenGate = new();
    private readonly Dictionary<string, MobRegen?> _regenCache = new(StringComparer.OrdinalIgnoreCase);
    private ILookup<string, BossFight>? _regenFights;
    private bool _regenHooked;

    /// <summary>
    /// The mob's estimated regen and hit points from its recorded kills on every server (a mob is the same on every server), or null
    /// with too few kills. Kept until a fight is recorded, since the DPS overlay asks several times a second.
    /// </summary>
    public MobRegen? RegenFor(string mob)
    {
        if (string.IsNullOrWhiteSpace(mob) || mob == DpsOverlayViewModel.IdleTitle)
        {
            return null;
        }

        string key = mob.Trim();
        lock (_regenGate)
        {
            if (!_regenHooked)
            {
                _regenHooked = true;
                _bossHistory.Changed += () =>
                {
                    lock (_regenGate)
                    {
                        _regenFights = null;
                        _regenCache.Clear();
                    }
                };
            }

            _regenFights ??= _bossHistory.Fights().ToLookup(f => f.Boss.Trim(), StringComparer.OrdinalIgnoreCase);
            if (!_regenCache.TryGetValue(key, out MobRegen? regen))
            {
                regen = MobRegenEstimator.Estimate(_regenFights[mob.Trim()], mob) ?? SharedMobs()?.Regen(mob); // your own kills first
                _regenCache[key] = regen;
            }

            return regen;
        }
    }

    /// <summary>Forgets the worked-out regen (after shared mob stats arrive).</summary>
    private void ClearRegenCache()
    {
        lock (_regenGate)
        {
            _regenCache.Clear();
        }
    }

    /// <summary>The fight with its mob's estimated regen and hit points attached, for the Discord picture.</summary>
    private EncounterSnapshot WithRegen(EncounterSnapshot encounter) =>
        RegenFor(encounter.TargetName) is { } regen
            ? encounter with { RegenText = regen.Describe(), Timeline = encounter.Timeline?.WithBossHp(regen.HitPoints, regen.RegenPerSecond) }
            : encounter;

    /// <summary>Your characters (the watched logs) with their server and Tank box, kept for the tailing thread.</summary>
    private void RefreshOwnCharacters()
    {
        var own = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (WatchedLogConfig log in Logs)
        {
            own.TryAdd(log.CharacterName, (log.ServerName, log.IsTank));
        }

        _ownCharacters = own;
    }

    /// <summary>True when this alert for this character went off less than <paramref name="quiet"/> ago; otherwise notes it as going off now.</summary>
    private bool RecentlyAlerted(string key, DateTime now, TimeSpan quiet)
    {
        lock (_boxAlertGate)
        {
            if (_lastBoxAlert.TryGetValue(key, out DateTime last) && now - last < quiet && now >= last)
            {
                return true;
            }

            _lastBoxAlert[key] = now;
            return false;
        }
    }

    private void OnPlayerAttacked(string player, string mob)
    {
        if (!AggroAlert || !_ownCharacters.TryGetValue(player, out (string Server, bool Tank) own) || own.Tank)
        {
            return;
        }

        DateTime now = BoxAlertClock();
        if (RecentlyAlerted("aggro|" + player, now, AggroQuiet))
        {
            return;
        }

        string text = $"{player} is being attacked by {mob}";
        _pipeline.Triggers.StartAlert(BoxAlertSourceId, BoxAlertSourceName, player, text, AggroBannerSeconds, now);
        _pipeline.Triggers.PublishAudio(BoxAlertSourceId, BoxAlertSourceName, player, text, now);
    }

    private void OnTargetOutOfReach(string character, bool unseen)
    {
        if (!RangeAlert || !_ownCharacters.ContainsKey(character))
        {
            return;
        }

        DateTime now = BoxAlertClock();
        if (RecentlyAlerted("range|" + character, now, RangeQuiet))
        {
            return;
        }

        _pipeline.Triggers.PublishAudio(
            BoxAlertSourceId, BoxAlertSourceName, character, unseen ? $"{character} can't see the target" : $"{character} out of range", now);
    }

    private void OnMobConsidered(string character, string mob, int level)
    {
        if (!IsMobCardOverlayVisible || MobCardReady is null)
        {
            return;
        }

        string? server = _ownCharacters.TryGetValue(character, out (string Server, bool Tank) own) ? own.Server : null;
        string? zone = server is null ? null : _pipeline.Zones.Get(character, server)?.Zone;
        if (BuildMobCard(mob, level, server, zone) is { } card)
        {
            MobCardReady(card);
        }
    }

    /// <summary>"Lure of Ice 25% of 8 · Tashania 10% of 10": your spells cast at least <see cref="MobCardMinCasts"/> times on the mob this week.</summary>
    internal string YourSpellResists(string mob)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        return string.Join(" · ", _weekResists
            .Where(r => r.Mob.Equals(mob, StringComparison.OrdinalIgnoreCase) && r.Landed + r.Resisted >= MobCardMinCasts)
            .OrderByDescending(r => r.Landed + r.Resisted)
            .Take(4)
            .Select(r => $"{r.Spell} {r.ResistPercent.ToString("0", culture)}% of {(r.Landed + r.Resisted).ToString(culture)}"));
    }

    /// <summary>"Last killed Nov 24 21:05 · 12 kills" (the count only when <paramref name="withCount"/>): the kills of <paramref name="mob"/> in the fight list. Null when there are none.</summary>
    internal string? LastKilledLine(string mob, CultureInfo culture, bool withCount = true)
    {
        EncounterSnapshot[] kills = _pipeline.LiveDps.Snapshot.Completed
            .Concat(_pipeline.HistoricalDps.Snapshot.Completed)
            .Where(e => e.Killed && e.TargetName.Equals(mob, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (kills.Length == 0)
        {
            return null;
        }

        DateTime last = kills.Max(k => k.EndTime);
        string text = $"Last killed {last.ToString("MMM d HH:mm", culture)}";
        return withCount ? text + $" · {kills.Length.ToString(culture)} kill{(kills.Length == 1 ? string.Empty : "s")}" : text;
    }

    /// <summary>
    /// The mob card for <paramref name="mob"/>: its fights on <paramref name="server"/> (kills, attempts, times, best DPS), when it was last killed, the raid's
    /// resist rates and its drops. Null when the app has none of these for the mob. The level range seen is the one for
    /// <paramref name="zone"/> (the zone the character is in), since one mob name can have other levels in other zones.
    /// </summary>
    public MobCardInfo? BuildMobCard(string mob, int level, string? server, string? zone = null)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        var lines = new List<string>();

        BossFight[] fights = BossHistoryStore.OnServer(_bossHistory.Fights(), server)
            .Where(f => f.Boss.Equals(mob, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (fights.Length > 0)
        {
            BossFight[] kills = fights.Where(f => f.Killed).ToArray();
            int attempts = fights.Length - kills.Length;
            string where = string.IsNullOrEmpty(server) ? string.Empty : server + ": ";
            string text = $"{where}{kills.Length} kill{(kills.Length == 1 ? string.Empty : "s")}, {attempts} attempt{(attempts == 1 ? string.Empty : "s")}";
            if (kills.Length > 0)
            {
                text += $" · fastest {BossHistoryStore.FormatDuration(kills.Min(k => k.Seconds))}" +
                        $" · avg {BossHistoryStore.FormatDuration(kills.Average(k => k.Seconds))}" +
                        $" · best DPS {kills.Max(k => k.Dps).ToString("N0", culture)}";
            }

            lines.Add(text);
        }

        if (ServerFirstLine(mob, server) is { } first)
        {
            lines.Add(first);
        }

        if (RegenFor(mob) is { } regen) // no kill-time estimate here: that is on the DPS overlay during the fight
        {
            lines.Add(regen.Describe());
        }

        if (LastKilledLine(mob, culture, withCount: fights.Length == 0) is { } lastKilled) // the boss line already counts the kills
        {
            lines.Add(lastKilled);
        }

        MobResistReport report = _pipeline.ResistStore.Report(mob, raid: true);
        string[] resists = SpellResist.Main
            .Where(t => report.ByType.TryGetValue(t, out ResistCount c) && c.Total > 0)
            .Select(t => $"{SpellResist.Name(t)} {report.ByType[t].Percent.ToString("0", culture)}%")
            .ToArray();
        if (resists.Length > 0)
        {
            lines.Add("Resisted: " + string.Join(" · ", resists));
        }
        else if (SharedResistLine(mob) is { } everyone)
        {
            lines.Add(everyone);
        }

        if (YourSpellResists(mob) is { Length: > 0 } yours)
        {
            lines.Add("Your spells: " + yours);
        }

        if (_pipeline.MobHits.MaxHit(mob) is > 0 and int maxHit)
        {
            lines.Add($"Max hit: {maxHit.ToString("N0", culture)}");
        }

        if (!string.IsNullOrEmpty(server)
            && _pipeline.Drops.Table(server, DropScope.Everyone)
                .Where(r => r.Mob.Equals(mob, StringComparison.OrdinalIgnoreCase) && r.Kills > 0)
                .OrderByDescending(r => r.Kills)
                .FirstOrDefault() is { } drops
            && drops.Items.Count > 0)
        {
            lines.Add("Drops: " + string.Join(" · ", drops.Items
                .OrderByDescending(i => i.Drops)
                .Take(3)
                .Select(i => $"{i.Item} {i.Drops.ToString(culture)} in {drops.Kills.ToString(culture)} kill{(drops.Kills == 1 ? string.Empty : "s")}")));
        }

        IReadOnlyList<MobCardSpell> spells = IsRaidBoss(mob) || RaidRules.Any(r => r.MobText.Trim().Equals(mob.Trim(), StringComparison.OrdinalIgnoreCase))
            ? BossSpellRows(mob, culture) : Array.Empty<MobCardSpell>();
        if (lines.Count == 0 && spells.Count == 0)
        {
            return null;
        }

        string heading = $"{mob} · level {level.ToString(culture)}";
        if ((_mobLevels.Find(mob, zone) ?? SharedMobs(server)?.LevelRange(mob, zone)) is { } seen && seen.Lowest != seen.Highest)
        {
            heading += $" (seen {seen.Lowest.ToString(culture)}–{seen.Highest.ToString(culture)})";
        }

        return new MobCardInfo(mob, heading, lines, spells);
    }

    /// <summary>How many spells a raid boss's mob card lists.</summary>
    public const int MobCardSpellLimit = 6;

    /// <summary>
    /// A raid boss's spells for its mob card: the ones it cast in the fights the app has (most-cast first), then its raid trigger spells
    /// not seen yet, at most <see cref="MobCardSpellLimit"/>.
    /// </summary>
    public IReadOnlyList<MobCardSpell> BossSpellRows(string mob, CultureInfo culture)
    {
        IReadOnlyList<BossSpellSeen> seen = BossSpellSummary.For(
            _pipeline.LiveDps.Snapshot.All.Concat(_pipeline.HistoricalDps.Snapshot.All), mob);
        var rows = new List<(string Spell, BossSpellSeen? Seen)>(seen.Select(s => (s.Spell, (BossSpellSeen?)s)));
        foreach (RaidTimerRuleViewModel rule in RaidRules.Where(r => r.MobText.Trim().Equals(mob.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            string spell = rule.SpellName.Trim();
            if (spell.Length > 0 && !rows.Any(r => r.Spell.Equals(spell, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Add((spell, null));
            }
        }

        SpellDatabase database = _pipeline.Spells.Database;
        return rows.Take(MobCardSpellLimit).Select(r =>
        {
            SpellDefinition? definition = database.FindDefinitionByName(r.Spell);
            string resist = definition is null ? "—" : !definition.IsDetrimental ? "Beneficial" : SpellResist.Name(definition.ResistType);
            bool shows = definition is { IsDetrimental: true };
            string adjust = shows ? definition!.ResistAdjust.ToString("0;−0;0", culture) : "—";
            string gap = r.Seen?.MinGapSeconds is int seconds ? TimeSpan.FromSeconds(seconds).ToString(@"m\:ss", culture) : "—";
            string count = r.Seen is { } s ? $"{s.Casts.ToString("N0", culture)} cast{(s.Casts == 1 ? string.Empty : "s")}" : "trigger";
            return new MobCardSpell(r.Spell, resist, adjust, shows && definition!.ResistAdjust <= -100, gap, count);
        }).ToList();
    }
}
