using System.Windows.Threading;
using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core;

namespace EQOverwatch.App.Overlays;

/// <summary>Flash alerts and countdown bars for one category (Buffs, Debuffs, Songs, Raid Triggers or CH Chain). Refreshed every 50ms.</summary>
public partial class TimerOverlayWindow : OverlayWindowBase
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(50);

    private readonly TimerOverlayViewModel _viewModel;
    private readonly DispatcherTimer _timer;

    public TimerOverlayWindow(TimerOverlayViewModel viewModel, OverlayKind kind)
    {
        InitializeComponent();

        OverlayName = kind.Title();
        Title = $"{AppInfo.Name} — {kind.Title()}";

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
