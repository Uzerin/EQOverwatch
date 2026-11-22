using System.IO;
using System.Windows.Threading;
using EQOverwatch.Core;
using EQOverwatch.Core.Discovery;

namespace EQOverwatch.App.ViewModels;

// The Maps tab: custom labels on the in-game maps.
public sealed partial class MainViewModel
{
    private MapsViewModel? _maps;

    /// <summary>The Maps tab.</summary>
    public MapsViewModel Maps => _maps ??= CreateMaps();

    private MapsViewModel CreateMaps()
    {
        var maps = new MapsViewModel(
        () => EqDirectoryScanner.Normalize(EqFolderPath),
        _pipeline.Zones,
        () => Logs,
        _dialogs,
        action =>
        {
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            {
                dispatcher.BeginInvoke(action, DispatcherPriority.Background);
            }
            else
            {
                action();
            }
        });
        maps.ConfigurePack(() => IsInCombat, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.SettingsFolderName));
        return maps;
    }

    private void InitializeMaps() => Maps.Initialize(LastPlayedLog());

    private void RefreshMaps() => Maps.Refresh(LastPlayedLog());
}
