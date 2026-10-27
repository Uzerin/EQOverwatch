using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EQCompanion.App.ViewModels;

namespace EQCompanion.App;

/// <summary>
/// Six-tab main window. A 250ms UI timer asks the view model to re-read the DPS snapshots; closing the window performs
/// the view model's clean shutdown (save settings, stop monitoring, close the overlays, dispose audio).
/// </summary>
public partial class MainWindow : Window
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private bool _userTyping;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => _viewModel.Refresh();

        Loaded += (_, _) => _refreshTimer.Start();
        Closing += (_, _) =>
        {
            _refreshTimer.Stop();
            _viewModel.Shutdown();
        };
    }

    /// <summary>Remembers that the next text change in a search box comes from the keyboard, not from picking an item.</summary>
    private void SearchCombo_UserInput(object sender, EventArgs e) => _userTyping = true;

    /// <summary>Opens the match list while the user types (but not when a pick puts text into the box).</summary>
    private void SearchCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_userTyping)
        {
            return;
        }

        _userTyping = false;
        if (sender is ComboBox combo && combo.Text.Length > 0 && combo.IsKeyboardFocusWithin)
        {
            combo.IsDropDownOpen = true;
        }
    }
}
