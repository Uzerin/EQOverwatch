using System.Windows;
using EQOverwatch.App.ViewModels;

namespace EQOverwatch.App;

/// <summary>One character's inventory, spellbook, missing spells and key ring, read from the files <c>/outputfile</c> wrote.</summary>
public partial class CharacterDataWindow : Window
{
    public CharacterDataWindow(CharacterDataViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public CharacterDataViewModel ViewModel { get; }
}
