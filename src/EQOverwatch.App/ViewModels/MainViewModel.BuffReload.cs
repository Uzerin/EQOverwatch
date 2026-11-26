using CommunityToolkit.Mvvm.Input;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App.ViewModels;

// "Reload buff bars": the buff and debuff bars are started again from the last hours of the logs (also once after an update).
public sealed partial class MainViewModel
{
    private string _buffBarsVersion = string.Empty;
    private int _buffReloadRunning;

    [RelayCommand]
    private Task ReloadBuffBars() => ReloadBuffBarsAsync();

    internal async Task ReloadBuffBarsAsync()
    {
        if (Interlocked.CompareExchange(ref _buffReloadRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (SpellLoadTask is { } loading)
            {
                await loading; // the bars need the spell file
            }

            var logs = Logs.Where(l => !string.IsNullOrWhiteSpace(l.FilePath))
                .Select(l => (l.FilePath, l.CharacterName, l.ServerName)).ToList();
            DateTime sinceLocal = _clock().Add(-BuffBarReload.Window);
            int bars = await Task.Run(() =>
            {
                List<BuffBarReload.ReplayLine> lines = BuffBarReload.ReadRecent(logs, sinceLocal);
                return BuffBarReload.Replay(_pipeline.Buffs, _pipeline.Triggers, lines, DateTime.UtcNow);
            });

            _buffBarsVersion = EQOverwatch.Core.AppInfo.Version;
            RequestSave();
            TrackerStatus = $"Buff bars reloaded from the last {BuffBarReload.Window.TotalHours:0} hours of your logs: {bars} up.";
        }
        catch (Exception ex)
        {
            TrackerStatus = $"Could not reload the buff bars: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _buffReloadRunning, 0);
        }
    }
}
