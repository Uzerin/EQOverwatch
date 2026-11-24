using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EQOverwatch.Core.Tlp;

namespace EQOverwatch.App.ViewModels;

// The Resource Hunter tab's own server picker: only servers whose rules include Resource Hunter.
public partial class MainViewModel
{
    /// <summary>The servers with Resource Hunter in their rules (Frostreaver, Fangbreaker).</summary>
    public ObservableCollection<string> ResourceServers { get; } =
        new(TlpServers.All.Where(s => s.ResourceHunter).Select(s => s.Key));

    [ObservableProperty]
    private string? _selectedResourceServer;

    [ObservableProperty]
    private string _resourceRulesetNext = string.Empty;

    [ObservableProperty]
    private string _resourceRulesetNow = string.Empty;

    private bool _resourceServerPicked;
    private bool _settingResourceServer;
    private DateOnly _resourceRulesetDay;

    partial void OnSelectedResourceServerChanged(string? value)
    {
        if (!_settingResourceServer)
        {
            _resourceServerPicked = true; // picked by hand: kept
        }

        _resourceRulesetDay = default;
        ShowResourceRuleset(DateTime.UtcNow);
        ShowResourceRows(DateTime.UtcNow);
    }

    /// <summary>Follows the Resource Hunter server you played last (Frostreaver when none) until you pick one yourself.</summary>
    private void SyncResourceServer()
    {
        if (_resourceServerPicked && SelectedResourceServer is not null)
        {
            return;
        }

        static string? RhKey(string? server) => TlpServers.Find(server) is { ResourceHunter: true } s ? s.Key : null;
        string wanted = RhKey(LastPlayedLog()?.ServerName)
                        ?? Logs.Select(l => RhKey(l.ServerName)).FirstOrDefault(k => k is not null)
                        ?? ResourceServers.FirstOrDefault() ?? "frostreaver";
        if (SelectedResourceServer != wanted)
        {
            _settingResourceServer = true;
            try
            {
                SelectedResourceServer = wanted;
            }
            finally
            {
                _settingResourceServer = false;
            }
        }
    }

    private void ShowResourceRuleset(DateTime nowUtc)
    {
        DateOnly today = TlpServers.Today(nowUtc);
        if (_resourceRulesetDay == today)
        {
            return;
        }

        _resourceRulesetDay = today;
        (ResourceRulesetNext, ResourceRulesetNow) = RulesetLines(TlpServers.Find(SelectedResourceServer), today);
    }
}
