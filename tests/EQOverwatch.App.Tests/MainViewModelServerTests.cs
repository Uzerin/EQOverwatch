using System.IO;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Pipeline;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

/// <summary>Levels, classes and additional characters are kept per server.</summary>
public partial class MainViewModelTests
{
    private static string LogOn(Harness h, string character, string server)
    {
        string path = Path.Combine(h.Dir, $"eqlog_{character}_{server}.txt");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static void Who(Harness h, string server, string line) =>
        ((ITriggerLineSink)h.Pipeline.Roster).OnLine("Someone", server, line, DateTime.UtcNow);

    [Fact]
    public void The_characters_tab_shows_each_characters_level_from_its_own_server()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { LogOn(h, "Kaelith", "frostreaver"), LogOn(h, "Kaelith", "newserver") };
        h.Vm.AddLogCommand.Execute(null);

        Who(h, "frostreaver", "[125 Warlord (Cleric)] Kaelith (Human)");
        Who(h, "newserver", "[12 Acolyte (Shaman)] Kaelith (Troll)");
        h.Vm.Refresh();

        Assert.Equal(("125", "Cleric"), Level(h, "frostreaver"));
        Assert.Equal(("12", "Shaman"), Level(h, "newserver"));

        // A later /who on the new server changes only that row.
        Who(h, "newserver", "[14 Acolyte (Shaman)] Kaelith (Troll)");
        h.Vm.Refresh();
        Assert.Equal(("125", "Cleric"), Level(h, "frostreaver"));
        Assert.Equal(("14", "Shaman"), Level(h, "newserver"));

        static (string, string) Level(Harness h, string server)
        {
            var log = h.Vm.Logs.Single(l => l.ServerName == server);
            return (log.LevelText, log.ClassText);
        }
    }

    [Fact]
    public void Additional_characters_have_a_server_that_can_be_changed()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { LogOn(h, "Kaelith", "frostreaver"), LogOn(h, "Borvik", "frostreaver"), LogOn(h, "Newbie", "newserver") };
        h.Vm.AddLogCommand.Execute(null);
        Assert.Equal(new[] { "frostreaver", "newserver" }, h.Vm.ServerChoices);
        Assert.Equal("frostreaver", h.Vm.NewAdditionalCharacterServer); // the server most logs are on

        h.Vm.NewAdditionalCharacter = "Thalra";
        h.Vm.NewAdditionalCharacterServer = "newserver";
        h.Vm.AddAdditionalCharacterCommand.Execute(null);
        h.Vm.NewAdditionalCharacter = "Thalra";
        h.Vm.NewAdditionalCharacterServer = "frostreaver";
        h.Vm.AddAdditionalCharacterCommand.Execute(null); // the same name on another server is another character
        Assert.Equal(2, h.Vm.AdditionalCharacterRows.Count);

        Who(h, "frostreaver", "[125 Oracle (Druid)] Thalra (Wood Elf)");
        Who(h, "newserver", "[8 Mystic (Shaman)] Thalra (Troll)");
        h.Vm.Refresh();
        CharacterInfoRow onNew = h.Vm.AdditionalCharacterRows.Single(r => r.Server == "newserver");
        CharacterInfoRow onFrost = h.Vm.AdditionalCharacterRows.Single(r => r.Server == "frostreaver");
        Assert.Equal(("8", "Shaman"), (onNew.LevelText, onNew.ClassText));
        Assert.Equal(("125", "Druid"), (onFrost.LevelText, onFrost.ClassText));

        // Moving a row onto a server where the name is already listed is refused.
        onNew.Server = "frostreaver";
        Assert.Equal("newserver", onNew.Server);
        Assert.Contains("already in the list", h.Vm.AdditionalCharacterStatus);

        // Removing one and moving the other: its level now comes from the new server.
        h.Vm.RemoveAdditionalCharacterCommand.Execute(onFrost);
        onNew.Server = "frostreaver";
        Assert.Equal(("125", "Druid"), (onNew.LevelText, onNew.ClassText));

        AppSettings saved = h.Saved();
        AdditionalCharacterSetting entry = Assert.Single(saved.AdditionalCharacterServers);
        Assert.Equal(("Thalra", "frostreaver"), (entry.Name, entry.Server));
        Assert.Equal(new[] { "Thalra" }, saved.AdditionalMonitoredCharacters);
    }

    [Fact]
    public void Older_settings_without_servers_are_filed_under_the_server_of_the_logs()
    {
        using var h = new Harness((settings, dir) =>
        {
            string frost = Path.Combine(dir, "eqlog_Kaelith_frostreaver.txt");
            string other = Path.Combine(dir, "eqlog_Newbie_newserver.txt");
            string frost2 = Path.Combine(dir, "eqlog_Borvik_frostreaver.txt");
            foreach (string path in new[] { frost, other, frost2 })
            {
                File.WriteAllText(path, string.Empty);
                settings.WatchedLogs.Add(new WatchedLogSettings { FilePath = path });
            }

            settings.AdditionalMonitoredCharacters = new List<string> { "Oodles" };
            settings.Roster = new RosterSettings
            {
                Players =
                {
                    new RosterPlayerEntry { Name = "Kaelith", Class = "Cleric", Level = 125 },
                    new RosterPlayerEntry { Name = "Newbie", Class = "Shaman", Level = 5 },
                    new RosterPlayerEntry { Name = "Oodles", Class = "Magician", Level = 124 },
                },
            };
        });

        CharacterInfoRow row = Assert.Single(h.Vm.AdditionalCharacterRows);
        Assert.Equal(("Oodles", "frostreaver", "124"), (row.Name, row.Server, row.LevelText));
        Assert.Equal("125", h.Vm.Logs.Single(l => l.CharacterName == "Kaelith").LevelText);
        Assert.Equal("5", h.Vm.Logs.Single(l => l.CharacterName == "Newbie").LevelText); // filed under its own log's server
        Assert.Equal(5, h.Pipeline.Roster.GetLevel("Newbie", "newserver"));
        Assert.Equal(0, h.Pipeline.Roster.GetLevel("Kaelith", "newserver"));
    }
}
