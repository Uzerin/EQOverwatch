using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.History;
using EQOverwatch.Core.Leveling;
using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

// The Raid triggers list in groups: the boss's expansion, then the zone it was fought in (from your fights, or a consider line there).
// A whole group can be turned on or off at once.
public sealed partial class MainViewModel
{
    public const string UnknownZoneGroup = "Unknown zone";

    private ListCollectionView? _raidRulesView;
    private readonly Dictionary<string, (string Zone, DateTime Start)> _bossZones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Raid triggers, grouped and sorted for the list.</summary>
    public ICollectionView RaidRulesView
    {
        get
        {
            if (_raidRulesView is null)
            {
                foreach (RaidTimerRuleViewModel rule in RaidRules)
                {
                    SetRaidRuleGroup(rule);
                }

                RaidRules.CollectionChanged += OnRaidRulesChanged;
                _raidRulesView = new ListCollectionView(RaidRules) { IsLiveGrouping = true, IsLiveSorting = true };
                _raidRulesView.LiveGroupingProperties.Add(nameof(RaidTimerRuleViewModel.GroupTop));
                _raidRulesView.LiveGroupingProperties.Add(nameof(RaidTimerRuleViewModel.GroupSub));
                _raidRulesView.LiveSortingProperties.Add(nameof(RaidTimerRuleViewModel.GroupOrder));
                _raidRulesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RaidTimerRuleViewModel.GroupTop)));
                _raidRulesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RaidTimerRuleViewModel.GroupSub)));
                _raidRulesView.SortDescriptions.Add(new SortDescription(nameof(RaidTimerRuleViewModel.GroupOrder), ListSortDirection.Ascending));
                _raidRulesView.SortDescriptions.Add(new SortDescription(nameof(RaidTimerRuleViewModel.GroupTop), ListSortDirection.Ascending));
                _raidRulesView.SortDescriptions.Add(new SortDescription(nameof(RaidTimerRuleViewModel.GroupSub), ListSortDirection.Ascending));
                _raidRulesView.SortDescriptions.Add(new SortDescription(nameof(RaidTimerRuleViewModel.MobText), ListSortDirection.Ascending));
                _raidRulesView.SortDescriptions.Add(new SortDescription(nameof(RaidTimerRuleViewModel.SpellName), ListSortDirection.Ascending));
            }

            return _raidRulesView;
        }
    }

    private void OnRaidRulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (RaidTimerRuleViewModel rule in e.NewItems?.OfType<RaidTimerRuleViewModel>() ?? Enumerable.Empty<RaidTimerRuleViewModel>())
        {
            SetRaidRuleGroup(rule);
        }
    }

    /// <summary>
    /// Remembers where each boss was fought: the zone most of its fights were in (the newest fight's zone breaks a tie), so one fight
    /// tagged with another zone does not move it. Regroups the raid triggers when that changed.
    /// </summary>
    private void LearnBossZones(IEnumerable<EncounterSnapshot> encounters)
    {
        bool changed = false;
        foreach (var (mob, zone, start) in MostFoughtZones(encounters))
        {
            _bossZones.TryGetValue(mob, out var known);
            if (known.Zone == zone && known.Start == start)
            {
                continue;
            }

            _bossZones[mob] = (zone, start);
            changed = changed || (known.Zone != zone && RaidRules.Any(r => r.MobText.Equals(mob, StringComparison.OrdinalIgnoreCase)));
        }

        if (changed)
        {
            ApplyRaidTriggerGrouping();
        }
    }

    /// <summary>Each mob's most-fought zone (ties go to the newest fight) and its newest fight's start.</summary>
    public static IEnumerable<(string Mob, string Zone, DateTime Start)> MostFoughtZones(IEnumerable<EncounterSnapshot> encounters) =>
        encounters
            .Where(e => !string.IsNullOrWhiteSpace(e.Zone) && !string.IsNullOrWhiteSpace(e.TargetName))
            .GroupBy(e => e.TargetName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(mob =>
            {
                var best = mob.GroupBy(e => e.Zone.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(z => (Zone: z.Key, Count: z.Select(e => e.StartTime).Distinct().Count(), Last: z.Max(e => e.StartTime)))
                    .OrderByDescending(z => z.Count).ThenByDescending(z => z.Last)
                    .First();
                return (mob.Key, best.Zone, mob.Max(e => e.StartTime));
            });

    private void ApplyRaidTriggerGrouping()
    {
        foreach (RaidTimerRuleViewModel rule in RaidRules)
        {
            SetRaidRuleGroup(rule);
        }
    }

    private readonly HashSet<string> _bossZonesScanned = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _bossZoneScanResult;
    private int _bossZoneScanRunning;

    private void RestoreBossZones(Dictionary<string, string>? saved)
    {
        foreach ((string mob, string zone) in saved ?? new Dictionary<string, string>())
        {
            if (mob.Trim().Length > 0 && zone.Trim().Length > 0 && !_bossZones.ContainsKey(mob.Trim()))
            {
                _bossZones[mob.Trim()] = (zone.Trim(), DateTime.MinValue);
            }
        }
    }

    /// <summary>The zones of the raid triggers' bosses, for the settings file.</summary>
    private Dictionary<string, string> SavedBossZones()
    {
        var bosses = new HashSet<string>(RaidRules.Select(r => r.MobText), StringComparer.OrdinalIgnoreCase);
        return _bossZones.Where(z => bosses.Contains(z.Key)).ToDictionary(z => z.Key, z => z.Value.Zone, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Once per boss, in the background: a raid trigger whose boss has no known zone (its fights are older than the loaded encounters)
    /// gets it from the watched logs. The result is applied on a later tick.
    /// </summary>
    private void TickBossZones()
    {
        if (Interlocked.Exchange(ref _bossZoneScanResult, null) is { } found)
        {
            foreach ((string mob, string zone) in found)
            {
                if (!_bossZones.ContainsKey(mob))
                {
                    _bossZones[mob] = (zone, DateTime.MinValue);
                }
            }

            ApplyRaidTriggerGrouping();
            RequestSave();
        }

        if (_raidRulesView is null || Logs.Count == 0 || Volatile.Read(ref _bossZoneScanRunning) != 0)
        {
            return;
        }

        List<string> unknown = RaidRules.Select(r => r.MobText)
            .Where(m => m != "Any boss" && BossZone(m).Length == 0 && !_bossZonesScanned.Contains(m))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count == 0 || Interlocked.CompareExchange(ref _bossZoneScanRunning, 1, 0) != 0)
        {
            return;
        }

        _bossZonesScanned.UnionWith(unknown);
        List<string> paths = Logs.Select(l => l.FilePath).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        _ = Task.Run(() =>
        {
            try
            {
                Interlocked.Exchange(ref _bossZoneScanResult, BossZoneScanner.Scan(paths, unknown));
            }
            catch (Exception)
            {
                // the bosses stay under Unknown zone
            }
            finally
            {
                Interlocked.Exchange(ref _bossZoneScanRunning, 0);
            }
        });
    }

    /// <summary>The zone a boss was fought in, or the zone a consider line saw it in; empty when neither is known.</summary>
    internal string BossZone(string mob)
    {
        if (_bossZones.TryGetValue(mob.Trim(), out var fought))
        {
            return fought.Zone;
        }

        return _mobLevels.All().Where(l => l.Mob.Equals(mob.Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(l.Zone))
            .Select(l => l.Zone!).FirstOrDefault() ?? string.Empty;
    }

    private void SetRaidRuleGroup(RaidTimerRuleViewModel rule)
    {
        string zone = rule.MobText == "Any boss" ? string.Empty : BossZone(rule.MobText);
        if (zone.Length == 0)
        {
            rule.GroupTop = UnknownZoneGroup;
            rule.GroupSub = string.Empty;
            rule.GroupOrder = 2000;
            return;
        }

        int? expansion = EqFolderPath.Length > 0 ? LevelingRanking.ExpansionOf(LevelingZones(), zone) : null;
        rule.GroupTop = expansion is int e ? ZoneDirectory.ExpansionName(e) : "Other zones";
        rule.GroupOrder = expansion is int order ? (order == 0 ? 999 : order) : 1000;
        rule.GroupSub = zone;
    }

    [RelayCommand]
    private void EnableRaidTriggerGroup(CollectionViewGroup? group) => SetRaidGroupEnabled(group, true);

    [RelayCommand]
    private void DisableRaidTriggerGroup(CollectionViewGroup? group) => SetRaidGroupEnabled(group, false);

    private void SetRaidGroupEnabled(CollectionViewGroup? group, bool on)
    {
        if (group is null)
        {
            return;
        }

        foreach (RaidTimerRuleViewModel rule in RaidLeaves(group))
        {
            rule.IsEnabled = on;
        }

        StatusMessage = $"{(on ? "Turned on" : "Turned off")} every raid trigger in {group.Name}.";
    }

    private static IEnumerable<RaidTimerRuleViewModel> RaidLeaves(CollectionViewGroup group)
    {
        foreach (object item in group.Items)
        {
            if (item is RaidTimerRuleViewModel rule)
            {
                yield return rule;
            }
            else if (item is CollectionViewGroup inner)
            {
                foreach (RaidTimerRuleViewModel nested in RaidLeaves(inner))
                {
                    yield return nested;
                }
            }
        }
    }
}
