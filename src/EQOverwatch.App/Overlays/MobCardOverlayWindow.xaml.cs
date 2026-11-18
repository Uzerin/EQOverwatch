using System.Windows.Threading;
using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;

namespace EQOverwatch.App.Overlays;

/// <summary>What the app knows about the mob you just /con'd. Hidden between cards while locked.</summary>
public partial class MobCardOverlayWindow : OverlayWindowBase
{
    private readonly DispatcherTimer _timer;

    public MobCardOverlayWindow(MobCardOverlayViewModel viewModel)
    {
        InitializeComponent();
        OverlayName = OverlayKind.MobCard.Title();
        DataContext = viewModel;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => viewModel.Tick();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }
}
