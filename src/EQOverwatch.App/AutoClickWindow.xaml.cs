using System.Windows;
using System.Windows.Input;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Services;

namespace EQOverwatch.App;

/// <summary>Settings → Cleric CH chain → Advanced: the auto-press switch, timing, CH key, assist key and hotkey.</summary>
public partial class AutoClickWindow : Window
{
    private readonly MainViewModel _viewModel;

    public AutoClickWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => viewModel.RecordAutoClickKey(null);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>After Set Key…, the next key pressed (with its Shift, Ctrl and Alt) becomes that key; Esc cancels.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsRecordingAutoClickKey)
        {
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return; // wait for the key itself
        }

        if (key == Key.Escape)
        {
            _viewModel.RecordAutoClickKey(null);
            return;
        }

        ModifierKeys mods = Keyboard.Modifiers;
        _viewModel.RecordAutoClickKey(new KeyChord(
            KeyInterop.VirtualKeyFromKey(key),
            mods.HasFlag(ModifierKeys.Control),
            mods.HasFlag(ModifierKeys.Alt),
            mods.HasFlag(ModifierKeys.Shift)));
    }
}
