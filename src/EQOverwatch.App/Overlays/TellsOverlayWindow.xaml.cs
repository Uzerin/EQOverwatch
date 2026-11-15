using System.Windows.Threading;
using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;

namespace EQOverwatch.App.Overlays;

/// <summary>Your characters' unanswered tells. Refreshed every second from the tell inbox.</summary>
public partial class TellsOverlayWindow : OverlayWindowBase
{
    private readonly DispatcherTimer _timer;

    public TellsOverlayWindow(TellsOverlayViewModel viewModel)
    {
        InitializeComponent();
        OverlayName = OverlayKind.Tells.Title();
        DataContext = viewModel;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => viewModel.Tick();
        Loaded += (_, _) =>
        {
            viewModel.Tick();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }
}
