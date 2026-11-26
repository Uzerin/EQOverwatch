using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Leveling;
using EQOverwatch.Core.Maps;

namespace EQOverwatch.App.ViewModels;

// The Custom triggers list in groups: automatically by expansion and then zone (from each trigger's Zone), or by your own folders.
// A whole group can be turned on or off at once.
public sealed partial class MainViewModel
{
    public const string AnyZoneGroup = "Any zone";
    public const string DefaultFolder = "General";

    private ListCollectionView? _rulesView;

    /// <summary>True: your own folders. False (the default): expansion, then zone.</summary>
    [ObservableProperty]
    private bool _groupTriggersByFolder;

    partial void OnGroupTriggersByFolderChanged(bool value)
    {
        ApplyTriggerGrouping();
        RequestSave();
    }

    /// <summary>The Custom triggers, grouped and sorted for the list.</summary>
    public ICollectionView RulesView
    {
        get
        {
            if (_rulesView is null)
            {
                _rulesView = new ListCollectionView(Rules) { IsLiveGrouping = true, IsLiveSorting = true };
                _rulesView.LiveGroupingProperties.Add(nameof(TriggerRuleViewModel.GroupTop));
                _rulesView.LiveGroupingProperties.Add(nameof(TriggerRuleViewModel.GroupSub));
                _rulesView.LiveSortingProperties.Add(nameof(TriggerRuleViewModel.GroupOrder));
                _rulesView.LiveSortingProperties.Add(nameof(TriggerRuleViewModel.GroupTop));
                _rulesView.LiveSortingProperties.Add(nameof(TriggerRuleViewModel.GroupSub));
                ApplyViewGrouping();
            }

            return _rulesView;
        }
    }

    /// <summary>Sorts every trigger into its group again (after the grouping changed or the zone list loaded).</summary>
    private void ApplyTriggerGrouping()
    {
        foreach (TriggerRuleViewModel rule in Rules)
        {
            SetRuleGroup(rule);
        }

        ApplyViewGrouping();
        if (_raidRulesView is not null)
        {
            ApplyRaidTriggerGrouping(); // the zone list may have just loaded
        }
    }

    private void ApplyViewGrouping()
    {
        if (_rulesView is null)
        {
            return;
        }

        using (_rulesView.DeferRefresh())
        {
            _rulesView.GroupDescriptions.Clear();
            _rulesView.SortDescriptions.Clear();
            _rulesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TriggerRuleViewModel.GroupTop)));
            _rulesView.SortDescriptions.Add(new SortDescription(nameof(TriggerRuleViewModel.GroupOrder), ListSortDirection.Ascending));
            _rulesView.SortDescriptions.Add(new SortDescription(nameof(TriggerRuleViewModel.GroupTop), ListSortDirection.Ascending));
            if (!GroupTriggersByFolder)
            {
                _rulesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TriggerRuleViewModel.GroupSub)));
                _rulesView.SortDescriptions.Add(new SortDescription(nameof(TriggerRuleViewModel.GroupSub), ListSortDirection.Ascending));
            }

            _rulesView.SortDescriptions.Add(new SortDescription(nameof(TriggerRuleViewModel.Name), ListSortDirection.Ascending));
        }
    }

    /// <summary>Works out a trigger's group: its folder, or its zone's expansion and the zone.</summary>
    private void SetRuleGroup(TriggerRuleViewModel rule)
    {
        if (GroupTriggersByFolder)
        {
            string folder = (rule.Folder ?? string.Empty).Trim();
            rule.GroupTop = folder.Length > 0 ? folder : DefaultFolder;
            rule.GroupSub = string.Empty;
            rule.GroupOrder = 0;
            return;
        }

        string zone = (rule.Zone ?? string.Empty).Trim();
        if (zone.Length == 0)
        {
            rule.GroupTop = AnyZoneGroup;
            rule.GroupSub = string.Empty;
            rule.GroupOrder = -1;
            return;
        }

        int? expansion = EqFolderPath.Length > 0 ? LevelingRanking.ExpansionOf(LevelingZones(), zone) : null;
        rule.GroupTop = expansion is int e ? ZoneDirectory.ExpansionName(e) : "Other zones";
        rule.GroupOrder = expansion is int order ? (order == 0 ? 999 : order) : 1000;
        rule.GroupSub = zone;
    }

    /// <summary>Turns every trigger in a group (and the groups inside it) on or off.</summary>
    [RelayCommand]
    private void EnableTriggerGroup(CollectionViewGroup? group) => SetGroupEnabled(group, true);

    [RelayCommand]
    private void DisableTriggerGroup(CollectionViewGroup? group) => SetGroupEnabled(group, false);

    private void SetGroupEnabled(CollectionViewGroup? group, bool on)
    {
        if (group is null)
        {
            return;
        }

        foreach (TriggerRuleViewModel rule in Leaves(group))
        {
            rule.IsEnabled = on;
        }

        StatusMessage = $"{(on ? "Turned on" : "Turned off")} every trigger in {group.Name}.";
    }

    private static IEnumerable<TriggerRuleViewModel> Leaves(CollectionViewGroup group)
    {
        foreach (object item in group.Items)
        {
            if (item is TriggerRuleViewModel rule)
            {
                yield return rule;
            }
            else if (item is CollectionViewGroup inner)
            {
                foreach (TriggerRuleViewModel nested in Leaves(inner))
                {
                    yield return nested;
                }
            }
        }
    }
}
