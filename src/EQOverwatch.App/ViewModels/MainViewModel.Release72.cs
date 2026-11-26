using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Characters;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Progress;

namespace EQOverwatch.App.ViewModels;

/// <summary>A tell to announce with a Windows notification.</summary>
public sealed record TellToastInfo(string Character, string From, string Text);

// Accounts (names, shared banks, colors, the Characters list's account columns), floating text options, tell toasts and the item total.
public sealed partial class MainViewModel
{
    private const string NoAccountColor = "#C8C8C8";

    private volatile Dictionary<string, string> _characterAccounts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _savedAccountColors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The account colors, one per named account, edited in the Colors window.</summary>
    public ObservableCollection<AttackColorEditorViewModel> AccountColors { get; } = new();

    private static string CharacterKey(string name, string server) => $"{name}|{server}".ToLowerInvariant();

    private static string AccountColorKey(string server, string account) => $"{server}|{account}".ToLowerInvariant();

    /// <summary>The account name typed for a character, or empty.</summary>
    public string AccountOf(string name, string server) =>
        _characterAccounts.TryGetValue(CharacterKey(name, server), out string? account) ? account : string.Empty;

    /// <summary>Names a character's account (empty clears it) and refreshes everything that shows accounts.</summary>
    public void SetAccount(string name, string server, string account)
    {
        var copy = new Dictionary<string, string>(_characterAccounts, StringComparer.OrdinalIgnoreCase);
        string key = CharacterKey(name, server);
        account = (account ?? string.Empty).Trim();
        if (account.Length == 0)
        {
            copy.Remove(key);
        }
        else
        {
            // Keep the spelling already used on the server, so "ream box" joins "Ream box".
            string server2 = (server ?? string.Empty).ToLowerInvariant();
            account = copy.Where(p => p.Key.EndsWith("|" + server2, StringComparison.Ordinal))
                .Select(p => p.Value).FirstOrDefault(a => a.Equals(account, StringComparison.OrdinalIgnoreCase)) ?? account;
            copy[key] = account;
        }

        _characterAccounts = copy;
        RebuildAccountColors();
        SaveUnlessRestoring();
        RebuildItemSearch();
        RebuildSortedCharacters();
        foreach (WeakReference<CharacterDataViewModel> reference in _openProfiles)
        {
            if (reference.TryGetTarget(out CharacterDataViewModel? profile) && !(profile.CharacterName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                                                                  && profile.ServerName.Equals(server, StringComparison.OrdinalIgnoreCase)))
            {
                profile.Reload();
            }
        }
    }

    /// <summary>The account names already used on a server, A-Z.</summary>
    public IReadOnlyList<string> AccountNamesOn(string server)
    {
        string suffix = "|" + (server ?? string.Empty).ToLowerInvariant();
        return _characterAccounts.Where(p => p.Key.EndsWith(suffix, StringComparison.Ordinal)).Select(p => p.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One color editor per named account, keeping colors already chosen.</summary>
    private void RebuildAccountColors()
    {
        foreach (AttackColorEditorViewModel editor in AccountColors.Where(e => e.Hex != e.DefaultHex))
        {
            _savedAccountColors[editor.Key] = editor.Hex;
        }

        var accounts = _characterAccounts
            .Select(p => (Server: p.Key[(p.Key.IndexOf('|') + 1)..], Account: p.Value))
            .DistinctBy(a => AccountColorKey(a.Server, a.Account))
            .OrderBy(a => a.Account, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Server, StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool severalServers = accounts.Select(a => a.Server).Distinct().Count() > 1;
        AccountColors.Clear();
        foreach ((string server, string account) in accounts)
        {
            string key = AccountColorKey(server, account);
            string hex = _savedAccountColors.TryGetValue(key, out string? saved) && EQOverwatch.Core.Theming.ThemeColors.TryNormalize(saved, out string n) ? n : NoAccountColor;
            AccountColors.Add(new AttackColorEditorViewModel(key, severalServers ? $"{account} ({server})" : account, NoAccountColor, hex, (_, _) =>
            {
                SaveUnlessRestoring();
                RefreshAccountColumns(DateTime.UtcNow);
            }));
        }
    }

    private Dictionary<string, string> AccountColorsToSave()
    {
        var colors = new Dictionary<string, string>(_savedAccountColors, StringComparer.OrdinalIgnoreCase);
        foreach (AttackColorEditorViewModel editor in AccountColors)
        {
            if (editor.Hex == editor.DefaultHex)
            {
                colors.Remove(editor.Key);
            }
            else
            {
                colors[editor.Key] = editor.Hex;
            }
        }

        return colors;
    }

    /// <summary>A named account's color, or empty when none was chosen.</summary>
    private string AccountColorOf(string server, string account)
    {
        if (account.Length == 0)
        {
            return string.Empty;
        }

        string key = AccountColorKey(server, account);
        AttackColorEditorViewModel? editor = AccountColors.FirstOrDefault(e => e.Key == key);
        string hex = editor?.Hex ?? (_savedAccountColors.TryGetValue(key, out string? saved) ? saved : NoAccountColor);
        return hex.Equals(NoAccountColor, StringComparison.OrdinalIgnoreCase) ? string.Empty : hex;
    }

    /// <summary>
    /// Fills the Characters list's Account, active dot and account last played columns: the named account or the characters whose shared
    /// banks match, whether the character is logged in, and when anyone on the account last played (the newest log write).
    /// </summary>
    private void RefreshAccountColumns(DateTime nowUtc)
    {
        IReadOnlyList<SharedBankData> banks = _characterData.SharedBanks();
        var groups = new Dictionary<WatchedLogConfig, string>();
        foreach (WatchedLogConfig log in Logs)
        {
            string named = AccountOf(log.CharacterName, log.ServerName);
            SharedBankData? bank = banks.FirstOrDefault(b => b.Server.Equals(log.ServerName, StringComparison.OrdinalIgnoreCase)
                                                             && b.Characters.Contains(log.CharacterName, StringComparer.OrdinalIgnoreCase));
            string label = named.Length > 0 ? named
                : bank is { Characters.Count: > 1 } ? bank.Label
                : "—";
            log.AccountText = label;
            log.AccountColor = AccountColorOf(log.ServerName, bank?.Account.Length > 0 ? bank.Account : named);
            log.IsActiveNow = _pipeline.Activity.IsActive(log.CharacterName, log.ServerName, nowUtc); // this server only
            log.GuildText = _pipeline.Guilds.Get(log.CharacterName, log.ServerName);
            groups[log] = label == "—" ? "solo|" + log.ServerName + "|" + log.CharacterName : log.ServerName + "|" + label;
        }

        foreach (IGrouping<string, KeyValuePair<WatchedLogConfig, string>> account in groups.GroupBy(g => g.Value, StringComparer.OrdinalIgnoreCase))
        {
            bool active = account.Any(g => g.Key.IsActiveNow);
            DateTime last = account.Select(g => LastWriteUtc(g.Key.FilePath)).DefaultIfEmpty(DateTime.MinValue).Max();
            string text = active ? "Now" : last == DateTime.MinValue ? "—" : AgoText(nowUtc - last);
            foreach (KeyValuePair<WatchedLogConfig, string> member in account)
            {
                member.Key.AccountPlayedText = text;
            }
        }
    }

    private static DateTime LastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>"5m ago", "3h ago", "2d ago".</summary>
    public static string AgoText(TimeSpan ago)
    {
        if (ago < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)ago.TotalMinutes)}m ago";
        }

        return ago < TimeSpan.FromDays(1) ? $"{(int)ago.TotalHours}h ago" : $"{(int)ago.TotalDays}d ago";
    }

    /// <summary>"43 found on 2 characters and 1 shared bank."</summary>
    public static string ItemTotalText(IReadOnlyList<ItemHit> hits)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        long total = hits.Sum(h => (long)Math.Max(1, h.Count));
        int characters = hits.Where(h => !h.Where.StartsWith("Shared bank", StringComparison.OrdinalIgnoreCase)).Select(h => h.Character)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int banks = hits.Where(h => h.Where.StartsWith("Shared bank", StringComparison.OrdinalIgnoreCase)).Select(h => h.Character)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var parts = new List<string>();
        if (characters > 0)
        {
            parts.Add($"{characters.ToString(culture)} character{(characters == 1 ? string.Empty : "s")}");
        }

        if (banks > 0)
        {
            parts.Add($"{banks.ToString(culture)} shared bank{(banks == 1 ? string.Empty : "s")}");
        }

        return $"{total.ToString("N0", culture)} found on {string.Join(" and ", parts)}.";
    }

    // ───────────── floating text options ─────────────

    public IReadOnlyList<string> FctFontChoices { get; } = new[] { "Segoe UI", "Arial", "Verdana", "Tahoma", "Trebuchet MS", "Calibri", "Consolas", "Impact" };

    public IReadOnlyList<string> FctWeightChoices { get; } = new[] { "Normal", "SemiBold", "Bold", "Black" };

    /// <summary>Hits within 0.3 s from one character on one target show as one number ("3,412 x4").</summary>
    [ObservableProperty]
    private bool _fctMergeHits;

    /// <summary>Misses and resists show as small gray words.</summary>
    [ObservableProperty]
    private bool _fctShowMisses = true;

    [ObservableProperty]
    private string _fctFont = "Segoe UI";

    [ObservableProperty]
    private string _fctWeight = "SemiBold";

    private FontFamily? _fctFontFamily;

    public FontFamily FctFontFamily => _fctFontFamily ??= new FontFamily(string.IsNullOrWhiteSpace(FctFont) ? "Segoe UI" : FctFont);

    public FontWeight FctFontWeight => FctWeight switch
    {
        "Normal" => FontWeights.Normal,
        "Bold" => FontWeights.Bold,
        "Black" => FontWeights.Black,
        _ => FontWeights.SemiBold,
    };

    partial void OnFctMergeHitsChanged(bool value) => SaveUnlessRestoring();

    partial void OnFctShowMissesChanged(bool value)
    {
        _pipeline.FloatingCombat.ShowMisses = value;
        SaveUnlessRestoring();
    }

    partial void OnFctFontChanged(string value)
    {
        _fctFontFamily = null;
        SaveUnlessRestoring();
    }

    partial void OnFctWeightChanged(string value) => SaveUnlessRestoring();

    // ───────────── tell toasts ─────────────

    /// <summary>A Windows notification when a tell arrives while EverQuest is not the active window.</summary>
    [ObservableProperty]
    private bool _tellToast = true;

    partial void OnTellToastChanged(bool value) => SaveUnlessRestoring();

    /// <summary>Raised on the UI thread for each new live tell while <see cref="TellToast"/> is on; the app decides whether EQ has focus.</summary>
    public event Action<TellToastInfo>? TellToastRequested;

    private long _tellToastVersion = -1;
    private DateTime _tellToastSince = DateTime.UtcNow;

    private void RefreshRelease72(DateTime nowUtc)
    {
        long version = _pipeline.TellInbox.Version;
        if (version == _tellToastVersion)
        {
            return;
        }

        _tellToastVersion = version;
        List<TellEntry> fresh = _pipeline.TellInbox.All()
            .Where(t => t.Utc > _tellToastSince && nowUtc - t.Utc < TimeSpan.FromMinutes(2) && !t.Answered)
            .OrderBy(t => t.Utc)
            .ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        _tellToastSince = fresh[^1].Utc;
        CountTellsForBadge(fresh.Count);
        if (!TellToast)
        {
            return;
        }

        foreach (TellEntry tell in fresh.TakeLast(3))
        {
            TellToastRequested?.Invoke(new TellToastInfo(tell.Character, tell.From, tell.Text));
        }
    }
}

// Mob History: the kill time trend column.
public sealed partial class MainViewModel
{
    /// <summary>"Faster 12%", "Slower 8%" or "Steady": the last kills' average time against the earlier ones; a dash under 3 kills.</summary>
    public static string KillTrendText(EQOverwatch.Core.History.BossTrend? trend)
    {
        if (trend is null || !trend.CanCompare || trend.EarlierAverageSeconds <= 0)
        {
            return "–";
        }

        double percent = (trend.EarlierAverageSeconds - trend.RecentAverageSeconds) / trend.EarlierAverageSeconds * 100.0;
        if (Math.Abs(percent) < 3)
        {
            return "Steady";
        }

        return string.Create(CultureInfo.CurrentCulture, $"{(percent > 0 ? "Faster" : "Slower")} {Math.Abs(percent):0}%");
    }
}
