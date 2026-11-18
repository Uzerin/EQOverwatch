using System.Windows;
using EQOverwatch.App.Interop;
using EQOverwatch.App.ViewModels;

namespace EQOverwatch.App;

/// <summary>Settings → Cleric CH chain → Advanced: the auto-click switch, hotkey, delay and click spot.</summary>
public partial class AutoClickWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _picking;

    public AutoClickWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PickAutoClickSpotRequested += Pick;
        Closed += (_, _) =>
        {
            viewModel.PickAutoClickSpotRequested -= Pick;
            viewModel.RecordAutoClickKey(null);
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>After Set Key…, the next key pressed (with its Shift, Ctrl and Alt) becomes the auto-click key; Esc cancels.</summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_viewModel.IsRecordingAutoClickKey)
        {
            return;
        }

        e.Handled = true;
        System.Windows.Input.Key key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or System.Windows.Input.Key.LeftCtrl
            or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
        {
            return; // wait for the key itself
        }

        if (key == System.Windows.Input.Key.Escape)
        {
            _viewModel.RecordAutoClickKey(null);
            return;
        }

        System.Windows.Input.ModifierKeys mods = System.Windows.Input.Keyboard.Modifiers;
        int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        _viewModel.RecordAutoClickKey(new EQOverwatch.Core.Services.KeyChord(
            vk,
            mods.HasFlag(System.Windows.Input.ModifierKeys.Control),
            mods.HasFlag(System.Windows.Input.ModifierKeys.Alt),
            mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)));
    }

    private async void Pick()
    {
        if (_picking)
        {
            return;
        }

        _picking = true;
        try
        {
            string? problem = null;
            var picker = new ClickSpotPicker(this);
            EQOverwatch.Core.Services.ClickSpot? spot = await picker.PickAsync(text => problem = text);
            _viewModel.ApplyAutoClickSpot(spot, problem);
        }
        finally
        {
            _picking = false;
        }
    }
}
