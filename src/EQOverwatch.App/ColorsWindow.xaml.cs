using System.Windows;
using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;

namespace EQOverwatch.App;

/// <summary>The colors of the Discord picture (attack types, pets and healing), with a preview drawn by the same code as the picture.</summary>
public partial class ColorsWindow : Window
{
    private readonly MainViewModel _viewModel;

    public ColorsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.ColorsChanged += RedrawPreview;
        Closed += (_, _) => viewModel.ColorsChanged -= RedrawPreview;
        Loaded += (_, _) => RedrawPreview();
    }

    private void RedrawPreview()
    {
        PreviewImage.Source = EncounterImageRenderer.RenderPreview(_viewModel.CurrentColors, _viewModel.ActivePalette, _viewModel.WindowTitle);
    }
}
