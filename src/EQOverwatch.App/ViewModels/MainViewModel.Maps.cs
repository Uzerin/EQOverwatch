using System.Windows.Threading;
using EQOverwatch.Core.Discovery;

namespace EQOverwatch.App.ViewModels;

// The Maps tab: custom labels on the in-game maps.
public sealed partial class MainViewModel
{
    private MapsViewModel? _maps;

    /// <summary>The Maps tab.</summary>
    public MapsViewModel Maps => _maps ??= new MapsViewModel(
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

    private void InitializeMaps() => Maps.Initialize(LastPlayedLog());

    private void RefreshMaps() => Maps.Refresh(LastPlayedLog());
}
