using CommunityToolkit.Mvvm.ComponentModel;
using EQCompanion.Core.Models;
using EQCompanion.Core.Settings;
using EQCompanion.Core.Sharing;

namespace EQCompanion.App.ViewModels;

/// <summary>An entry of the "CH Chain Position" drop-down on the Characters tab.</summary>
public sealed record ChainPositionOption(int Value, string Display)
{
    /// <summary>None, Pos 1 ... Pos 12.</summary>
    public static IReadOnlyList<ChainPositionOption> All { get; } =
        new[] { new ChainPositionOption(0, "None") }
            .Concat(Enumerable.Range(1, WatchedLogConfig.MaxChChainPosition).Select(n => new ChainPositionOption(n, $"Pos {n}")))
            .ToArray();
}

/// <summary>An entry of the "Historical Days to Load" drop-down.</summary>
public sealed record HistoricalDaysOption(int Days, string Display)
{
    /// <summary>1 Day, 3 Days, 7 Days, 14 Days, 30 Days, 90 Days, All History (0).</summary>
    public static IReadOnlyList<HistoricalDaysOption> All { get; } = AppSettings.HistoricalDaysChoices
        .Select(days => new HistoricalDaysOption(days, AppSettings.DescribeHistoricalDays(days)))
        .ToArray();
}

/// <summary>A trigger or raid timer someone shared in chat, waiting for the user to accept or ignore it.</summary>
public sealed class PendingSharedTriggerViewModel
{
    public PendingSharedTriggerViewModel(SharedTriggerOffer offer)
    {
        Offer = offer ?? throw new ArgumentNullException(nameof(offer));
    }

    public SharedTriggerOffer Offer { get; }

    public SharedItem Item => Offer.Item;

    public string Sender => Offer.Sender;

    /// <summary>"Ream shared trigger "Protective Skin Faded"".</summary>
    public string Title => $"{Offer.Sender} shared {Item.Description}";

    /// <summary>What the trigger looks for: its pattern, or a raid timer's log text.</summary>
    public string Detail => Item.Kind == SharedItemKind.Trigger
        ? $"Matches: {Item.Trigger?.Pattern}"
        : $"Matches: {Item.RaidBossTimer?.MatchText}";
}
