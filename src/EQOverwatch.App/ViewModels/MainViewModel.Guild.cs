using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Attendance;

namespace EQOverwatch.App.ViewModels;

/// <summary>One guild member on the Guild tab.</summary>
public sealed record GuildRowView(string Name, int Level, string Class, string Rank, string Alt, string LastSeen, DateTime LastSeenSort);

// The Guild tab (under Raid): the guild roster of the server you're viewing, from the newest /outputfile guild dump.
public sealed partial class MainViewModel
{
    public const int GuildRaidTab = 3;

    [ObservableProperty]
    private string _guildTitle = "Guild";

    [ObservableProperty]
    private string _guildTabStatus = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<GuildRowView> _guildRows = Array.Empty<GuildRowView>();

    private void RebuildGuildTab()
    {
        string server = ToolsServerFilter ?? LastPlayedLog()?.ServerName ?? string.Empty;
        if (server.Length == 0)
        {
            GuildTitle = "Guild";
            GuildRows = Array.Empty<GuildRowView>();
            GuildTabStatus = "Pick a server in the Server box to see its guild.";
            return;
        }

        _guilds.Remove(server); // read the newest dump each time the tab opens
        if (GuildFor(server) is not { } guild)
        {
            GuildTitle = "Guild";
            GuildRows = Array.Empty<GuildRowView>();
            GuildTabStatus = $"No guild roster for {server} yet: type /outputfile guild in game.";
            return;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        DateTime now = _clock();
        GuildRows = guild.Members
            .OrderByDescending(m => m.LastOn ?? DateTime.MinValue).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new GuildRowView(m.Name, m.Level, m.Class, m.Rank, m.IsAlt ? "Alt" : string.Empty,
                m.LastOn is { } on ? (on.Date == now.Date ? "Today" : on.ToString("MMM d, yyyy", culture)) : string.Empty,
                m.LastOn ?? DateTime.MinValue))
            .ToList();
        int week = guild.Members.Count(m => m.LastOn is { } on && (now - on).TotalDays <= 7);
        GuildTitle = $"{guild.Guild} · {server}";
        GuildTabStatus = $"{guild.Members.Count:N0} members, {week:N0} on in the last 7 days. Roster from {guild.Taken.ToString("MMM d HH:mm", culture)}; type /outputfile guild for a newer one.";
    }
}
