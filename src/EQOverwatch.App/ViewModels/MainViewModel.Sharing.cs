using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Export;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Sharing;

namespace EQOverwatch.App.ViewModels;

// Trigger sharing ({EQO:...} tokens) and the chat / Discord exports of an encounter.
public sealed partial class MainViewModel
{
    /// <summary>The most offers waiting for an answer; further ones are dropped so a chat spammer cannot flood the bar.</summary>
    public const int MaxPendingSharedTriggers = 20;

    /// <summary>An offer the user answered (or one already waiting) is not shown again for this long.</summary>
    public static readonly TimeSpan HandledOfferMemory = TimeSpan.FromMinutes(5);

    private readonly ConcurrentQueue<SharedTriggerOffer> _offers = new();
    private readonly Dictionary<string, DateTime> _handledPayloads = new(StringComparer.Ordinal);

    // ─────────────────────────── receiving ───────────────────────────

    /// <summary>Shared triggers waiting for [ Accept ] or [ Ignore ]. Nothing in here is active.</summary>
    public ObservableCollection<PendingSharedTriggerViewModel> PendingSharedTriggers { get; } = new();

    public PendingSharedTriggerViewModel? CurrentSharedOffer => PendingSharedTriggers.FirstOrDefault();

    public bool HasPendingSharedTriggers => PendingSharedTriggers.Count > 0;

    /// <summary>"2 more waiting" or an empty string.</summary>
    public string PendingSharedMore => PendingSharedTriggers.Count > 1 ? $"+{PendingSharedTriggers.Count - 1} more waiting" : string.Empty;

    private void OnPendingSharedChanged()
    {
        OnPropertyChanged(nameof(CurrentSharedOffer));
        OnPropertyChanged(nameof(HasPendingSharedTriggers));
        OnPropertyChanged(nameof(PendingSharedMore));
    }

    /// <summary>Called on a pipeline thread: only queues; <see cref="Refresh"/> moves it to the UI collection.</summary>
    private void OnSharedOffered(SharedTriggerOffer offer) => _offers.Enqueue(offer);

    private void DrainSharedOffers()
    {
        while (_offers.TryDequeue(out SharedTriggerOffer? offer))
        {
            if (PendingSharedTriggers.Count >= MaxPendingSharedTriggers || WasHandledRecently(offer.Payload))
            {
                continue;
            }

            PendingSharedTriggers.Add(new PendingSharedTriggerViewModel(offer));
            RememberHandled(offer.Payload); // also stops the same token, heard again, from stacking up while this one waits
            StatusMessage = $"{offer.Sender} shared {offer.Item.Description}. Accept or ignore it at the top of the window.";
        }
    }

    private bool WasHandledRecently(string payload) =>
        _handledPayloads.TryGetValue(payload, out DateTime at) && _clock() - at < HandledOfferMemory;

    private void RememberHandled(string payload)
    {
        DateTime now = _clock();
        if (_handledPayloads.Count > 256)
        {
            foreach (string stale in _handledPayloads.Where(p => now - p.Value >= HandledOfferMemory).Select(p => p.Key).ToList())
            {
                _handledPayloads.Remove(stale);
            }
        }

        _handledPayloads[payload] = now;
    }

    /// <summary>The user consents: the shared trigger or raid timer is added to their own list (and starts working).</summary>
    [RelayCommand]
    private void AcceptShared(PendingSharedTriggerViewModel? pending)
    {
        if (pending is null || !PendingSharedTriggers.Remove(pending))
        {
            return;
        }

        SharedItem item = pending.Item;
        if (item.Kind == SharedItemKind.Trigger && item.Trigger is { } trigger)
        {
            TriggerRuleViewModel row = CreateRule(trigger);
            Rules.Add(row);
            SelectedRule = row;
            OnRulesChanged();
        }
        else if (item.RaidBossTimer is { } raid)
        {
            RaidRules.Add(CreateRaidRule(raid));
            OnRaidRulesChanged();
        }

        StatusMessage = $"Added {item.Description} from {pending.Sender}.";
    }

    [RelayCommand]
    private void IgnoreShared(PendingSharedTriggerViewModel? pending)
    {
        if (pending is not null && PendingSharedTriggers.Remove(pending))
        {
            StatusMessage = $"Ignored {pending.Item.Description} from {pending.Sender}.";
        }
    }

    // ─────────────────────────── sharing ───────────────────────────

    /// <summary>Copies <c>{EQO:...}</c> for the trigger selected on the Custom Triggers tab.</summary>
    [RelayCommand]
    private void ShareSelectedRule()
    {
        if (SelectedRule is null)
        {
            StatusMessage = "Select a trigger to share first.";
            return;
        }

        CopyShareToken(TriggerShareSerializer.Export(SelectedRule.ToRule()), SelectedRule.Name);
    }

    /// <summary>Copies <c>{EQO:...}</c> for a saved raid boss timer.</summary>
    [RelayCommand]
    private void ShareRaidRule(RaidTimerRuleViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        RaidBossTimerRule rule = row.ToRule();
        CopyShareToken(TriggerShareSerializer.Export(rule), rule.Label);
    }

    /// <summary>Copies <c>{EQO:...}</c> for the raid timer being built (it does not have to be added to the list first).</summary>
    [RelayCommand]
    private void ShareRaidBuilder()
    {
        if (TryBuildRaidRule(out RaidBossTimerRule rule))
        {
            CopyShareToken(TriggerShareSerializer.Export(rule), rule.Label);
        }
    }

    private void CopyShareToken(string token, string name)
    {
        try
        {
            _clipboard.SetText(token);
            StatusMessage = TriggerShareSerializer.FitsInChat(token)
                ? $"Copied {{EQO}} for \"{name}\". Paste it into raid chat."
                : $"Copied {{EQO}} for \"{name}\", but at {token.Length} characters it is long for EverQuest chat and may arrive cut off.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The clipboard could not be used: {ex.Message}";
        }
    }

    // ─────────────────────────── exports ───────────────────────────

    /// <summary>Copies the selected encounter's summary and top 12 attackers as chat text.</summary>
    [RelayCommand]
    private void CopyTop12ForChat() => CopyChat(SelectedEncounter?.Snapshot);

    /// <summary>Copies the selected encounter as a dark-themed picture with the product footer.</summary>
    [RelayCommand]
    private void CopyImageForDiscord() => CopyImage(SelectedEncounter?.Snapshot);

    /// <summary>Copies <paramref name="encounter"/> (also used by the DPS overlay's header buttons) for EverQuest chat.</summary>
    public void CopyChat(EncounterSnapshot? encounter)
    {
        if (encounter is null)
        {
            StatusMessage = "Select an encounter first.";
            return;
        }

        encounter = encounter.WithClasses(_pipeline.Roster.GetClassLabel);
        try
        {
            _clipboard.SetText(EncounterExport.BuildChatText(encounter, EncounterExport.DefaultTopCount));
            StatusMessage = $"Copied the top {Math.Min(EncounterExport.DefaultTopCount, encounter.Participants.Count)} for {encounter.TargetName} as chat text.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The clipboard could not be used: {ex.Message}";
        }
    }

    /// <summary>Copies <paramref name="encounter"/> as a picture for Discord, in the active theme, footed with the app name and version.</summary>
    public void CopyImage(EncounterSnapshot? encounter)
    {
        if (encounter is null)
        {
            StatusMessage = "Select an encounter first.";
            return;
        }

        encounter = encounter.WithClasses(_pipeline.Roster.GetClassLabel);
        try
        {
            _clipboard.SetEncounterImage(encounter, ActivePalette, WindowTitle, EncounterExport.AllAttackers);
            StatusMessage = $"Copied a picture of {encounter.TargetName} with all {encounter.Participants.Count} attacker(s). Paste it into Discord.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"The picture could not be copied: {ex.Message}";
        }
    }
}
