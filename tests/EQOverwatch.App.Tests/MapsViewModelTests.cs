using System.IO;
using EQOverwatch.App.Services;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Maps;
using EQOverwatch.Core.Models;
using Xunit;

namespace EQOverwatch.App.Tests;

/// <summary>The Maps tab: picking zones, packs and layers, and adding, moving, editing and saving labels.</summary>
public sealed class MapsViewModelTests : IDisposable
{
    private readonly string _eq = Path.Combine(Path.GetTempPath(), "eqmapsvm-" + Guid.NewGuid().ToString("N"));
    private readonly string _pack;
    private readonly ZoneTracker _zones = new();
    private readonly AskingDialogs _dialogs = new();
    private readonly List<WatchedLogConfig> _logs = new();

    public MapsViewModelTests()
    {
        _pack = Path.Combine(_eq, "maps", "Dark Brewall");
        Directory.CreateDirectory(_pack);
        Directory.CreateDirectory(Path.Combine(_eq, "maps", "Claude"));
        Directory.CreateDirectory(Path.Combine(_eq, "Resources"));
        Directory.CreateDirectory(Path.Combine(_eq, "Logs"));
        File.WriteAllLines(Path.Combine(_eq, "Resources", "ZoneNames.txt"), new[]
        {
            "55^Ak'Anon^0^0^1^1",
            "112^Velketor's Labyrinth^25^0^3^3",
            "169^Mons Letalis^20^60^4^4",
            "828^Velketor's Labyrinth^20^60^27^27",
        });
        File.WriteAllText(Path.Combine(_pack, "letalis.txt"), "L 0, 0, 0, 100, 100, 0, 0, 0, 0\r\nP 50, 50, 0,  0, 0, 0,  2,  Gate\r\n");
        File.WriteAllText(Path.Combine(_pack, "letalis_3.txt"), "P -1020, 655, 14,  240, 75, 74,  2,  Boss_spawn\r\n");
        File.WriteAllText(Path.Combine(_pack, "akanon.txt"), "L 0, 0, 0, 10, 10, 0, 0, 0, 0\r\n");
        File.WriteAllText(Path.Combine(_pack, "velketor.txt"), "L 0, 0, 0, 10, 10, 0, 0, 0, 0\r\n");
        File.WriteAllText(Path.Combine(_pack, "velketortwo.txt"), "L 0, 0, 0, 10, 10, 0, 0, 0, 0\r\n");
        File.WriteAllText(Path.Combine(_eq, "maps", "Claude", "akanon.txt"), "L 0, 0, 0, 10, 10, 0, 0, 0, 0\r\n");
        File.WriteAllText(Path.Combine(_eq, "UI_Ream_frostreaver_CLR.ini"), "MapSkin=Dark Brewall\r\n");

        string log = Path.Combine(_eq, "Logs", "eqlog_Ream_frostreaver.txt");
        File.WriteAllText(log, string.Empty);
        _logs.Add(new WatchedLogConfig(log));
    }

    public void Dispose()
    {
        if (Directory.Exists(_eq))
        {
            Directory.Delete(_eq, recursive: true);
        }
    }

    private MapsViewModel Create()
    {
        var vm = new MapsViewModel(() => _eq, _zones, () => _logs, _dialogs);
        vm.Initialize(_logs[0]);
        return vm;
    }

    private void Enter(string zone, string? loc = null)
    {
        _zones.OnLine("Ream", "frostreaver", $"You have entered {zone}.", DateTime.UtcNow);
        if (loc is not null)
        {
            _zones.OnLine("Ream", "frostreaver", $"Your Location is {loc}", DateTime.UtcNow);
        }
    }

    [Fact]
    public void Current_zone_follows_the_primary_dps_character_and_opens_on_layer_3_of_its_pack()
    {
        MapsViewModel vm = Create();
        Assert.Equal("Dark Brewall", vm.SelectedPack!.Name); // the pack Ream uses in game
        Assert.Null(vm.ShownZone);
        Assert.Contains("zone not known yet", vm.CurrentZoneText, StringComparison.Ordinal);

        Enter("Mons Letalis", "-655.00, 1020.00, 14.00");
        vm.Refresh(_logs[0]);

        Assert.Equal("letalis", vm.ShownZone);
        Assert.Equal("Ream is in Mons Letalis", vm.CurrentZoneText);
        Assert.Equal(3, vm.SelectedLayer);
        Assert.Equal("Boss spawn", vm.Labels.Single().Text);
        Assert.Equal("letalis_3.txt", vm.LayerFileText);
        Assert.Equal(new MapPlayerSpot("Ream", -1020.0, 655.0), Assert.Single(vm.PlayerSpots)); // the /loc marker, with the name

        vm.SelectedLayer = 0;
        Assert.Equal("Gate", vm.Labels.Single().Text);

        vm.SelectedLayer = 2;
        Assert.Empty(vm.Labels);
        Assert.Equal("letalis_2.txt (new file)", vm.LayerFileText);
    }

    [Fact]
    public void A_zone_name_with_two_maps_lets_you_pick_which_one()
    {
        MapsViewModel vm = Create();
        Enter("Velketor's Labyrinth");
        vm.Refresh(_logs[0]);

        Assert.True(vm.HasCurrentChoices);
        Assert.Equal("velketor", vm.ShownZone);
        vm.SelectedCurrentChoice = vm.CurrentChoices.Single(c => c.ShortName == "velketortwo");
        Assert.Equal("velketortwo", vm.ShownZone);
        Assert.Equal("Velketor's Labyrinth (Torment of Velious)", vm.SelectedCurrentChoice!.Label);
    }

    [Fact]
    public void Select_zone_lists_zones_by_expansion_alphabetically()
    {
        MapsViewModel vm = Create();
        vm.IsSelectZoneTab = true;

        Assert.Equal("All", vm.Expansions[0].Label);
        Assert.Equal("The Scars of Velious", vm.Expansions[3].Label);
        Assert.Equal("Other", vm.Expansions[^1].Label);
        Assert.Equal(
            new[] { "Ak'Anon", "Mons Letalis", "Velketor's Labyrinth (The Scars of Velious)", "Velketor's Labyrinth (Torment of Velious)" },
            vm.ZoneChoices.Select(z => z.Label));
        Assert.Equal("akanon", vm.ShownZone);

        vm.SelectedExpansion = vm.Expansions.Single(e => e.Label == "The Shadows of Luclin");
        Assert.Equal("letalis", vm.ZoneChoices.Single().ShortName);
        Assert.Equal("letalis", vm.ShownZone);

        vm.SelectedPack = vm.Packs.Single(p => p.Name == "Claude");
        Assert.Empty(vm.ZoneChoices); // that pack has no Luclin maps
        Assert.Null(vm.ShownZone);
    }

    [Fact]
    public void Labels_are_added_moved_edited_deleted_and_saved_to_the_layer_file()
    {
        MapsViewModel vm = Create();
        Enter("Mons Letalis", "-655.00, 1020.00, 14.00");
        vm.Refresh(_logs[0]);

        vm.AddLabelCommand.Execute(null);
        Assert.Equal("Enter the label text first.", vm.ErrorText);
        vm.EditText = "Camp spot";
        vm.AddLabelCommand.Execute(null);
        Assert.Equal("Click the map or enter a /loc first.", vm.ErrorText);

        vm.UseMyLocCommand.Execute(null);
        Assert.Equal("-655, 1020, 14", vm.EditLoc);
        Assert.Equal((-1020.0, 655.0), vm.Pending);
        vm.EditColorHex = "#5DCAA5";
        vm.AddLabelCommand.Execute(null);
        Assert.True(vm.IsDirty);
        Assert.Equal(2, vm.Labels.Count);

        // A click on the map places the next one; a drag moves an existing one.
        vm.PlaceAt(10.4, 20.6);
        Assert.Equal("-21, -10, 0", vm.EditLoc);
        MapLabelItem boss = vm.Labels[0];
        vm.SelectLabelCommand.Execute(boss);
        Assert.Equal("Editing: Boss spawn", vm.FormTitle);
        vm.MoveLabel(boss, -900.2, 600.7);
        Assert.Equal("-601, 900, 14", vm.EditLoc);
        vm.EditText = "Named";
        vm.EditSize = vm.SizeChoices[2];
        vm.DeleteLabelCommand.Execute(vm.Labels[1]);

        vm.SaveCommand.Execute(null);
        Assert.False(vm.IsDirty);
        Assert.StartsWith("Saved 1 label to maps\\Dark Brewall\\letalis_3.txt.", vm.StatusLine, StringComparison.Ordinal);
        Assert.Equal(new MapLabel(-900, 601, 14, 240, 75, 74, 3, "Named"), MapFiles.Load(_pack, "letalis")[3].Labels.Single());
        Assert.True(File.Exists(Path.Combine(_pack, "letalis_3.txt.bak")));
        Assert.Equal("L 0, 0, 0, 100, 100, 0, 0, 0, 0", File.ReadAllLines(Path.Combine(_pack, "letalis.txt"))[0]); // other files untouched
    }

    [Fact]
    public void Leaving_a_layer_with_unsaved_changes_asks_first()
    {
        MapsViewModel vm = Create();
        Enter("Mons Letalis");
        vm.Refresh(_logs[0]);
        vm.MoveLabel(vm.Labels[0], 1, 2);

        _dialogs.Answer = UnsavedChoice.Cancel;
        vm.SelectedLayer = 1;
        Assert.Equal(3, vm.SelectedLayer); // stayed
        Assert.True(vm.IsDirty);
        Assert.Equal(1, _dialogs.Asked);

        _dialogs.Answer = UnsavedChoice.Discard;
        vm.IsSelectZoneTab = true;
        Assert.True(vm.IsSelectZoneTab);
        Assert.False(vm.IsDirty);
        Assert.Equal(-1020, MapFiles.Load(_pack, "letalis")[3].Labels.Single().X); // nothing written

        vm.IsCurrentZoneTab = true;
        Assert.Equal(-1020, vm.Labels.Single().X); // shown as the file is
        vm.MoveLabel(vm.Labels[0], 5, 6);
        _dialogs.Answer = UnsavedChoice.Save;
        Assert.True(vm.ConfirmClose());
        Assert.Equal(5, MapFiles.Load(_pack, "letalis")[3].Labels.Single().X);
    }

    [Fact]
    public void A_character_moving_on_with_unsaved_changes_keeps_the_map_until_they_are_saved()
    {
        MapsViewModel vm = Create();
        Enter("Mons Letalis");
        vm.Refresh(_logs[0]);
        vm.MoveLabel(vm.Labels[0], 1, 2);

        Enter("Ak'Anon");
        vm.Refresh(_logs[0]);
        Assert.Equal("letalis", vm.ShownZone);
        Assert.NotEmpty(vm.FollowNote);

        vm.SaveCommand.Execute(null);
        Assert.Equal("akanon", vm.ShownZone);
        Assert.Empty(vm.FollowNote);
    }

    private sealed class AskingDialogs : IDialogService
    {
        public UnsavedChoice Answer { get; set; } = UnsavedChoice.Cancel;

        public int Asked { get; private set; }

        public string[]? PickLogFiles() => null;

        public string? PickImportLogFile() => null;

        public string? PickWavFile() => null;

        public string? PickSpellsFile() => null;

        public string? PickEqFolder() => null;

        public UnsavedChoice AskUnsavedChanges(string title, string message)
        {
            Asked++;
            return Answer;
        }
    }
}
