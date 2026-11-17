using System.IO;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Updates;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    // ───────────────────────────── installing an update ─────────────────────────────

    private sealed class FakeInstaller(string newExeText) : IUpdateInstaller
    {
        public Task<string> PrepareAsync(UpdateAssets assets, string stagingDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(stagingDirectory);
            string exe = Path.Combine(stagingDirectory, UpdateInstaller.ExeName);
            File.WriteAllText(exe, newExeText);
            return Task.FromResult(exe);
        }
    }

    [Fact]
    public async Task Installing_an_update_closes_everything_before_the_program_file_is_replaced_then_restarts()
    {
        using var h = new Harness();
        string exe = Path.Combine(h.Dir, UpdateInstaller.ExeName);
        File.WriteAllText(exe, "old");
        h.Updates.Result = Newer() with { Assets = new UpdateAssets("EQOverwatch.zip", "https://example.invalid/z", "https://example.invalid/h") };
        await h.Vm.StartUpdateCheckAsync();

        string? exeWhileClosing = null;
        h.Audio.Disposing = () => exeWhileClosing = File.ReadAllText(exe); // closing loads parts of the program from the old file
        (string Exe, bool Installed, string Text)? restarted = null;
        h.Vm.ConfigureUpdateInstall(new FakeInstaller("new"), exe, (path, installed) => restarted = (path, installed, File.ReadAllText(path)));
        h.Dialogs.ConfirmAnswer = true;

        await h.Vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal("old", exeWhileClosing);
        Assert.NotNull(restarted);
        Assert.Equal(exe, restarted.Value.Exe);
        Assert.True(restarted.Value.Installed);
        Assert.Equal("new", restarted.Value.Text);
        Assert.Equal("old", File.ReadAllText(UpdateSwapper.BackupPath(exe)));
        Assert.Null(h.Vm.UpdateShutdownError);
    }

    [Fact]
    public void A_copy_reopened_after_a_failed_update_says_so()
    {
        using var h = new Harness();
        h.Vm.ReportFailedUpdate();
        Assert.Contains("couldn't be installed", h.Vm.UpdateStatus);
    }

    // ───────────────────────────── every log ─────────────────────────────

    private const string GnomeFight = "[Wed Oct 21 20:00:00 2026] You slash a gnome for 100 points of damage.";

    [Fact]
    public async Task The_first_open_offers_once_to_load_every_log_and_a_no_is_remembered()
    {
        string dir;
        {
            using var h = new Harness((s, d) =>
            {
                string path = Path.Combine(d, "eqlog_Stackprime_Frostreaver.txt");
                File.WriteAllText(path, GnomeFight + "\n");
                s.WatchedLogs.Add(new Core.Settings.WatchedLogSettings { FilePath = path, CharacterName = "Stackprime" });
            });
            await h.Vm.StartSessionAsync();

            Assert.Equal(new[] { "Load fights from all your logs?" }, h.Dialogs.Questions);
            Assert.Contains("Reload All Logs", h.Vm.ImportStatus);
            Assert.True(h.Saved().AllLogsLoadOffered);
            dir = h.Dir;
        }

        using var again = new Harness((s, _) => s.AllLogsLoadOffered = true);
        await again.Vm.StartSessionAsync();
        Assert.Empty(again.Dialogs.Questions);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Saying_yes_loads_every_logs_whole_history_counting_a_shared_hit_once()
    {
        using var h = new Harness();
        h.Dialogs.ConfirmAnswer = true;
        h.Dialogs.LogFiles = new[]
        {
            h.CreateLog("Stackprime", GnomeFight, "[Wed Oct 21 20:00:05 2026] A gnome has been slain by Stackprime!"),
            h.CreateLog("Oodles", "[Wed Oct 21 20:00:01 2026] Stackprime slashes a gnome for 100 points of damage.",
                "[Wed Oct 21 20:00:05 2026] A gnome has been slain by Stackprime!"),
        };
        h.Vm.AddLogCommand.Execute(null); // a new install's first logs

        await h.Vm.AutoImportTask;

        Assert.Single(h.Dialogs.Questions);
        Core.Models.EncounterSnapshot fight = Assert.Single(h.Pipeline.HistoricalDps.Snapshot.Completed);
        Assert.Equal(100, fight.TotalDamage);
        Assert.Contains("2 logs", h.Vm.ImportStatus);
    }

    [Fact]
    public async Task The_dps_tab_shows_the_fights_of_the_server_chosen()
    {
        using var h = new Harness();
        string teek = Path.Combine(h.Dir, "eqlog_Ream_Teek.txt");
        File.WriteAllText(teek, string.Empty);
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime", GnomeFight), teek };
        h.Vm.AddLogCommand.Execute(null);
        h.Pipeline.LiveDps.OnLogServer("Frostreaver");
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", "a gnome", 100, 1));
        h.Pipeline.LiveDps.OnLogServer("Teek");
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Ream", "a bat", 50, 1));
        h.Vm.Refresh();

        h.Vm.SelectedToolsServer = MainViewModel.AllServers;
        Assert.Equal(2, h.Vm.Encounters.Count);

        h.Vm.SelectedToolsServer = "teek";
        Assert.Equal("a bat", Assert.Single(h.Vm.Encounters).TargetName);

        h.Vm.SelectedToolsServer = "frostreaver";
        Assert.Equal("a gnome", Assert.Single(h.Vm.Encounters).TargetName);
        await Task.CompletedTask;
    }
}
