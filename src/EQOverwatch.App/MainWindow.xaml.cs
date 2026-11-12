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

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private bool _userTyping;
    private GlobalHotkey? _hotkey;

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

        _viewModel = viewModel;
        DataContext = viewModel;

        // Opens where it was last time; stored before the view model's final save on close.
        WindowPlacementKeeper.Attach(
            this,
            () => viewModel.GetWindowPlacement(WindowPlacementSetting.Main),
            placement => viewModel.SetWindowPlacement(WindowPlacementSetting.Main, placement));
        viewModel.ShowColorsRequested += OpenColorsWindow;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => _viewModel.Refresh();

        Loaded += (_, _) =>
        {
            _refreshTimer.Start();
            DiscordUrlBox.Password = _viewModel.DiscordWebhookUrl; // a password box cannot be bound, so it is filled in by hand
        };
        SourceInitialized += (_, _) =>
        {
            // The hotkey that switches the CH auto-click on and off works from any program.
            _hotkey = new GlobalHotkey(this, id: 0x4551);
            _hotkey.Pressed += _viewModel.ToggleAutoClick;
            _viewModel.AutoClickHotkeyChanged += RegisterAutoClickHotkey;
            RegisterAutoClickHotkey();
        };
        Closing += (_, _) =>
        {
            _refreshTimer.Stop();
            _viewModel.AutoClickHotkeyChanged -= RegisterAutoClickHotkey;
            _hotkey?.Dispose();
            _viewModel.Shutdown();
        };
    }

    private void DiscordUrlBox_PasswordChanged(object sender, RoutedEventArgs e) => _viewModel.DiscordWebhookUrl = DiscordUrlBox.Password;

    private void RegisterAutoClickHotkey()
    {
        if (_hotkey is null)
        {
            return;
        }

        bool ok = HotkeyParser.TryParse(_viewModel.AutoClickHotkey, out uint modifiers, out uint key) && _hotkey.Register(modifiers, key);
        if (!ok)
        {
            _hotkey.Unregister();
        }

        _viewModel.ReportAutoClickHotkey(ok);
    }

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
