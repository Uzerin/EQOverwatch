using System.Threading;
using System.Windows;
using System.Windows.Controls;
using EQOverwatch.App.Controls;
using EQOverwatch.App.Presentation;
using Xunit;

namespace EQOverwatch.App.Tests;

public sealed class LayoutCustomizerTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
    }

    [Fact]
    public void A_tab_with_a_badge_is_named_by_its_text()
    {
        OnSta(() =>
        {
            var header = new StackPanel { Tag = "Loot" };
            header.Children.Add(new TextBlock { Text = "Loot" });
            header.Children.Add(new TextBlock { Text = "3" });
            Assert.Equal("Loot", LayoutCustomizer.TitleOf(new TabItem { Header = header }));
            Assert.Equal("Raid", LayoutCustomizer.TitleOf(new TabItem { Header = "Raid" }));
        });
    }

    [Fact]
    public void Tabs_are_laid_out_in_the_order_the_player_dragged_them_to()
    {
        OnSta(() =>
        {
            var panel = new StaticTabPanel { Columns = 6 };
            var a = new Border { Width = 50, Height = 20 };
            var b = new Border { Width = 50, Height = 20 };
            var c = new Border { Width = 50, Height = 20 };
            panel.Children.Add(a);
            panel.Children.Add(b);
            panel.Children.Add(c);
            LayoutCustomizer.SetTabOrder(c, 0);
            LayoutCustomizer.SetTabOrder(a, 1);
            LayoutCustomizer.SetTabOrder(b, 2);
            b.Visibility = Visibility.Collapsed;
            panel.Measure(new Size(1000, 100));
            panel.Arrange(new Rect(0, 0, 1000, 100));
            Assert.Equal(0, c.TranslatePoint(new Point(), panel).X);
            Assert.Equal(50, a.TranslatePoint(new Point(), panel).X);
        });
    }

    [Fact]
    public void A_gap_between_two_panels_in_a_tab_gets_a_divider()
    {
        OnSta(() =>
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new DataGrid();
            var right = new DataGrid();
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(right);
            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Encounter DPS", Content = grid });
            var window = new Window { Content = tabs, Width = 800, Height = 600, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -3000 };
            EQOverwatch.App.Interop.DarkWindowChrome.Apply(window);
            window.Show();
            var store = new EQOverwatch.Core.Settings.UiLayoutStore();
            LayoutCustomizer.Attach(window, store, _ => false, _ => null, (_, _) => { });
            window.Close();
            Assert.Single(grid.Children.OfType<GridSplitter>());
        });
    }
}
