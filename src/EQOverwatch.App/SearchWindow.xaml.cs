using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using EQOverwatch.App.ViewModels;

namespace EQOverwatch.App;

/// <summary>Ctrl+F: finds characters, items, mobs and fights, and triggers anywhere in the app, and opens the one picked.</summary>
public partial class SearchWindow : Window
{
    private readonly MainViewModel _viewModel;

    public SearchWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Interop.DarkWindowChrome.Apply(this, resizable: false);
        Loaded += (_, _) => Query.Focus();
    }

    private void Query_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        IReadOnlyList<SearchHit> hits = _viewModel.SearchEverything(Query.Text);
        var view = new ListCollectionView(hits.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SearchHit.Kind)));
        Results.ItemsSource = view;
        if (hits.Count > 0)
        {
            Results.SelectedIndex = 0;
        }

        Status.Text = Query.Text.Trim().Length < 2
            ? "Type at least two letters: characters, items, mobs and fights, triggers, spells, zones."
            : hits.Count == 0 ? "Nothing found." : $"{hits.Count} found. Enter or double-click to open.";
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            OpenSelected();
        }
        else if (e.Key == Key.Down && Results.Items.Count > 0)
        {
            Results.SelectedIndex = Math.Min(Results.Items.Count - 1, Results.SelectedIndex + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && Results.Items.Count > 0)
        {
            Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 1);
            e.Handled = true;
        }
    }

    private void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelected();
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Results.SelectedItem is SearchHit hit)
        {
            Close();
            hit.Open();
        }
    }
}
