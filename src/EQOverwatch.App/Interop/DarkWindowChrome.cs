using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace EQOverwatch.App.Interop;

/// <summary>
/// Window tops in the app's colors. <see cref="Apply"/> replaces the white Windows title bar with a thin blank strip in the theme with
/// the minimize / maximize / close buttons inside the window, like Discord; <see cref="UseDarkTitleBars"/> gives every other window
/// a dark title bar.
/// </summary>
public static class DarkWindowChrome
{
    public const double StripHeight = 26;

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    /// <summary>Every window that keeps the standard title bar gets a dark one (Windows 10 2004 and later; ignored elsewhere).</summary>
    public static void UseDarkTitleBars()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window { WindowStyle: not WindowStyle.None } window && WindowChrome.GetWindowChrome(window) is null)
            {
                SetDarkFrame(window);
            }
        }));
    }

    private static void SetDarkFrame(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            int on = 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            if (window.Background is SolidColorBrush { Color: var c })
            {
                int colorRef = c.R | (c.G << 8) | (c.B << 16); // COLORREF is 0x00BBGGRR
                _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref colorRef, sizeof(int));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: the title bar stays as it is.
        }
    }

    /// <summary>
    /// Moves the window's content under a thin, blank theme-colored strip with small minimize / maximize / close buttons. Call after InitializeComponent.
    /// Dragging the strip moves the window, double-clicking it maximizes, and the edges still resize and snap.
    /// </summary>
    private static readonly List<(Window Window, FrameworkElement Content, double Font)> Registered = new();

    /// <summary>The interface size (0.5 to 1.5) every window with this chrome is drawn at.</summary>
    public static double Scale { get; private set; } = 1.0;

    /// <summary>Compact mode: smaller text and tighter table rows.</summary>
    public static bool Compact { get; private set; }

    /// <summary>Applies the interface size and compact mode to every open window (and the ones opened later).</summary>
    public static void SetLook(double scale, bool compact)
    {
        Scale = Math.Clamp(double.IsFinite(scale) ? scale : 1.0, 0.5, 1.5);
        Compact = compact;
        if (Application.Current?.Resources is { } resources)
        {
            resources["GridCellPadding"] = compact ? new Thickness(4, 2, 4, 2) : new Thickness(6, 5, 6, 5);
            resources["GridHeaderPadding"] = compact ? new Thickness(6, 4, 6, 4) : new Thickness(8, 8, 8, 8);
        }

        foreach ((Window, FrameworkElement, double) entry in Registered.ToList())
        {
            ApplyLook(entry);
        }
    }

    private static void ApplyLook((Window Window, FrameworkElement Content, double Font) entry)
    {
        entry.Content.LayoutTransform = Math.Abs(Scale - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(Scale, Scale);
        entry.Window.FontSize = Compact ? Math.Round(entry.Font * 0.88, 1) : entry.Font;
    }

    public static void Apply(Window window, bool resizable = true)
    {
        if (window.Content is not UIElement content)
        {
            return;
        }

        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = StripHeight,
            ResizeBorderThickness = resizable ? new Thickness(6) : new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1), // keeps the Windows shadow and rounded corners
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        window.Content = null;
        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(BuildStrip(window, resizable));
        root.Children.Add(content);

        var frame = new Border { Child = root };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderSoftBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "WindowBgBrush");
        window.Content = frame;

        // A maximized window reaches past the screen edge by its (hidden) frame, so the content is pulled back in.
        void FitMaximized()
        {
            Thickness edge = SystemParameters.WindowResizeBorderThickness;
            double pad = SystemParameters.WindowNonClientFrameThickness.Left - edge.Left;
            frame.Padding = window.WindowState == WindowState.Maximized
                ? new Thickness(edge.Left + Math.Max(pad, 0) + 1, edge.Top + Math.Max(pad, 0) + 1, edge.Right + Math.Max(pad, 0) + 1, edge.Bottom + Math.Max(pad, 0) + 1)
                : new Thickness(0);
            frame.BorderThickness = window.WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        }

        window.StateChanged += (_, _) => FitMaximized();
        FitMaximized();

        if (content is FrameworkElement element)
        {
            var entry = (window, element, window.FontSize);
            Registered.Add(entry);
            window.Closed += (_, _) => Registered.Remove(entry);
            ApplyLook(entry);
        }
    }

    private static UIElement BuildStrip(Window window, bool resizable)
    {
        var strip = new DockPanel { Height = StripHeight, LastChildFill = true };
        strip.SetResourceReference(Panel.BackgroundProperty, "WindowBgBrush");
        DockPanel.SetDock(strip, Dock.Top);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(buttons, Dock.Right);
        if (resizable)
        {
            buttons.Children.Add(StripButton("", "Minimize", false, () => window.WindowState = WindowState.Minimized));
            Button maximize = StripButton("", "Maximize", false, () =>
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            window.StateChanged += (_, _) =>
            {
                bool max = window.WindowState == WindowState.Maximized;
                ((TextBlock)maximize.Content).Text = max ? "" : "";
                maximize.ToolTip = max ? "Restore" : "Maximize";
            };
            buttons.Children.Add(maximize);
        }

        buttons.Children.Add(StripButton("", "Close", true, window.Close));
        strip.Children.Add(buttons);

        // Blank, like Discord: the window's name shows on the taskbar and in Alt+Tab, and each window says what it is in its content.
        strip.Children.Add(new Border());
        return strip;
    }

    private static Button StripButton(string glyph, string tip, bool isClose, Action click)
    {
        var button = new Button
        {
            Width = 34,
            Height = StripHeight,
            ToolTip = tip,
            Focusable = false,
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9 },
            Template = StripButtonTemplate(isClose),
        };
        button.SetResourceReference(Control.ForegroundProperty, "TextMutedBrush"); // small grey glyphs, brighter on hover
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => click();
        return button;
    }

    private static ControlTemplate StripButtonTemplate(bool isClose)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        if (isClose)
        {
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)), "Bd"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        }
        else
        {
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HoverBgBrush"), "Bd"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextPrimaryBrush")));
        }

        template.Triggers.Add(hover);
        return template;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
