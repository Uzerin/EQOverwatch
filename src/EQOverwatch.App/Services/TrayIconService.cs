using System.Windows;
using EQOverwatch.App.ViewModels;
using Forms = System.Windows.Forms;

namespace EQOverwatch.App.Services;

/// <summary>
/// Minimize to the system tray: while the setting is on, minimizing the main window hides it and shows a tray icon. Double-click
/// the icon (or Open) to bring the window back; the right-click menu also has Mute audio, Lock overlays and Exit.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _main;
    private readonly MainViewModel _viewModel;
    private readonly Forms.ToolStripMenuItem _mute;
    private readonly Forms.ToolStripMenuItem _lock;
    private WindowState _restoreTo = WindowState.Normal;

    public TrayIconService(Window main, MainViewModel viewModel)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _icon = new Forms.NotifyIcon { Text = Core.AppInfo.Name, Visible = false };
        try
        {
            _icon.Icon = Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException)
        {
            _icon.Icon = null;
        }

        _icon.Icon ??= System.Drawing.SystemIcons.Application;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open " + Core.AppInfo.Name, null, (_, _) => Restore());
        _mute = new Forms.ToolStripMenuItem("Mute audio", null, (_, _) => _viewModel.IsAudioMuted = !_viewModel.IsAudioMuted);
        _lock = new Forms.ToolStripMenuItem("Lock overlays", null, (_, _) => _viewModel.AreOverlaysLocked = !_viewModel.AreOverlaysLocked);
        menu.Items.Add(_mute);
        menu.Items.Add(_lock);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _main.Dispatcher.BeginInvoke(() => _main.Close()));
        menu.Opening += (_, _) =>
        {
            _mute.Checked = _viewModel.IsAudioMuted;
            _lock.Checked = _viewModel.AreOverlaysLocked;
        };
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => Restore();

        _main.StateChanged += OnStateChanged;
        if (_main.WindowState == WindowState.Minimized)
        {
            OnStateChanged(null, EventArgs.Empty); // started minimized with Windows
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_main.WindowState != WindowState.Minimized)
        {
            _restoreTo = _main.WindowState;
            return;
        }

        if (_viewModel.MinimizeToTray)
        {
            _icon.Visible = true;
            _main.Hide();
        }
    }

    /// <summary>Shows the main window again where it was.</summary>
    public void Restore()
    {
        _main.Dispatcher.BeginInvoke(() =>
        {
            _main.Show();
            _main.WindowState = _restoreTo;
            _main.Activate();
            _icon.Visible = false;
        });
    }

    public void Dispose()
    {
        _main.StateChanged -= OnStateChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
