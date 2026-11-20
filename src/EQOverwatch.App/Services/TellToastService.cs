using System.Windows.Threading;

namespace EQOverwatch.App.Services;

/// <summary>
/// Shows a Windows notification (a tray balloon, which Windows 10 and 11 show as a toast) with the app's icon. The tray icon is only
/// there while a notification is up.
/// </summary>
public sealed class TellToastService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _hide;

    public TellToastService()
    {
        _icon = new System.Windows.Forms.NotifyIcon { Text = EQOverwatch.Core.AppInfo.Name, Visible = false };
        try
        {
            if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon)
            {
                _icon.Icon = icon;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException)
        {
            _icon.Icon = System.Drawing.SystemIcons.Information;
        }

        _icon.Icon ??= System.Drawing.SystemIcons.Information;
        _icon.BalloonTipClicked += (_, _) => System.Windows.Application.Current?.MainWindow?.Activate();
        _hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            _icon.Visible = false;
        };
    }

    public void Show(string title, string text)
    {
        _icon.Visible = true;
        _icon.ShowBalloonTip(8000, title, text.Length > 200 ? text[..200] + "…" : text, System.Windows.Forms.ToolTipIcon.None);
        _hide.Stop();
        _hide.Start();
    }

    public void Dispose()
    {
        _hide.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
