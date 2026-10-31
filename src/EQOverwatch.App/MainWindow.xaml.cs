using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EQOverwatch.App.ViewModels;

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
