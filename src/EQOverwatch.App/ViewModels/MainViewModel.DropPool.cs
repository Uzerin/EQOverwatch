using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Drops;
using EQOverwatch.Core.Tlp;

namespace EQOverwatch.App.ViewModels;

// Drop Table pooling: a server whose loot works like any normal server's (no loot bonus, no Resource Hunter loot zones, no random,
// personal or doubled loot) adds the shared drops of every other such server.
public partial class MainViewModel
{
    private static readonly TimeSpan MergedServersEvery = TimeSpan.FromHours(6);

    private IReadOnlyList<MergedServer> _mergedServers = Array.Empty<MergedServer>();
    private DateTime _nextMergedServersUtc = DateTime.MinValue;

    /// <summary>Add the other normal-loot servers' shared drops to the Drop Table.</summary>
    [ObservableProperty]
    private bool _poolNormalLootDrops = true;

    /// <summary>"Pooled with Tormax, Yelinak and Aradune · 14 players", or why the table is this server's alone.</summary>
    [ObservableProperty]
    private string _dropPoolText = string.Empty;

    partial void OnPoolNormalLootDropsChanged(bool value)
    {
        if (_restoring)
        {
            return;
        }

        RequestSave();
        if (value)
        {
            SharedDropsTask = SyncSharedDropsAsync(force: true);
        }

        RebuildDrops(tableToo: true);
    }

    /// <summary>True when drops on <paramref name="server"/> are normal: TLPs by their rules, every other server (Live) always.</summary>
    public static bool HasNormalLoot(string server) => TlpServers.Find(server)?.PlainLoot ?? true;

    /// <summary>The other servers whose shared drops are pooled with <paramref name="server"/>'s (none when it has special loot).</summary>
    public IReadOnlyList<string> PoolFor(string server)
    {
        if (!UseSharedDrops || !PoolNormalLootDrops || !HasNormalLoot(server))
        {
            return Array.Empty<string>();
        }

        return _mergedServers.Select(m => m.Server)
            .Where(s => !s.Equals(server, StringComparison.OrdinalIgnoreCase) && HasNormalLoot(s))
            .ToList();
    }

    /// <summary>Takes the service's list of servers with a merged table (every few hours, or when forced).</summary>
    private async Task RefreshMergedServersAsync(bool force)
    {
        if (_sharedDrops is null || !PoolNormalLootDrops || (!force && DateTime.UtcNow < _nextMergedServersUtc))
        {
            return;
        }

        _mergedServers = await _sharedDrops.MergedServersAsync();
        _nextMergedServersUtc = DateTime.UtcNow + MergedServersEvery;
    }

    private string PoolText(string? server, IReadOnlyList<string> pool)
    {
        if (server is null || !UseSharedDrops || SelectedDropScope != DropScope.Everyone)
        {
            return string.Empty;
        }

        if (!HasNormalLoot(server))
        {
            return $"{ServerTitle(server)}'s loot rules differ from other servers', so its table is never pooled.";
        }

        var have = pool.Where(s => _pipeline.Drops.MergedInfo(s) is not null).ToList();
        if (!PoolNormalLootDrops || have.Count == 0)
        {
            return PoolNormalLootDrops ? "No other server with normal loot has shared drops yet." : string.Empty;
        }

        int players = have.Sum(s => _pipeline.Drops.MergedInfo(s)!.Players);
        string names = Join(have.Select(ServerTitle).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
        return $"Pooled with {names} · {players.ToString("N0", CultureInfo.CurrentCulture)} more player{(players == 1 ? string.Empty : "s")}.";
    }

    private static string ServerTitle(string server) =>
        TlpServers.Find(server)?.Name ?? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(server.ToLowerInvariant());

    private static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
