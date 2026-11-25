using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>One result of Ctrl+F: what was found, where, and what opening it does.</summary>
public sealed record SearchHit(string Kind, string Text, string Detail, Action Open);

// Ctrl+F: one box that finds characters, items, mobs (fights) and triggers across the app and jumps to the match.
public sealed partial class MainViewModel
{
    public const int MaxSearchHitsPerKind = 12;
    private const int EncounterTab = 2;
    private const int ItemsAndScrollsTab = 2;

    /// <summary>Everything matching <paramref name="text"/> (part of a name, any case), grouped by kind.</summary>
    public IReadOnlyList<SearchHit> SearchEverything(string? text)
    {
        string query = text?.Trim() ?? string.Empty;
        var hits = new List<SearchHit>();
        if (query.Length < 2)
        {
            return hits;
        }

        bool Has(string value) => value.Contains(query, StringComparison.OrdinalIgnoreCase);

        foreach (WatchedLogConfig log in Logs.Where(l => Has(l.CharacterName)).OrderBy(l => l.CharacterName, StringComparer.OrdinalIgnoreCase).Take(MaxSearchHitsPerKind))
        {
            WatchedLogConfig found = log;
            hits.Add(new SearchHit("Characters", log.CharacterName, $"{log.ServerName}{(log.ClassName.Length > 0 ? " · " + log.ClassName : string.Empty)}",
                () => OpenProfileCommand.Execute(found)));
        }

        foreach (ItemHit item in CharacterPlanner.FindItems(_characterData.All(), _characterData.SharedBanks(), query, null)
                     .GroupBy(i => i.Item, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(MaxSearchHitsPerKind))
        {
            string name = item.Item;
            hits.Add(new SearchHit("Items", item.Item, $"{item.Character} · {item.Where}", () =>
            {
                SelectedMainTab = LootTab;
                SelectedLootTab = ItemsAndScrollsTab;
                ItemSearchText = name;
            }));
        }

        IEnumerable<EncounterSnapshot> fights = _pipeline.LiveDps.Snapshot.All.Concat(_pipeline.HistoricalDps.Snapshot.All);
        foreach (IGrouping<string, EncounterSnapshot> mob in fights.Where(f => Has(f.TargetName))
                     .GroupBy(f => f.TargetName, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(MaxSearchHitsPerKind))
        {
            string name = mob.Key;
            int count = mob.Count();
            hits.Add(new SearchHit("Mobs and fights", name, $"{count} fight{(count == 1 ? string.Empty : "s")}, last {mob.Max(f => f.StartTime):yyyy-MM-dd}", () =>
            {
                SelectedMainTab = EncounterTab;
                EncounterNameFilter = name;
            }));
        }

        foreach (TriggerRuleViewModel rule in Rules.Where(r => Has(r.Name) || Has(r.Pattern)).Take(MaxSearchHitsPerKind))
        {
            TriggerRuleViewModel found = rule;
            hits.Add(new SearchHit("Triggers", rule.Name, rule.Pattern, () =>
            {
                SelectedMainTab = TriggersTab;
                SelectedTriggersTab = 0;
                SelectedRule = found;
            }));
        }

        return hits;
    }
}
