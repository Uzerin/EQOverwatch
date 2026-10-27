using System.IO;
using System.Globalization;
using EQCompanion.App.ViewModels;
using EQCompanion.Core.Models;
using EQCompanion.Core.Settings;
using Xunit;

namespace EQCompanion.App.Tests;

public partial class MainViewModelTests
{
    private static readonly DateTime BootNow = new(2026, 10, 26, 12, 0, 0);

    private static string LogLine(DateTime at, string message) =>
        $"[{at.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture)}] {message}";

    /// <summary>A harness whose settings watch Stackprime's log (already on disk, Primary DPS) with the given lines.</summary>
    private static Harness HarnessWithPrimaryLog(Func<DateTime, string[]> lines, Action<AppSettings>? tweak = null) =>
        new((settings, dir) =>
        {
            string path = Path.Combine(dir, "eqlog_Stackprime_Frostreaver.txt");
            File.WriteAllText(path, string.Join("\n", lines(BootNow)) + "\n");
            settings.WatchedLogs.Add(new WatchedLogSettings
            {
                FilePath = path, CharacterName = "Stackprime", IsPrimaryDps = true, IsTriggerEnabled = true,
            });
            tweak?.Invoke(settings);
        });

    // ───────────────────────────── start-up ─────────────────────────────

    [Fact]
    public void A_saved_everquest_folder_is_described_at_start_up_without_pressing_refresh()
    {
        using var h = new Harness((settings, dir) =>
        {
            string logs = Path.Combine(dir, "Logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "eqlog_Stackprime_Frostreaver.txt"), string.Empty);
            settings.EqFolderPath = dir;
        });

        Assert.DoesNotContain("No EverQuest folder", h.Vm.EqFolderStatus);
        Assert.Contains("1 character log(s) found", h.Vm.EqFolderStatus);
        Assert.Single(h.Vm.Logs); // and the character is listed
    }

    [Fact]
    public void Without_a_saved_folder_the_status_still_asks_for_one()
    {
        using var h = new Harness();

        Assert.Contains("No EverQuest folder", h.Vm.EqFolderStatus);
    }

    [Fact]
    public async Task Opening_the_app_mid_fight_shows_the_fight_in_progress_and_starts_live_monitoring()
    {
        using var h = HarnessWithPrimaryLog(now => new[]
        {
            LogLine(now.AddSeconds(-4), "You slash a frost giant for 142 points of damage."),
            LogLine(now.AddSeconds(-3), "Stackprime crushes a frost giant for 210 points of damage."),
            LogLine(now.AddSeconds(-2), "Oodles hit a frost giant for 450 points of non-melee damage."),
        });
        h.Now = BootNow;

        await h.Vm.StartSessionAsync();

        EncounterRowViewModel fight = Assert.Single(h.Vm.Encounters);
        Assert.Equal(Giant, fight.TargetName);
        Assert.Equal(802, fight.TotalDamage);
        Assert.True(fight.IsActive);          // still in progress, as if the app had been open all along
        Assert.False(fight.IsImported);
        Assert.Same(fight, h.Vm.SelectedEncounter);
        Assert.Contains("(in progress)", h.Vm.EncounterSummary);
        Assert.True(h.Pipeline.IsLiveRunning);
        Assert.True(h.Vm.IsLiveRunning);
    }

    [Fact]
    public async Task A_fight_that_ended_long_ago_is_loaded_but_not_shown_as_in_progress()
    {
        using var h = HarnessWithPrimaryLog(now => new[]
        {
            LogLine(now.AddHours(-3), "You slash a frost giant for 142 points of damage."),
            LogLine(now.AddHours(-3).AddSeconds(2), "Stackprime crushes a frost giant for 210 points of damage."),
        });
        h.Now = BootNow;

        await h.Vm.StartSessionAsync();

        EncounterRowViewModel fight = Assert.Single(h.Vm.Encounters);
        Assert.False(fight.IsActive);
        Assert.Equal(352, fight.TotalDamage);
    }

    [Fact]
    public async Task Fights_older_than_the_history_window_are_not_loaded_at_start_up()
    {
        using var h = HarnessWithPrimaryLog(
            now => new[]
            {
                LogLine(now.AddDays(-3), "You slash a frost giant for 142 points of damage."),
                LogLine(now.AddHours(-1), "You slash a gnoll pup for 10 points of damage."),
            },
            settings => settings.HistoricalDaysLimit = 1);
        h.Now = BootNow;

        await h.Vm.StartSessionAsync();

        Assert.Equal("a gnoll pup", Assert.Single(h.Vm.Encounters).TargetName);
    }

    [Fact]
    public async Task There_is_no_setting_to_turn_live_monitoring_off()
    {
        using var h = new Harness();

        Assert.Null(typeof(AppSettings).GetProperty("AutoStartLive"));
        Assert.Null(h.Vm.GetType().GetProperty("ToggleLiveCommand"));
        await h.Vm.StartSessionAsync();
        Assert.True(h.Pipeline.IsLiveRunning);
    }

    [Fact]
    public async Task Start_up_without_a_primary_dps_log_just_starts_live_monitoring()
    {
        using var h = new Harness();

        await h.Vm.StartSessionAsync();

        Assert.True(h.Pipeline.IsLiveRunning);
        Assert.Empty(h.Vm.Encounters);
    }

    // ───────────────────────────── one encounter list ─────────────────────────────

    [Fact]
    public async Task An_imported_fight_that_is_also_live_is_listed_once_and_the_list_is_newest_first()
    {
        using var h = new Harness();
        // The same fight, seen live and in an imported copy of the log (it starts at 11:45:01), plus a newer live fight.
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 142, 1));
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", "a gnoll", 50, 30));
        h.Dialogs.ImportFile = h.CreateLog("Stackprime", SampleLog);

        await h.Vm.ImportLogCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "a gnoll", Giant }, h.Vm.Encounters.Select(r => r.TargetName));
        Assert.All(h.Vm.Encounters, r => Assert.False(r.IsImported)); // the live copy wins
    }

    // ───────────────────────────── editing raid triggers ─────────────────────────────

    private static void BuildRaidRule(Harness h, string mob = "Tormax")
    {
        h.Vm.RaidMobName = mob;
        h.Vm.RaidSpellName = "Dragon Roar";
        h.Vm.RaidMatchText = "is blasted";
        h.Vm.RaidRecastSeconds = 30;
        h.Vm.RaidCategory = TimerOverlayCategory.Debuffs;
        h.Vm.RaidWarningTtsText = "Roar in ten";
        h.Vm.AddRaidTriggerCommand.Execute(null);
    }

    [Fact]
    public void A_saved_raid_trigger_can_be_loaded_into_the_builder_changed_and_saved_in_place()
    {
        using var h = new Harness();
        BuildRaidRule(h);
        BuildRaidRule(h, mob: "Velketor");
        RaidTimerRuleViewModel first = h.Vm.RaidRules[0];
        Guid id = first.Id;
        first.IsEnabled = false;

        h.Vm.EditRaidTriggerCommand.Execute(first);

        Assert.True(h.Vm.IsEditingRaidRule);
        Assert.Equal("Save Changes", h.Vm.RaidSaveButtonText);
        Assert.Equal("Tormax", h.Vm.RaidMobName);
        Assert.Equal("Dragon Roar", h.Vm.RaidSpellName);
        Assert.Equal("is blasted", h.Vm.RaidMatchText);
        Assert.Equal(30, h.Vm.RaidRecastSeconds);
        Assert.Equal(TimerOverlayCategory.Debuffs, h.Vm.RaidCategory);
        Assert.Equal("Roar in ten", h.Vm.RaidWarningTtsText);

        h.Vm.RaidRecastSeconds = 45;
        h.Vm.RaidStartTtsText = "Roar now";
        h.Vm.AddRaidTriggerCommand.Execute(null);

        Assert.Equal(2, h.Vm.RaidRules.Count);              // nothing was added
        RaidTimerRuleViewModel saved = h.Vm.RaidRules[0];    // and it stayed in place
        Assert.Equal(id, saved.Id);
        Assert.False(saved.IsEnabled);                       // the on/off state is kept
        Assert.Equal(45, saved.RecastSeconds);
        Assert.Equal("Roar now", saved.StartText);
        Assert.Contains("Saved changes", h.Vm.RaidBuilderStatus);

        Assert.False(h.Vm.IsEditingRaidRule);
        Assert.Equal("Add Raid Trigger", h.Vm.RaidSaveButtonText);
        Assert.Equal(string.Empty, h.Vm.RaidSpellName);      // the builder is clear again

        RaidBossTimerRule persisted = h.Saved().RaidBossTimers.Single(r => r.Id == id);
        Assert.Equal(45, persisted.RecastSeconds);
        Assert.Equal("Roar now", persisted.StartTtsText);
        Assert.Equal("Roar in ten", persisted.WarningTtsText);
        Assert.False(persisted.IsEnabled);
    }

    [Fact]
    public void An_edit_can_be_cancelled_and_removing_the_edited_trigger_ends_the_edit()
    {
        using var h = new Harness();
        BuildRaidRule(h);
        RaidTimerRuleViewModel row = Assert.Single(h.Vm.RaidRules);

        Assert.False(h.Vm.CancelRaidEditCommand.CanExecute(null));
        h.Vm.EditRaidTriggerCommand.Execute(row);
        Assert.True(h.Vm.CancelRaidEditCommand.CanExecute(null));

        h.Vm.CancelRaidEditCommand.Execute(null);
        Assert.False(h.Vm.IsEditingRaidRule);
        Assert.Single(h.Vm.RaidRules);                       // unchanged
        Assert.Equal(string.Empty, h.Vm.RaidSpellName);

        h.Vm.EditRaidTriggerCommand.Execute(row);
        h.Vm.RemoveRaidTriggerCommand.Execute(row);
        Assert.False(h.Vm.IsEditingRaidRule);
        Assert.Empty(h.Vm.RaidRules);
    }

    [Fact]
    public void Editing_keeps_the_spell_id_of_the_trigger()
    {
        using var h = new Harness();
        h.Vm.RaidSpellName = "Dragon Roar";
        h.Vm.RaidMatchText = "is blasted";
        h.Vm.AddRaidTriggerCommand.Execute(null);
        RaidTimerRuleViewModel row = Assert.Single(h.Vm.RaidRules);
        int spellId = row.ToRule().SpellId;

        h.Vm.EditRaidTriggerCommand.Execute(row);
        h.Vm.RaidMatchText = "is blasted by a roar";
        h.Vm.AddRaidTriggerCommand.Execute(null);

        RaidBossTimerRule rule = Assert.Single(h.Vm.RaidRules).ToRule();
        Assert.Equal(spellId, rule.SpellId);
        Assert.Equal("is blasted by a roar", rule.MatchText);
    }
}
