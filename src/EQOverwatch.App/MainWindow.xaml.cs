using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EQOverwatch.App.Interop;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Settings;

namespace EQOverwatch.App;

/// <summary>
/// Six-tab main window. A 250ms UI timer asks the view model to re-read the DPS snapshots; closing the window performs
/// the view model's clean shutdown (save settings, stop monitoring, close the overlays, dispose audio).
/// </summary>
public partial class MainWindow : Window
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    // The page's tables are full height, so the wheel scrolls the whole page even over a table.
    // Several fights picked in the Encounter DPS list show as one combined parse.
    private void EncounterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid)
        {
            _viewModel?.SetPickedEncounters(grid.SelectedItems.OfType<EncounterRowViewModel>().ToList());
        }
    }

    private void ScrollWholePage(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer page)
        {
            page.ScrollToVerticalOffset(page.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }
    }

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private bool _userTyping;

    private ColorsWindow? _colorsWindow;

    private void OpenColorsWindow()
    {
        if (_colorsWindow is null)
        {
            _colorsWindow = new ColorsWindow(_viewModel) { Owner = this };
            _colorsWindow.Closed += (_, _) => _colorsWindow = null;
            _colorsWindow.Show();
        }
        else
        {
            if (_colorsWindow.WindowState == WindowState.Minimized)
            {
                _colorsWindow.WindowState = WindowState.Normal;
            }

            _colorsWindow.Activate();
        }
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        Interop.DarkWindowChrome.Apply(this); // theme-colored top strip instead of the white title bar

        _viewModel = viewModel;
        DataContext = viewModel;

        // Opens where it was last time; stored before the view model's final save on close.
        WindowPlacementKeeper.Attach(
            this,
            () => viewModel.GetWindowPlacement(WindowPlacementSetting.Main),
            placement => viewModel.SetWindowPlacement(WindowPlacementSetting.Main, placement));
        viewModel.ShowColorsRequested += OpenColorsWindow;
        OnEditionCreated();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => _viewModel.Refresh();

        Loaded += (_, _) =>
        {
            _refreshTimer.Start();
        };
        SourceInitialized += (_, _) => OnEditionSourceInitialized();
        Closing += (_, e) =>
        {
            if (!_viewModel.Maps.ConfirmClose())
            {
                e.Cancel = true; // the user chose to stay with unsaved map labels
                return;
            }

            _refreshTimer.Stop();
            OnEditionClosing();
            _viewModel.Shutdown();
        };
    }


    // Hooks an edition fills in with windows and hotkeys of its own (they do nothing in this one).
    partial void OnEditionCreated();

    partial void OnEditionSourceInitialized();

    partial void OnEditionClosing();

    /// <summary>Lists the characters whose logs can be searched (logs may have been added since the tab was opened).</summary>
    private void LogSearchCharacters_DropDownOpened(object sender, EventArgs e) => _viewModel.RefreshLogSearchCharactersCommand.Execute(null);

    /// <summary>Enter in the search box searches.</summary>
    private void LogSearchText_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && _viewModel.SearchLogsCommand.CanExecute(null))
        {
            _viewModel.SearchLogsCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Double-clicking a found line copies it.</summary>
    private void LogSearchGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: LogSearchRow row })
        {
            _viewModel.CopyLogSearchRowCommand.Execute(row);
        }
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
            bool wasOpen = combo.IsDropDownOpen;
            combo.IsDropDownOpen = true;
            if (!wasOpen)
            {
                // Opening the list highlights the whole text, and the next letter would replace it ("D" then "a" left only "a").
                // Put the cursor back at the end now, and again once WPF has finished opening the list.
                PutCursorAtEnd(combo);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => PutCursorAtEnd(combo)));
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => PutCursorAtEnd(combo)));
            }
        }
    }

    private static void PutCursorAtEnd(ComboBox combo)
    {
        if (combo.Template?.FindName("PART_EditableTextBox", combo) is TextBox box
            && box.Text.Length > 0 && box.SelectionLength == box.Text.Length)
        {
            box.SelectionStart = box.Text.Length;
            box.SelectionLength = 0;
        }
    }
}
