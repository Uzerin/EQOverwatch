using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Loot;

namespace EQOverwatch.App.ViewModels;

// Tab badges: a small count on the Logs tab (new tells) and the Loot tab (new loot) while you are on another tab.
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogsBadge))]
    private int _logsBadge;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLootBadge))]
    private int _lootBadge;

    public bool HasLogsBadge => LogsBadge > 0;

    public bool HasLootBadge => LootBadge > 0;

    private DateTime _lootBadgeSince = DateTime.Now;
    private volatile bool _lootChangedForBadge;
    private bool _lootBadgeHooked;

    /// <summary>True for the name of a character whose log this app watches (its rows are tinted in every table).</summary>
    public bool IsOwnCharacter(string name) => _pipeline.IsMonitoredCharacter(name);

    /// <summary>New live tells arrived; counted on the Logs tab unless it is open.</summary>
    private void CountTellsForBadge(int count)
    {
        if (count > 0 && SelectedMainTab != LogsTab)
        {
            LogsBadge += count;
        }
    }

    /// <summary>Counts loot looted since the last look at the Loot tab (only loot newer than the app's start, not old logs read in).</summary>
    private void TickBadges()
    {
        if (!_lootBadgeHooked)
        {
            _lootBadgeHooked = true;
            _pipeline.Loot.Changed += () => _lootChangedForBadge = true;
        }

        if (!_lootChangedForBadge)
        {
            return;
        }

        _lootChangedForBadge = false;
        DateTime since = _lootBadgeSince;
        IReadOnlyList<LootEntry> entries = _pipeline.Loot.Entries;
        int fresh = 0;
        DateTime newest = since;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Time > since)
            {
                fresh++;
                if (entries[i].Time > newest)
                {
                    newest = entries[i].Time;
                }
            }
        }

        _lootBadgeSince = newest;
        if (fresh > 0 && SelectedMainTab != LootTab)
        {
            LootBadge += fresh;
        }
    }

    /// <summary>Opening a badged tab clears its count.</summary>
    private void ClearBadgeFor(int tab)
    {
        if (tab == LogsTab)
        {
            LogsBadge = 0;
        }
        else if (tab == LootTab)
        {
            LootBadge = 0;
        }
    }
}
