using System.Windows;
using System.Windows.Threading;
using EQOverwatch.App.Interop;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

/// <summary>The main, Colors and Character data windows open where they were last time.</summary>
public partial class MainViewModelTests
{
    private static readonly WindowPlacementSetting Spot = new() { Left = 120, Top = 90, Right = 820, Bottom = 640 };

    [Fact]
    public void Window_placements_are_saved_with_the_settings_and_restored_on_the_next_start()
    {
        using (var h = new Harness())
        {
            Assert.Null(h.Vm.GetWindowPlacement(WindowPlacementSetting.Main)); // first run: opens centered
            h.Vm.SetWindowPlacement(WindowPlacementSetting.Main, Spot with { IsMaximized = true });
            h.Vm.SetWindowPlacement(WindowPlacementSetting.Colors, Spot);
            h.Vm.SetWindowPlacement(WindowPlacementSetting.CharacterData, new WindowPlacementSetting()); // empty: ignored

            AppSettings saved = h.Saved();
            Assert.Equal(Spot with { IsMaximized = true }, saved.WindowPlacements[WindowPlacementSetting.Main]);
            Assert.Equal(Spot, saved.WindowPlacements[WindowPlacementSetting.Colors]);
            Assert.False(saved.WindowPlacements.ContainsKey(WindowPlacementSetting.CharacterData));
        }

        using var next = new Harness((settings, _) =>
            settings.WindowPlacements[WindowPlacementSetting.Main] = Spot with { IsMaximized = true });
        Assert.Equal(Spot with { IsMaximized = true }, next.Vm.GetWindowPlacement(WindowPlacementSetting.Main));
    }

    [Fact]
    public void A_real_window_reopens_where_it_was_left()
    {
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            return; // build machines may have no desktop to show a window on
        }

        Exception? failure = null;
        WindowPlacementSetting? stored = null;
        WindowPlacementSetting? reopened = null;
        WindowPlacementSetting? maximized = null;
        WindowState maximizedState = WindowState.Normal;
        var thread = new Thread(() =>
        {
            try
            {
                // 1. A window that was never placed is moved and resized, then closed: its spot is kept.
                var first = new Window { Width = 400, Height = 300, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                WindowPlacementKeeper.Attach(first, () => null, p => stored = p);
                first.Show();
                first.Left = 150;
                first.Top = 110;
                first.Width = 520;
                first.Height = 380;
                Pump();
                first.Close();

                // 2. A new window given that spot opens there, not centered.
                var second = new Window { Width = 400, Height = 300, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                WindowPlacementKeeper.Attach(second, () => stored, p => reopened = p);
                second.Show();
                Pump();
                second.Close();

                // 3. One saved as maximized opens maximized, and keeps its normal size for when it is restored.
                var third = new Window { Width = 400, Height = 300, ShowActivated = false, ShowInTaskbar = false };
                WindowPlacementKeeper.Attach(third, () => stored! with { IsMaximized = true }, p => maximized = p);
                third.Show();
                Pump();
                maximizedState = third.WindowState;
                third.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.NotNull(stored);
        Assert.False(stored!.IsMaximized);
        Assert.True(stored.Width > 400 && stored.Height > 300, $"stored {stored}");
        Assert.Equal(stored, reopened);
        Assert.Equal(WindowState.Maximized, maximizedState);
        Assert.Equal(stored with { IsMaximized = true }, maximized);

        static void Pump()
        {
            for (int i = 0; i < 5; i++)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
        }
    }
}
