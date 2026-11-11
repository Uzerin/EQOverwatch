using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Theming;

namespace EQOverwatch.App.ViewModels;

// Posts each finished boss fight to one Discord channel's webhook, as the same picture "Copy Image for Discord" makes.
public sealed partial class MainViewModel
{
    private readonly DiscordAutoPostSelector _discordSelector = new();
    private readonly SemaphoreSlim _discordPostGate = new(1, 1);
    private IDiscordPoster? _discordPoster;
    private Func<EncounterSnapshot, ThemePalette, string, byte[]>? _renderPng;
    private string _discordLastResult = string.Empty;

    /// <summary>On: every boss fight that ends and passes the filters is posted to the webhook. Off by default.</summary>
    [ObservableProperty]
    private bool _discordPostEnabled;

    /// <summary>The channel's webhook address. Stays on this PC: it is left out of backups and never synced.</summary>
    [ObservableProperty]
    private string _discordWebhookUrl = string.Empty;

    [ObservableProperty]
    private bool _discordPostWipes;

    [ObservableProperty]
    private int _discordMinSeconds = DiscordAutoPostSelector.DefaultMinSeconds;

    [ObservableProperty]
    private int _discordMinPlayers = DiscordAutoPostSelector.DefaultMinPlayers;

    [ObservableProperty]
    private string _discordStatus = "Off.";

    private void ConfigureDiscord(IDiscordPoster? poster, Func<EncounterSnapshot, ThemePalette, string, byte[]>? renderPng)
    {
        _discordPoster = poster;
        _renderPng = renderPng;
        SyncDiscord();
    }

    partial void OnDiscordPostEnabledChanged(bool value) => DiscordSettingChanged();

    partial void OnDiscordWebhookUrlChanged(string value) => DiscordSettingChanged();

    partial void OnDiscordPostWipesChanged(bool value) => DiscordSettingChanged();

    partial void OnDiscordMinSecondsChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, DiscordAutoPostSelector.MaxMinSeconds);
        if (clamped != value)
        {
            DiscordMinSeconds = clamped;
            return;
        }

        DiscordSettingChanged();
    }

    partial void OnDiscordMinPlayersChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, DiscordAutoPostSelector.MaxMinPlayers);
        if (clamped != value)
        {
            DiscordMinPlayers = clamped;
            return;
        }

        DiscordSettingChanged();
    }

    private void DiscordSettingChanged()
    {
        SyncDiscord();
        if (!_restoring)
        {
            RequestSave();
        }
    }

    private void SyncDiscord()
    {
        _discordSelector.PostWipes = DiscordPostWipes;
        _discordSelector.MinSeconds = DiscordMinSeconds;
        _discordSelector.MinPlayers = DiscordMinPlayers;
        _discordSelector.Enabled = DiscordPostEnabled && _discordPoster is not null && _renderPng is not null
                                   && DiscordAutoPostSelector.IsWebhookUrl(DiscordWebhookUrl);
        UpdateDiscordStatus();
    }

    private void UpdateDiscordStatus()
    {
        if (!DiscordPostEnabled)
        {
            DiscordStatus = "Off.";
        }
        else if (!DiscordAutoPostSelector.IsWebhookUrl(DiscordWebhookUrl))
        {
            DiscordStatus = "Paste the channel's webhook address (it starts with https://discord.com/api/webhooks/) to start posting.";
        }
        else
        {
            string what = DiscordPostWipes ? "Posting every fight" : "Posting every kill";
            DiscordStatus = $"{what} of {DiscordMinPlayers} or more players that lasts {DiscordMinSeconds} seconds or more." +
                            (_discordLastResult.Length > 0 ? " " + _discordLastResult : string.Empty);
        }
    }

    /// <summary>Called when the live fights change: posts the ones that just ended and pass the filters.</summary>
    private void PostFinishedFightsToDiscord(DpsSnapshot live)
    {
        foreach (EncounterSnapshot fight in _discordSelector.Select(live.Completed, _clock()))
        {
            QueueDiscordPost(fight.WithClassesOnServer(ClassLabelOnServer));
        }
    }

    private void QueueDiscordPost(EncounterSnapshot fight)
    {
        if (_discordPoster is null || _renderPng is null)
        {
            return;
        }

        byte[] png;
        try
        {
            png = _renderPng(fight, ActivePalette, WindowTitle); // drawn here, on the UI thread
        }
        catch (Exception ex)
        {
            ReportDiscord($"The picture for {fight.TargetName} could not be drawn: {ex.Message}");
            return;
        }

        IDiscordPoster poster = _discordPoster;
        string url = DiscordWebhookUrl.Trim();
        string message = DiscordAutoPostSelector.BuildMessage(fight);
        _ = Task.Run(async () =>
        {
            await _discordPostGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string? error = await poster.PostAsync(url, message, png, "fight.png").ConfigureAwait(false);
                ReportDiscord(error is null ? $"Posted {fight.TargetName}." : $"Posting {fight.TargetName} failed: {error}");
            }
            catch (Exception ex)
            {
                ReportDiscord($"Posting {fight.TargetName} failed: {ex.Message}");
            }
            finally
            {
                _discordPostGate.Release();
            }
        });
    }

    private void ReportDiscord(string result)
    {
        _discordLastResult = result;
        UpdateDiscordStatus();
    }

    /// <summary>Posts a short test message (no picture) so the webhook address can be checked.</summary>
    [RelayCommand]
    private async Task SendDiscordTest()
    {
        if (_discordPoster is null)
        {
            StatusMessage = "Discord posting is not available.";
            return;
        }

        if (!DiscordAutoPostSelector.IsWebhookUrl(DiscordWebhookUrl))
        {
            StatusMessage = "Paste the channel's webhook address first.";
            return;
        }

        string? error = await _discordPoster.PostAsync(DiscordWebhookUrl.Trim(), $"{WindowTitle}: test post. The webhook works.", null, "test.png").ConfigureAwait(true);
        StatusMessage = error is null ? "Sent a test post to Discord." : $"The test post failed: {error}";
        ReportDiscord(error is null ? "Test post sent." : $"Test post failed: {error}");
    }
}
