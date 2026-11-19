using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Chat;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

/// <summary>A line on the Chat tab.</summary>
public sealed record ChatRowView(string When, string Seen, string Channel, string Speaker, string Text, bool IsMention, bool IsYou, bool IsSearchHit = false);

// The Chat tab: every kind of player chat, one sub tab each (Tells keeps its own view), name mentions highlighted, one player's chat on double-click.
public sealed partial class MainViewModel
{
    public const int ChatAllTab = 0;
    public const int ChatTellsTab = 1;
    private const int ChatShownMax = 3000;

    public IReadOnlyList<string> ChatTabs { get; } = new[] { "All", "Tells", "Say", "Group", "Raid", "Guild", "OOC", "Shout", "Auction", "Channels" };

    private static readonly ChatChannel?[] ChatTabChannels =
    {
        null, ChatChannel.Tell, ChatChannel.Say, ChatChannel.Group, ChatChannel.Raid, ChatChannel.Guild, ChatChannel.Ooc, ChatChannel.Shout, ChatChannel.Auction, ChatChannel.Channel,
    };

    [ObservableProperty]
    private int _selectedChatTab;

    /// <summary>Only this player's lines (set by double-clicking a line), or empty for everyone.</summary>
    [ObservableProperty]
    private string _chatPlayer = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ChatRowView> _chatRows = Array.Empty<ChatRowView>();

    [ObservableProperty]
    private string _chatStatus = "Reading the last day of chat…";

    public bool HasChatPlayer => ChatPlayer.Length > 0;

    private long _chatVersionShown = -1;
    private bool _chatScanStarted;

    partial void OnSelectedChatTabChanged(int value) => RebuildChat();

    /// <summary>Text to find in every channel's messages and speakers; empty shows everything.</summary>
    [ObservableProperty]
    private string _chatSearch = string.Empty;

    [ObservableProperty]
    private string _chatSearchStatus = string.Empty;

    partial void OnChatSearchChanged(string value) => RebuildChat();

    partial void OnChatPlayerChanged(string value)
    {
        OnPropertyChanged(nameof(HasChatPlayer));
        RebuildChat();
    }

    [RelayCommand]
    private void FilterChatPlayer(ChatRowView? row)
    {
        if (row is not null && row.Speaker.Length > 0)
        {
            ChatPlayer = row.Speaker;
        }
    }

    [RelayCommand]
    private void ClearChatPlayer() => ChatPlayer = string.Empty;

    /// <summary>Called on the UI tick: reads the last day of chat once, then shows new lines.</summary>
    private void RefreshChat()
    {
        if (!_chatScanStarted && Logs.Count > 0)
        {
            _chatScanStarted = true;
            var logs = Logs.Select(l => (l.FilePath, l.CharacterName)).ToList();
            DateTime since = _clock().AddDays(-1);
            ChatLog chat = _pipeline.Chat;
            _ = Task.Run(() =>
            {
                foreach ((string path, string character) in logs)
                {
                    chat.ScanRecent(path, character, since);
                }
            });
        }

        if (_pipeline.Chat.Version != _chatVersionShown)
        {
            RebuildChat();
        }
    }

    internal void RebuildChat()
    {
        _chatVersionShown = _pipeline.Chat.Version;
        ChatChannel? channel = SelectedChatTab >= 0 && SelectedChatTab < ChatTabChannels.Length ? ChatTabChannels[SelectedChatTab] : null;
        string player = ChatPlayer;
        string[] names = Logs.Select(l => l.CharacterName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Regex? mention = names.Length == 0
            ? null
            : new Regex(@"\b(?:" + string.Join("|", names.Select(Regex.Escape)) + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        CultureInfo culture = CultureInfo.CurrentCulture;
        IReadOnlyList<ChatLine> all = _pipeline.Chat.All();
        var rows = new List<ChatRowView>();
        string search = ChatSearch?.Trim() ?? string.Empty;
        if (search.Length > 0)
        {
            int hits = all.Count(l => l.Text.Contains(search, StringComparison.OrdinalIgnoreCase) || l.Speaker.Contains(search, StringComparison.OrdinalIgnoreCase));
            ChatSearchStatus = hits == 0 ? "No chat line contains that." : $"{hits:N0} line{(hits == 1 ? string.Empty : "s")} in all channels; this tab shows its own.";
        }
        else
        {
            ChatSearchStatus = string.Empty;
        }

        for (int i = all.Count - 1; i >= 0 && rows.Count < ChatShownMax; i--)
        {
            ChatLine line = all[i];
            if ((channel is { } c && line.Channel != c) || (player.Length > 0 && !line.Speaker.Equals(player, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            bool searchHit = search.Length > 0
                             && (line.Text.Contains(search, StringComparison.OrdinalIgnoreCase) || line.Speaker.Contains(search, StringComparison.OrdinalIgnoreCase));
            if (search.Length > 0 && !searchHit)
            {
                continue;
            }

            rows.Add(new ChatRowView(
                line.Utc.ToLocalTime().ToString("MMM d HH:mm:ss", culture),
                line.Character,
                ChannelLabel(line),
                line.Speaker,
                line.Text,
                !line.IsYou && mention is not null && mention.IsMatch(line.Text),
                line.IsYou,
                searchHit));
        }

        ChatRows = rows;
        ChatStatus = player.Length > 0
            ? $"Showing only {player}. Double-click another line to switch players."
            : "Newest first: the last day of chat, then everything new. Double-click a line to see only that player; lines naming one of your characters are highlighted.";
    }

    private static string ChannelLabel(ChatLine line) => line.Channel switch
    {
        ChatChannel.Tell => "Tell " + line.ChannelName,
        ChatChannel.Ooc => "OOC",
        ChatChannel.Channel => line.ChannelName,
        _ => line.Channel.ToString(),
    };
}
