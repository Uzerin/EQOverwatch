using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Drops;
using EQOverwatch.Core.Tlp;

namespace EQOverwatch.App.ViewModels;

// "6 players today · 3 on vaniki" at the top of the app and players per server on the TLP tab, from the anonymous player count
// (read once an hour; each copy of the app counts itself once a day, with the servers its characters played on lately).
public sealed partial class MainViewModel
{
    private static readonly TimeSpan PlayerStatsEvery = TimeSpan.FromHours(1);
    private DateTime _nextPlayerStatsUtc;
    private bool _playerStatsBusy;
    private PlayerStats? _playerStats;

    /// <summary>"6 players today · 3 on vaniki"; empty until the count has been read.</summary>
    [ObservableProperty]
    private string _playersText = string.Empty;

    private void RefreshPlayerStats(DateTime nowUtc)
    {
        if (_sharedDrops is null || _playerStatsBusy || nowUtc < _nextPlayerStatsUtc)
        {
            return;
        }

        _nextPlayerStatsUtc = nowUtc + PlayerStatsEvery;
        _ = ReadPlayerStatsAsync();
    }

    private async Task ReadPlayerStatsAsync()
    {
        _playerStatsBusy = true;
        try
        {
            if (await _sharedDrops!.StatsAsync() is { } stats)
            {
                _playerStats = stats;
                ShowPlayerStats();
                _tlpBuiltFor = default; // the TLP tabs show the per-server counts on their next refresh
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // read again in an hour
        }
        finally
        {
            _playerStatsBusy = false;
        }
    }

    private void ShowPlayerStats()
    {
        if (_playerStats is not { } stats)
        {
            return;
        }

        string text = $"{stats.ActiveToday:N0} player{(stats.ActiveToday == 1 ? string.Empty : "s")} today";
        string server = LastPlayedLog()?.ServerName ?? string.Empty;
        if (server.Length > 0 && stats.ServersToday.TryGetValue(server, out int here) && here > 0)
        {
            text += $" · {here:N0} on {server.ToLowerInvariant()}";
        }

        PlayersText = text;
    }

    /// <summary>"3 EQ Overwatch players this week" for a TLP server's tab.</summary>
    private string PlayersOnServerText(TlpServer server)
    {
        if (_playerStats is not { } stats)
        {
            return string.Empty;
        }

        int count = stats.Servers7Days.Where(p => TlpServers.Find(p.Key)?.Key == server.Key).Sum(p => p.Value);
        return count == 0 ? string.Empty : $"{count:N0} EQ Overwatch player{(count == 1 ? string.Empty : "s")} this week";
    }

    /// <summary>The servers of the logs written in the last day, sent with the daily player count (names only).</summary>
    private IReadOnlyList<string> RecentServers()
    {
        DateTime since = DateTime.UtcNow.AddDays(-1);
        return Logs
            .Where(l => l.ServerName.Length > 0 && SafeLastWrite(l.FilePath) >= since)
            .Select(l => l.ServerName.ToLowerInvariant())
            .Distinct()
            .Take(10)
            .ToList();
    }

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTime.MinValue;
        }
    }
}
