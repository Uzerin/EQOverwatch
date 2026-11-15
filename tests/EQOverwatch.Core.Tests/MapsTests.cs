using System.Text;
using EQOverwatch.Core.Maps;
using Xunit;

namespace EQOverwatch.Core.Tests;

/// <summary>Map files, map packs, zone names and where each character is, for the Maps tab.</summary>
public sealed class MapsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqmaps-" + Guid.NewGuid().ToString("N"));

    public MapsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void A_map_file_is_read_and_saving_changes_only_its_labels()
    {
        string[] file =
        {
            "L 1040.0000, -1120.0000, 29.0010,  1100.0000, -1120.0000, 29.0010,  0, 0, 0",
            "P 444.0000, -2112.0000, -183.9365,  0, 0, 0,  3,  Clockwork_(Spells)",
            "L 0, 0, 0, 10, 10, 0, 255, 128, 0",
            "P 1.5, 2.5, 3,  240, 10, 20,  2,  Boss_spawn, east side",
        };

        MapLayer layer = MapLayer.Parse(file);

        Assert.Equal(2, layer.Lines.Count);
        Assert.Equal(new MapLine(0, 0, 0, 10, 10, 0, 255, 128, 0), layer.Lines[1]);
        Assert.Equal(2, layer.Labels.Count);
        Assert.Equal(new MapLabel(444, -2112, -183.9365, 0, 0, 0, 3, "Clockwork (Spells)"), layer.Labels[0]);
        Assert.Equal("Boss spawn, east side", layer.Labels[1].Text); // commas in the text are kept

        MapLayer changed = layer.WithLabels(new[] { layer.Labels[0] with { X = 450, Text = "Gnome spells" } });
        string[] saved = changed.ToFileText().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(new[] { file[0], file[2], "P 450.0000, -2112.0000, -183.9365,  0, 0, 0,  3,  Gnome_spells" }, saved);
        Assert.Equal(changed.Labels, MapLayer.Parse(saved).Labels);
    }

    [Fact]
    public void Map_coordinates_are_the_loc_numbers_with_their_signs_turned_round()
    {
        // /loc prints "Y, X, Z"; the map's x is minus that X and its y minus that Y.
        MapLabel label = MapLabel.AtLoc(1612.04, -1691.07, -80.71, 255, 255, 255, 2, "Here");

        Assert.Equal((1691.07, -1612.04), (label.X, label.Y));
        Assert.Equal((1612.04, -1691.07, -80.71), label.Loc);
    }

    [Fact]
    public void Packs_and_zones_are_listed_and_a_saved_layer_keeps_a_backup_of_the_old_file()
    {
        string maps = Path.Combine(_dir, "maps");
        string pack = Path.Combine(maps, "Dark Brewall");
        Directory.CreateDirectory(pack);
        Directory.CreateDirectory(Path.Combine(maps, "Empty"));
        File.WriteAllText(Path.Combine(pack, "letalis.txt"), "L 0, 0, 0, 10, 10, 0, 0, 0, 0\r\n");
        File.WriteAllText(Path.Combine(pack, "letalis_1.txt"), "P 5, 5, 0,  0, 0, 0,  2,  Old\r\n");
        File.WriteAllText(Path.Combine(pack, "akanon_3.txt"), string.Empty);
        File.WriteAllText(Path.Combine(maps, "qeynos.txt"), string.Empty);

        IReadOnlyList<MapPack> packs = MapFiles.Packs(_dir);
        Assert.Equal(new[] { "Dark Brewall", MapPack.RootLabel }, packs.Select(p => p.Label));
        Assert.Equal(new[] { "akanon", "letalis" }, MapFiles.Zones(pack));

        MapLayer[] layers = MapFiles.Load(pack, "letalis");
        Assert.Single(layers[0].Lines);
        Assert.Equal("Old", layers[1].Labels.Single().Text);
        Assert.True(layers[3].IsEmpty);

        var label = new MapLabel(-1020, 655, 14, 133, 183, 235, 2, "Camp spot");
        MapFiles.Save(pack, "letalis", 1, layers[1].WithLabels(new[] { label }));
        MapFiles.Save(pack, "letalis", 3, MapLayer.Empty.WithLabels(new[] { label })); // a new file: nothing to back up

        Assert.Equal(label, MapFiles.Load(pack, "letalis")[1].Labels.Single());
        Assert.Contains("Old", File.ReadAllText(Path.Combine(pack, "letalis_1.txt.bak")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(pack, "letalis_3.txt.bak")));
        Assert.Equal(label, MapFiles.Load(pack, "letalis")[3].Labels.Single());

        // A second save keeps the first backup (the file as it was before the app touched it).
        MapFiles.Save(pack, "letalis", 1, MapLayer.Empty);
        Assert.Contains("Old", File.ReadAllText(Path.Combine(pack, "letalis_1.txt.bak")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_characters_map_pack_comes_from_its_ui_file()
    {
        File.WriteAllText(Path.Combine(_dir, "UI_Ream_frostreaver_CLR.ini"), "[MapViewWnd]\r\nShow=0\r\n[Other]\r\nMapSkin=Dark Brewall\r\n");

        Assert.Equal("Dark Brewall", MapFiles.CharacterPack(_dir, "Ream", "frostreaver"));
        Assert.Null(MapFiles.CharacterPack(_dir, "Nobody", "frostreaver"));
    }

    [Fact]
    public void Zone_names_and_expansions_come_from_eqs_zone_names_file()
    {
        ZoneDirectory zones = ZoneDirectory.Parse(new[]
        {
            "55^Ak'Anon^0^0^1^1",
            "112^Velketor's Labyrinth^25^0^3^3",
            "169^Mons Letalis^20^60^4^4",
            "202^The Plane of Knowledge^0^0^5^5",
            "828^Velketor's Labyrinth^20^60^27^27",
            "837^Winter^0^0^^28",
            "999999^Not a zone",
        });

        Assert.Equal(new ZoneInfo(169, "Mons Letalis", "letalis", 4), zones.ByShortName("letalis"));
        Assert.Equal("poknowledge", zones.ByName("Plane of Knowledge").Single().ShortName); // "The" is ignored
        Assert.Equal(new[] { "velketor", "velketortwo" }, zones.ByName("Velketor's Labyrinth").Select(z => z.ShortName));
        Assert.Equal(27, zones.ByShortName("velketortwo")!.Expansion);
        Assert.Equal("The Scars of Velious", ZoneDirectory.ExpansionName(3));
        Assert.Equal("Other", ZoneDirectory.ExpansionName(0));
        Assert.Equal("Expansion 40", ZoneDirectory.ExpansionName(40));
        Assert.Equal(33, ZoneDirectory.ExpansionNames.Count);
    }

    [Fact]
    public void Every_zone_short_name_is_unique()
    {
        Assert.Equal(ZoneShortNames.ById.Count, ZoneShortNames.ById.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_characters_zone_and_last_loc_follow_its_log()
    {
        var tracker = new ZoneTracker();
        DateTime now = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);

        tracker.OnLine("Ream", "frostreaver", "Your Location is 1, 2, 3", now); // no zone yet: nothing to attach it to
        Assert.Null(tracker.Get("Ream", "frostreaver"));

        tracker.OnLine("Ream", "frostreaver", "You have entered Mons Letalis.", now);
        tracker.OnLine("Ream", "frostreaver", "You have entered an area where levitation effects do not function.", now);
        tracker.OnLine("Ream", "frostreaver", "Your Location is 1588.55, 833.57, 3.17", now);

        Assert.Equal(new CharacterWhereabouts("Mons Letalis", (1588.55, 833.57, 3.17)), tracker.Get("Ream", "frostreaver"));

        tracker.OnLine("Ream", "frostreaver", "You have entered The Plane of Knowledge.", now);
        Assert.Equal(new CharacterWhereabouts("The Plane of Knowledge", null), tracker.Get("Ream", "frostreaver")); // a new zone forgets the old /loc
    }

    [Fact]
    public void Where_a_character_was_is_read_from_the_end_of_a_long_log()
    {
        string path = Path.Combine(_dir, "eqlog_Ream_frostreaver.txt");
        var text = new StringBuilder();
        text.Append("[Sat May 30 12:18:14 2026] You have entered West Freeport.\n");
        text.Append("[Sat May 30 12:24:53 2026] Your Location is 1612.04, -1691.07, -80.71\n");
        text.Append("[Sat May 30 12:30:00 2026] You have entered Mons Letalis.\n");
        text.Append("[Sat May 30 12:31:00 2026] Your Location is 10.00, 20.00, 30.00\n");
        text.Append("[Sat May 30 12:31:30 2026] Your Location is -655.00, 1020.00, 14.00\n");
        string filler = "[Sat May 30 12:32:00 2026] You hit a goblin for 12 points of damage.\n";
        while (text.Length < 2_600_000) // more than two of the 1 MB pieces it reads at a time
        {
            text.Append(filler);
        }

        File.WriteAllText(path, text.ToString());

        Assert.Equal(new CharacterWhereabouts("Mons Letalis", (-655, 1020, 14)), ZoneTracker.FindInLog(path));

        var tracker = new ZoneTracker();
        tracker.OnLine("Ream", "frostreaver", "You have entered The Bazaar.", DateTime.UtcNow);
        tracker.ReadFromLog(path, "Ream", "frostreaver"); // a live line already said where Ream is
        Assert.Equal("The Bazaar", tracker.Get("Ream", "frostreaver")!.Zone);
    }
}
