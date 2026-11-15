using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using EQOverwatch.App.ViewModels;
using Xunit;

namespace EQOverwatch.App.Tests;

/// <summary>The Experience by mob drop-downs, bound the way MainWindow binds them.</summary>
public partial class MainViewModelTests
{
    /// <summary>
    /// Regression: with the character drop-down bound, a new character in the store made the rebuild clear the list, the drop-down pushed
    /// its selection back, and every rebuild started another one until the stack overflowed (0xc00000fd, v1.0.53 to v1.0.55).
    /// </summary>
    [Fact]
    public void Experience_by_mob_drop_downs_follow_the_store_without_rebuilding_inside_a_rebuild()
    {
        using var h = new Harness();
        string kaelith = Path.Combine(h.Dir, "eqlog_Kaelith_frostreaver.txt");
        string borvik = Path.Combine(h.Dir, "eqlog_Borvik_frostreaver.txt");
        File.WriteAllLines(kaelith, new[]
        {
            "[Wed May 27 12:00:00 2026] You gain experience! (12.000%)",
            "[Wed May 27 12:00:00 2026] You have slain a werebat!",
            "[Wed May 27 12:02:00 2026] You have gained a level! Welcome to level 25!",
            "[Wed May 27 12:03:00 2026] You gain experience! (6.000%)",
            "[Wed May 27 12:03:00 2026] You have slain a ghoul!",
        });
        File.WriteAllLines(borvik, new[]
        {
            "[Wed May 27 12:00:00 2026] You gain experience! (2.000%)",
            "[Wed May 27 12:00:00 2026] You have slain a skeleton!",
        });
        MethodInfo rebuild = typeof(MainViewModel).GetMethod("RebuildMobExperience", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MainViewModel vm = h.Vm;
        int rebuildsStarted = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.MobExperienceRows))
            {
                rebuildsStarted++;
            }
        };

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                h.Pipeline.MobExperience.ScanLog(kaelith, "Kaelith", "frostreaver", 25);
                rebuild.Invoke(vm, new object[] { true });

                // Bound as in MainWindow.xaml (Experience tab).
                var characters = new ComboBox { DataContext = vm, DisplayMemberPath = "Label" };
                characters.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.MobExperienceCharacters)));
                characters.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedMobExperienceCharacter)) { Mode = BindingMode.TwoWay });
                var levels = new ComboBox { DataContext = vm };
                levels.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.MobExperienceLevels)));
                levels.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedMobExperienceLevel)) { Mode = BindingMode.TwoWay });
                Assert.Equal("Kaelith", (characters.SelectedItem as MobExperienceCharacterChoice)?.Character);

                // A second character appears ahead of the selected one: the selection holds, in the view model and the drop-down.
                levels.SelectedItem = "24";
                Assert.Equal(new[] { "a werebat" }, vm.MobExperienceRows.Select(r => r.Mob).ToArray());
                h.Pipeline.MobExperience.ScanLog(borvik, "Borvik", "frostreaver", 40);
                rebuildsStarted = 0;
                rebuild.Invoke(vm, new object[] { true });
                Assert.Equal(1, rebuildsStarted);
                Assert.Equal(new[] { "Borvik", "Kaelith" }, vm.MobExperienceCharacters.Select(c => c.Character).ToArray());
                Assert.Equal("Kaelith", vm.SelectedMobExperienceCharacter?.Character);
                Assert.Same(vm.SelectedMobExperienceCharacter, characters.SelectedItem);
                Assert.Equal("24", vm.SelectedMobExperienceLevel);
                Assert.Equal("24", levels.SelectedItem);

                // Picking the other character in the drop-down shows its table; the level it does not have falls back to All.
                characters.SelectedItem = vm.MobExperienceCharacters[0];
                Assert.Equal("Borvik", vm.SelectedMobExperienceCharacter?.Character);
                Assert.Equal(new[] { "All", "40" }, vm.MobExperienceLevels);
                Assert.Equal("All", vm.SelectedMobExperienceLevel);
                Assert.Equal("All", levels.SelectedItem);
                Assert.Equal(new[] { "a skeleton" }, vm.MobExperienceRows.Select(r => r.Mob).ToArray());

                // Everything cleared: the lists empty out and nothing is selected.
                h.Pipeline.MobExperience.Clear();
                rebuild.Invoke(vm, new object[] { true });
                Assert.Empty(vm.MobExperienceCharacters);
                Assert.Null(vm.SelectedMobExperienceCharacter);
                Assert.Null(characters.SelectedItem);
                Assert.Equal(new[] { "All" }, vm.MobExperienceLevels);
                Assert.Equal("All", vm.SelectedMobExperienceLevel);
                Assert.Empty(vm.MobExperienceRows);
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
    }
}
