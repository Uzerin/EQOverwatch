using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Discord;
using EQOverwatch.Core.Drops;
using EQOverwatch.Core.Models;

namespace EQOverwatch.App.ViewModels;

// Raid bosses and the Discord webhooks.
//  - Raid Boss: a tick per mob in the encounter list. The server's kill announcement ("... has been defeated by a group of hardy
//    adventurers!") ticks it, unless the player unticked that mob.
//  - Webhooks: raid boss kills (the Discord picture), loot lists linked to raid chat, and unlooted items (with @here). The owner pastes
//    a webhook and says its raid key in /rsay; every Overwatch player in the raid then sends that kind of post to it through the
//    Overwatch server, which posts each event once. Each kind is sent automatically only when its "Share automatically" is ticked, and
//    only from live lines (a kill as its death line is read, loot within 15 minutes of the kill); otherwise the Send buttons do it.
public sealed partial class MainViewModel
{
    private readonly HashSet<string> _raidBosses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _raidBossesUnticked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _announcedBosses = new();
    private readonly ConcurrentQueue<EncounterSnapshot> _killedFights = new();
    private IDiscordRelay? _discord;
    private bool _discordHooked;

    [ObservableProperty]
    private string _discordKillsUrl = string.Empty;

    [ObservableProperty]
    private string _discordKillsKey = string.Empty;

    [ObservableProperty]
    private bool _discordKillsAuto;

    [ObservableProperty]
    private string _discordLootUrl = string.Empty;

    [ObservableProperty]
    private string _discordLootKey = string.Empty;

    [ObservableProperty]
    private bool _discordLootAuto;

    [ObservableProperty]
    private string _discordUnlootedUrl = string.Empty;

    [ObservableProperty]
    private string _discordUnlootedKey = string.Empty;

    [ObservableProperty]
    private bool _discordUnlootedAuto;

    /// <summary>Say "Ream, loot Head of the Inquisitor" when an item given to one of your characters is not looted within a minute.</summary>
    [ObservableProperty]
    private bool _unlootedAlert;

    /// <summary>Save each raid boss kill's parse picture to Pictures\EQ Overwatch.</summary>
    [ObservableProperty]
    private bool _saveKillPictures;

    /// <summary>Loot lists linked to raid chat after raid boss kills, newest first (the Loot tab's Posted Loot).</summary>
    [ObservableProperty]
    private ObservableCollection<PostedLootRow> _postedLootRows = new();

    private static readonly Guid LootUnlockSourceId = new("b3c1f6a2-8d47-4e0b-9a65-2f1e7c4d9b30");
    private const string LootUnlockLabel = "Loot unlocks";
    private readonly List<(string Mob, string Zone)> _unlockTimers = new();

    /// <summary>The Settings sub-tab shown: General, Overlays, Alerts or Sharing.</summary>
    [ObservableProperty]
    private int _selectedSettingsTab;

    /// <summary>What the last webhook change or post did ("Raid boss kills webhook saved.").</summary>
    [ObservableProperty]
    private string _discordStatus = string.Empty;

    /// <summary>Connects the Overwatch server that relays the posts. Without it nothing is sent.</summary>
    public void ConfigureDiscord(IDiscordRelay relay) => _discord = relay;

    private void HookDiscord()
    {
        if (_discordHooked)
        {
            return;
        }

        _discordHooked = true;
        _discordKillsKey = RaidKey.New(HookKind.Kills);
        _discordLootKey = RaidKey.New(HookKind.Loot);
        _discordUnlootedKey = RaidKey.New(HookKind.Unlooted);
        _pipeline.LiveDps.RaidBossAnnounced += _announcedBosses.Enqueue;
        _pipeline.HistoricalDps.RaidBossAnnounced += _announcedBosses.Enqueue;
        _pipeline.LiveDps.EncounterKilled += fight =>
        {
            if (IsRaidBoss(fight.TargetName))
            {
                _killedFights.Enqueue(fight);
            }
        };
    }

    // ───────────── raid bosses ─────────────

    /// <summary>True when <paramref name="mob"/> is ticked as a raid boss.</summary>
    public bool IsRaidBoss(string mob)
    {
        lock (_raidBosses)
        {
            return _raidBosses.Contains(mob.Trim());
        }
    }

    /// <summary>Ticks or unticks <paramref name="mob"/> (every fight of it); an untick keeps the kill announcement from ticking it again.</summary>
    public void SetRaidBoss(string mob, bool raidBoss)
    {
        mob = mob.Trim();
        lock (_raidBosses)
        {
            if (raidBoss)
            {
                _raidBosses.Add(mob);
                _raidBossesUnticked.Remove(mob);
            }
            else
            {
                _raidBosses.Remove(mob);
                _raidBossesUnticked.Add(mob);
            }
        }

        ShowRaidBossTicks(mob);
        RequestSave();
    }

    private void ShowRaidBossTicks(string mob)
    {
        bool ticked = IsRaidBoss(mob);
        foreach (EncounterRowViewModel row in Encounters.Where(r => r.TargetName.Equals(mob, StringComparison.OrdinalIgnoreCase)))
        {
            row.IsRaidBoss = ticked;
        }
    }

    /// <summary>Ticks the mobs the server announced as raid boss kills (from the refresh tick).</summary>
    private void DrainRaidBossAnnouncements()
    {
        bool changed = false;
        while (_announcedBosses.TryDequeue(out string? mob))
        {
            lock (_raidBosses)
            {
                if (_raidBossesUnticked.Contains(mob) || !_raidBosses.Add(mob))
                {
                    continue;
                }
            }

            ShowRaidBossTicks(mob);
            changed = true;
        }

        if (changed)
        {
            RequestSave();
        }
    }

    private void RestoreDiscord(Core.Settings.AppSettings settings)
    {
        lock (_raidBosses)
        {
            _raidBosses.Clear();
            _raidBossesUnticked.Clear();
            _raidBosses.UnionWith(settings.RaidBosses ?? new List<string>());
            _raidBossesUnticked.UnionWith(settings.RaidBossesUnticked ?? new List<string>());
        }

        DiscordKillsUrl = settings.DiscordKillsUrl ?? string.Empty;
        DiscordKillsKey = RaidKey.IsValid(settings.DiscordKillsKey ?? string.Empty, HookKind.Kills) ? settings.DiscordKillsKey! : RaidKey.New(HookKind.Kills);
        DiscordKillsAuto = settings.DiscordKillsAuto;
        DiscordLootUrl = settings.DiscordLootUrl ?? string.Empty;
        DiscordLootKey = RaidKey.IsValid(settings.DiscordLootKey ?? string.Empty, HookKind.Loot) ? settings.DiscordLootKey! : RaidKey.New(HookKind.Loot);
        DiscordLootAuto = settings.DiscordLootAuto;
        DiscordUnlootedUrl = settings.DiscordUnlootedUrl ?? string.Empty;
        DiscordUnlootedKey = RaidKey.IsValid(settings.DiscordUnlootedKey ?? string.Empty, HookKind.Unlooted) ? settings.DiscordUnlootedKey! : RaidKey.New(HookKind.Unlooted);
        DiscordUnlootedAuto = settings.DiscordUnlootedAuto;
        UnlootedAlert = settings.UnlootedAlert;
        SaveKillPictures = settings.SaveKillPictures;
        PostedLootRows = new ObservableCollection<PostedLootRow>(
            (settings.PostedLoot ?? new List<Core.Settings.PostedLootEntry>()).OrderByDescending(e => e.Utc).Select(PostedLootRow.From));
    }

    private void SaveDiscord(Core.Settings.AppSettings settings)
    {
        lock (_raidBosses)
        {
            settings.RaidBosses = _raidBosses.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            settings.RaidBossesUnticked = _raidBossesUnticked.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        }

        settings.DiscordKillsUrl = DiscordKillsUrl;
        settings.DiscordKillsKey = DiscordKillsKey;
        settings.DiscordKillsAuto = DiscordKillsAuto;
        settings.DiscordLootUrl = DiscordLootUrl;
        settings.DiscordLootKey = DiscordLootKey;
        settings.DiscordLootAuto = DiscordLootAuto;
        settings.DiscordUnlootedUrl = DiscordUnlootedUrl;
        settings.DiscordUnlootedKey = DiscordUnlootedKey;
        settings.DiscordUnlootedAuto = DiscordUnlootedAuto;
        settings.UnlootedAlert = UnlootedAlert;
        settings.SaveKillPictures = SaveKillPictures;
        settings.PostedLoot = PostedLootRows.Take(200).Reverse().Select(r => r.Entry).ToList();
    }

    partial void OnDiscordKillsAutoChanged(bool value) => SaveUnlessRestoring();

    partial void OnDiscordLootAutoChanged(bool value) => SaveUnlessRestoring();

    partial void OnDiscordUnlootedAutoChanged(bool value) => SaveUnlessRestoring();

    partial void OnUnlootedAlertChanged(bool value) => SaveUnlessRestoring();

    partial void OnSaveKillPicturesChanged(bool value) => SaveUnlessRestoring();

    // ───────────── webhooks (the owner's side) ─────────────

    /// <summary>Saves a webhook address and registers it under its raid key on the Overwatch server (empty: forgets it).</summary>
    [RelayCommand]
    private Task SaveDiscordKills() => SaveHook(HookKind.Kills);

    [RelayCommand]
    private Task SaveDiscordLoot() => SaveHook(HookKind.Loot);

    [RelayCommand]
    private Task SaveDiscordUnlooted() => SaveHook(HookKind.Unlooted);

    [RelayCommand]
    private Task NewDiscordKillsKey() => NewKey(HookKind.Kills);

    [RelayCommand]
    private Task NewDiscordLootKey() => NewKey(HookKind.Loot);

    [RelayCommand]
    private Task NewDiscordUnlootedKey() => NewKey(HookKind.Unlooted);

    [RelayCommand]
    private void CopyDiscordKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        try
        {
            _clipboard.SetText(key);
            DiscordStatus = $"Copied {key}. Say it in /rsay so the raid sends to your Discord.";
        }
        catch (Exception ex)
        {
            DiscordStatus = $"The clipboard could not be used: {ex.Message}";
        }
    }

    private static string HookName(HookKind kind) => kind switch
    {
        HookKind.Kills => "Raid boss kills",
        HookKind.Loot => "Posted loot",
        _ => "Unlooted items",
    };

    private (string Url, string Key) Hook(HookKind kind) => kind switch
    {
        HookKind.Kills => (DiscordKillsUrl, DiscordKillsKey),
        HookKind.Loot => (DiscordLootUrl, DiscordLootKey),
        _ => (DiscordUnlootedUrl, DiscordUnlootedKey),
    };

    private (string Id, string Key) DiscordInstall()
    {
        if (_sharedDropsId.Length == 0 || _sharedDropsKey.Length == 0)
        {
            (_sharedDropsId, _sharedDropsKey) = HttpSharedDropsService.NewInstall();
        }

        return (_sharedDropsId, _sharedDropsKey);
    }

    private async Task SaveHook(HookKind kind)
    {
        (string url, string key) = Hook(kind);
        RequestSave();
        if (_discord is null)
        {
            return;
        }

        (string id, string installKey) = DiscordInstall();
        try
        {
            if (url.Trim().Length == 0)
            {
                await _discord.UnregisterAsync(key, id, installKey);
                DiscordStatus = $"{HookName(kind)}: webhook removed.";
                return;
            }

            string? problem = await _discord.RegisterAsync(key, kind, url, id, installKey);
            DiscordStatus = problem is null
                ? $"{HookName(kind)} webhook saved. Say {key} in /rsay so the raid sends to it."
                : $"{HookName(kind)}: {problem}";
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            DiscordStatus = $"{HookName(kind)}: the Overwatch server could not be reached.";
        }
    }

    private async Task NewKey(HookKind kind)
    {
        (string url, string old) = Hook(kind);
        string key = RaidKey.New(kind);
        switch (kind)
        {
            case HookKind.Kills:
                DiscordKillsKey = key;
                break;
            case HookKind.Loot:
                DiscordLootKey = key;
                break;
            default:
                DiscordUnlootedKey = key;
                break;
        }

        RequestSave();
        if (_discord is not null && url.Trim().Length > 0)
        {
            (string id, string installKey) = DiscordInstall();
            try
            {
                await _discord.UnregisterAsync(old, id, installKey);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                // The old key stays on the server until it is removed; nobody new will hear it.
            }

            await SaveHook(kind);
        }
        else
        {
            DiscordStatus = $"{HookName(kind)}: new key {key}.";
        }
    }

    // ───────────── posts ─────────────

    /// <summary>From the refresh tick: raid boss ticks, kills to post, and the loot posts that are due.</summary>
    private void TickDiscord(DateTime utcNow)
    {
        DrainRaidBossAnnouncements();
        RaidDiscordSink sink = _pipeline.RaidDiscord;
        while (_killedFights.TryDequeue(out EncounterSnapshot? fight))
        {
            DateTime killedUtc = DateTime.SpecifyKind(fight.EndTime, DateTimeKind.Local).ToUniversalTime();
            sink.Loot.OnRaidBossKill(fight.TargetName, killedUtc);
            sink.Keys.BossKilled(utcNow);
            double left = (RaidLootWatch.LootWindow - (utcNow - killedUtc)).TotalSeconds;
            if (left > 0)
            {
                _pipeline.Triggers.StartTimer(LootUnlockSourceId, "Loot unlock", fight.TargetName, LootUnlockLabel, left, utcNow);
                _unlockTimers.RemoveAll(t => t.Mob.Equals(fight.TargetName, StringComparison.OrdinalIgnoreCase));
                _unlockTimers.Add((fight.TargetName, fight.Zone));
            }

            if (SaveKillPictures)
            {
                SaveKillPicture(fight);
            }

            if (DiscordKillsAuto && utcNow - killedUtc <= RaidLootWatch.LootWindow)
            {
                _ = SendKill(fight, manual: false);
            }
        }

        // The loot unlock bars go away when a character zones out of the boss's zone or camps.
        while (sink.Moves.TryDequeue(out (string Character, string? Zone) move))
        {
            foreach ((string mob, string zone) in _unlockTimers
                         .Where(t => move.Zone is null || (t.Zone.Length > 0 && !t.Zone.Equals(move.Zone, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _pipeline.Triggers.CancelTimer(LootUnlockSourceId, mob, LootUnlockLabel);
                _unlockTimers.Remove((mob, zone));
            }
        }

        foreach (DiscordPost post in sink.Loot.Tick(utcNow))
        {
            if (post is LootListPost list)
            {
                PostedLootRows.Insert(0, PostedLootRow.From(new Core.Settings.PostedLootEntry(list.Mob, list.Poster, LootText(list.Items), list.At)));
                while (PostedLootRows.Count > 200)
                {
                    PostedLootRows.RemoveAt(PostedLootRows.Count - 1);
                }

                RequestSave();
            }

            if (post is UnlootedPost unlooted && UnlootedAlert && _ownCharacters.ContainsKey(unlooted.Player))
            {
                _pipeline.Triggers.PublishAudio(BoxAlertSourceId, BoxAlertSourceName, unlooted.Player, $"{unlooted.Player}, loot {unlooted.Item}", BoxAlertClock());
            }

            bool auto = post.Kind == HookKind.Loot ? DiscordLootAuto : DiscordUnlootedAuto;
            if (auto)
            {
                _ = Send(post, manual: false);
            }
        }

        // Items given out and not looted yet show on the Tells overlay until they are looted or the corpse opens to everyone.
        _pipeline.Notices.Sync("unlooted", TellsOverlayNotices
            ? sink.Loot.CurrentUnlooted(utcNow).Select(u => ($"Not looted: {u.Item} → {u.Player}", u.Given, u.Unlocks)).ToList()
            : new List<(string, DateTime, DateTime)>());
    }

    private static string LootText(IReadOnlyList<(string Item, int Count)> items) =>
        string.Join(" · ", items.Select(i => i.Count > 1 ? $"{i.Item} x{i.Count.ToString(CultureInfo.InvariantCulture)}" : i.Item));

    /// <summary>Saves a raid boss kill's parse picture as Pictures\EQ Overwatch\"2026-11-18 Lord Inquisitor Seru.png".</summary>
    private void SaveKillPicture(EncounterSnapshot fight)
    {
        try
        {
            byte[] png = EncounterImageRenderer.RenderPng(WithRegen(fight.WithClassesOnServer(ClassLabelOnServer)), ActivePalette, WindowTitle);
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "EQ Overwatch");
            Directory.CreateDirectory(folder);
            string name = fight.EndTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " "
                          + string.Concat(fight.TargetName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string path = Path.Combine(folder, name + ".png");
            for (int n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(folder, $"{name} {n.ToString(CultureInfo.InvariantCulture)}.png");
            }

            File.WriteAllBytes(path, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"The kill picture could not be saved: {ex.Message}";
        }
    }

    /// <summary>Sends the selected fight's Discord picture to the raid boss kills keys heard in raid chat.</summary>
    [RelayCommand]
    private Task SendKillToDiscord() => CurrentEncounter() is { } fight ? SendKill(fight, manual: true) : Task.CompletedTask;

    /// <summary>Sends the last loot list linked to raid chat.</summary>
    [RelayCommand]
    private Task SendLootListToDiscord()
    {
        if (_pipeline.RaidDiscord.Loot.LastLootList is not { } list)
        {
            StatusMessage = "No loot list was linked to raid chat after a raid boss kill yet.";
            return Task.CompletedTask;
        }

        return Send(list, manual: true);
    }

    /// <summary>Sends the items of the last raid boss kill that are still not looted.</summary>
    [RelayCommand]
    private Task SendUnlootedToDiscord()
    {
        if (_pipeline.RaidDiscord.Loot.LastUnlooted(DateTime.UtcNow) is not { } reminder)
        {
            StatusMessage = "Nothing from the last raid boss kill is waiting to be looted.";
            return Task.CompletedTask;
        }

        return Send(reminder, manual: true);
    }

    private Task SendKill(EncounterSnapshot fight, bool manual)
    {
        EncounterSnapshot shown = WithRegen(fight.WithClassesOnServer(ClassLabelOnServer));
        byte[] png;
        try
        {
            png = EncounterImageRenderer.RenderPng(shown, ActivePalette, WindowTitle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            png = Array.Empty<byte>();
        }

        var post = new KillPost(
            fight.TargetName, DateTime.SpecifyKind(fight.EndTime, DateTimeKind.Local).ToUniversalTime(), fight.DurationSeconds, fight.TotalDamage,
            fight.DurationSeconds > 0 ? fight.TotalDamage / fight.DurationSeconds : 0, fight.Zone, png);
        return Send(post, manual);
    }

    private async Task Send(DiscordPost post, bool manual)
    {
        if (_discord is null)
        {
            return;
        }

        IReadOnlyList<string> keys = _pipeline.RaidDiscord.Keys.Active(post.Kind, DateTime.UtcNow);
        if (keys.Count == 0)
        {
            if (manual)
            {
                StatusMessage = $"No raid key yet: nobody said a {RaidKey.Prefix(post.Kind)}- key in raid chat since you joined the raid.";
            }

            return;
        }

        int sent = 0;
        string? problem = null;
        foreach (string key in keys)
        {
            try
            {
                if (await _discord.PostAsync(key, post) is { } why)
                {
                    problem = why;
                }
                else
                {
                    sent++;
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                problem = "the Overwatch server could not be reached";
            }
        }

        if (manual || problem is not null)
        {
            StatusMessage = problem is null
                ? $"Sent {post.Mob} to Discord ({sent} raid key{(sent == 1 ? string.Empty : "s")})."
                : $"Discord: {problem}.";
        }
    }
}

/// <summary>A row of the Loot tab's Posted Loot: a loot list linked to raid chat after a raid boss kill.</summary>
public sealed record PostedLootRow(string Mob, string Poster, string Items, string Time, DateTime TimeSort, Core.Settings.PostedLootEntry Entry)
{
    public static PostedLootRow From(Core.Settings.PostedLootEntry e)
    {
        DateTime local = DateTime.SpecifyKind(e.Utc, DateTimeKind.Utc).ToLocalTime();
        return new PostedLootRow(e.Mob, e.Poster, e.Items, local.ToString("MMM d HH:mm", CultureInfo.CurrentCulture), local, e);
    }
}
