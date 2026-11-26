using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>Remembers where each boss was fought (the newest fight wins) and regroups the raid triggers when that changed.</summary>
    private void LearnBossZones(IEnumerable<EncounterSnapshot> encounters)
    {
        bool changed = false;
        foreach (EncounterSnapshot encounter in encounters)
        {
            if (string.IsNullOrWhiteSpace(encounter.Zone))
            {
                continue;
            }

            string mob = encounter.TargetName.Trim();
            if (_bossZones.TryGetValue(mob, out var known) && known.Start >= encounter.StartTime)
            {
                continue;
            }

            string zone = encounter.Zone.Trim();
            _bossZones[mob] = (zone, encounter.StartTime);
            changed = changed || (known.Zone != zone && RaidRules.Any(r => r.MobText.Equals(mob, StringComparison.OrdinalIgnoreCase)));
        }

        if (changed)
        {
            ApplyRaidTriggerGrouping();
        }
    }

    private void ApplyRaidTriggerGrouping()
    {
        foreach (RaidTimerRuleViewModel rule in RaidRules)
        {
            SetRaidRuleGroup(rule);
        }
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
