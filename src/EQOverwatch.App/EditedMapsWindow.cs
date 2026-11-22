using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EQOverwatch.Core.Maps;

namespace EQOverwatch.App;

/// <summary>
/// Asks what to do with each Overwatch map file the player edited before an update: Keep Mine (the default), Overwrite with the
/// pack's file, or Merge (the pack's file plus the player's own labels). Replaced and merged files are backed up first.
/// </summary>
public sealed class EditedMapsWindow : Window
{
    /// <summary>A file and the player's choice for it.</summary>
    public sealed class Row : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private MapEditChoice _choice = MapEditChoice.KeepMine;

        public Row(string name) => Name = name;

        public string Name { get; }

        public MapEditChoice Choice
        {
            get => _choice;
            set => SetProperty(ref _choice, value);
        }
    }

    public static IReadOnlyList<KeyValuePair<MapEditChoice, string>> ChoiceLabels { get; } = new[]
    {
        new KeyValuePair<MapEditChoice, string>(MapEditChoice.KeepMine, "Keep Mine"),
        new KeyValuePair<MapEditChoice, string>(MapEditChoice.Overwrite, "Overwrite"),
        new KeyValuePair<MapEditChoice, string>(MapEditChoice.Merge, "Merge"),
    };

    private readonly List<Row> _rows;

    public EditedMapsWindow(IReadOnlyList<string> fileNames)
    {
        _rows = fileNames.Select(n => new Row(n)).ToList();
        Title = "Your edited map files";
        Width = 560;
        Height = 520;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBgBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = $"{_rows.Count} map file(s) in maps\\Overwatch differ from what the app installed, so they may hold your own labels. "
                + "Keep Mine leaves a file alone. Overwrite replaces it with the pack's file. Merge uses the pack's file and adds back "
                + "the labels you added. Replaced and merged files are backed up first.",
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");

        var grid = new DataGrid
        {
            ItemsSource = _rows,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "File", Binding = new Binding(nameof(Row.Name)), IsReadOnly = true, Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Choice",
            ItemsSource = ChoiceLabels,
            SelectedValuePath = "Key",
            DisplayMemberPath = "Value",
            SelectedValueBinding = new Binding(nameof(Row.Choice)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = DataGridLength.Auto,
        });

        var all = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach ((MapEditChoice choice, string label) in ChoiceLabels)
        {
            var button = new Button { Content = label + " (all)", Margin = new Thickness(0, 0, 8, 0) };
            button.SetResourceReference(StyleProperty, "SecondaryButton");
            button.Click += (_, _) =>
            {
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                foreach (Row row in _rows)
                {
                    row.Choice = choice;
                }
            };
            all.Children.Add(button);
        }

        var ok = new Button { Content = "Continue", IsDefault = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        ok.Click += (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        cancel.SetResourceReference(StyleProperty, "SecondaryButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(intro, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(all, Dock.Bottom);
        root.Children.Add(intro);
        root.Children.Add(buttons);
        root.Children.Add(all);
        root.Children.Add(grid);
        Content = root;
    }

    /// <summary>Each file's choice.</summary>
    public IReadOnlyDictionary<string, MapEditChoice> Choices =>
        _rows.ToDictionary(r => r.Name, r => r.Choice, StringComparer.OrdinalIgnoreCase);
}
