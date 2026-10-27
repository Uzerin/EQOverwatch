using System.Windows.Threading;
using EQCompanion.App.Services;
using EQCompanion.App.ViewModels;

namespace EQCompanion.App.Overlays;

/// <summary>Mini DPS meter. Refreshed every 250ms from the live tracker's snapshot.</summary>
public partial class DpsOverlayWindow : OverlayWindowBase
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly DpsOverlayViewModel _viewModel;
    private readonly DispatcherTimer _timer;

    public DpsOverlayWindow(DpsOverlayViewModel viewModel)
    {
        InitializeComponent();

        OverlayName = OverlayKind.Dps.Title();

        _viewModel = viewModel;
        DataContext = viewModel;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => _viewModel.Tick();

        Loaded += (_, _) =>
        {
            _viewModel.Tick();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }
}
