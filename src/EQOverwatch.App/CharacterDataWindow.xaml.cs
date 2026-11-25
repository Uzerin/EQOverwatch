using System.Windows;
using EQOverwatch.App.ViewModels;

namespace EQOverwatch.App;

/// <summary>One character's inventory, spellbook, missing spells and key ring, read from the files <c>/outputfile</c> wrote.</summary>
public partial class CharacterDataWindow : Window
{
    public CharacterDataWindow(CharacterDataViewModel viewModel)
    {
        InitializeComponent();
        Interop.DarkWindowChrome.Apply(this); // theme-colored top strip instead of the white title bar
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public CharacterDataViewModel ViewModel { get; }

    private void OnAddSocials(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, ViewModel.AddOverwatchSocials(), "Overwatch Data socials", MessageBoxButton.OK, MessageBoxImage.Information);
}
