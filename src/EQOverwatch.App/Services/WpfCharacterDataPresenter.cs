using System.Windows;
using EQOverwatch.App.ViewModels;

namespace EQOverwatch.App.Services;

/// <summary>Opens one data window per character (a second click reuses it) and shows the "no data yet" instructions.</summary>
public sealed class WpfCharacterDataPresenter : ICharacterDataPresenter
{
    private readonly Dictionary<string, CharacterDataWindow> _open = new(StringComparer.OrdinalIgnoreCase);

    public void Show(CharacterDataViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        if (_open.TryGetValue(viewModel.Key, out CharacterDataWindow? existing))
        {
            existing.ViewModel.Refresh();
            existing.ViewModel.SelectedTabIndex = viewModel.SelectedTabIndex;
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        var window = new CharacterDataWindow(viewModel) { Owner = Application.Current?.MainWindow };
        string key = viewModel.Key;
        window.Closed += (_, _) => _open.Remove(key);
        _open[key] = window;
        window.Show();
    }

    public void ShowMessage(string title, string message)
    {
        Window? owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
