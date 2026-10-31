using System.IO;
using EQOverwatch.App.ViewModels;
using EQOverwatch.Core.Export;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Settings;
using EQOverwatch.Core.Sharing;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    private static string MakeEqFolder(string root, params string[] logNames)
    {
        string eq = Path.Combine(root, "EQ");
        Directory.CreateDirectory(Path.Combine(eq, "Logs"));
        foreach (string name in logNames)
        {
            File.WriteAllText(Path.Combine(eq, "Logs", name), string.Empty);
        }

        return eq;
    }

    // ───────────────────────────── branding ─────────────────────────────

    [Fact]
    public void The_window_title_and_badge_show_the_product_name_and_version()
    {
        using var h = new Harness();

        Assert.Equal("EQ Overwatch", h.Vm.AppName);
        Assert.Equal("v1.0.32", h.Vm.AppVersion);
        Assert.Equal("EQ Overwatch v1.0.32", h.Vm.WindowTitle);
    }

    [Fact]
    public void The_lock_button_text_follows_the_lock_state()
    {
        using var h = new Harness();

        bool locked = h.Vm.AreOverlaysLocked;
        Assert.Equal(locked ? "Unlock overlays" : "Lock overlays", h.Vm.LockButtonText);

        h.Vm.AreOverlaysLocked = !locked;
        Assert.Equal(!locked ? "Unlock overlays" : "Lock overlays", h.Vm.LockButtonText);
    }

    // ───────────────────────────── Characters tab ─────────────────────────────

    [Fact]
    public async Task Choosing_the_eq_folder_discovers_logs_sorted_by_server_then_character_and_loads_spells()
    {
        using var h = new Harness();
        string eq = MakeEqFolder(Path.GetTempPath() + "eqo-" + Guid.NewGuid().ToString("N"),
            "eqlog_Zed_Teek.txt", "eqlog_Alice_Teek.txt", "eqlog_Bob_Frostreaver.txt");
        try
        {
            Harness.WriteSpellFile(Path.Combine(eq, "spells_us.txt"));
            h.Dialogs.EqFolder = Path.Combine(eq, "Logs"); // choosing the Logs folder is fine

            await h.Vm.SetEqFolderCommand.ExecuteAsync(null);

            Assert.Equal(eq, h.Vm.EqFolderPath);
            Assert.Equal(
                new[] { "Frostreaver/Bob", "Teek/Alice", "Teek/Zed" },
                h.Vm.SortedCharacters.Select(l => $"{l.ServerName}/{l.CharacterName}"));
            Assert.All(h.Vm.SortedCharacters, l => Assert.False(l.IsTriggerEnabled)); // alts are not tailed until enabled
            Assert.Equal(Path.Combine(eq, "spells_us.txt"), h.Vm.SpellsFilePath);
            Assert.Contains("Loaded", h.Vm.SpellsStatus);
            Assert.Equal(Path.Combine(eq, "spells_us.txt"), h.Vm.SpellsFilePath);
            Assert.Equal(eq, h.Saved().EqFolderPath);
        }
        finally
        {
            Directory.Delete(Directory.GetParent(eq)!.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_eq_folder_is_reported_without_changing_anything()
    {
        using var h = new Harness();

        await h.Vm.ApplyEqFolderAsync(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(string.Empty, h.Vm.EqFolderPath);
        Assert.Contains("does not exist", h.Vm.EqFolderStatus);
        Assert.Empty(h.Vm.SortedCharacters);
    }

    [Fact]
    public void Additional_characters_are_validated_deduplicated_saved_and_removable()
    {
        using var h = new Harness();

        h.Vm.NewAdditionalCharacter = "oodles";
        h.Vm.AddAdditionalCharacterCommand.Execute(null);
        h.Vm.NewAdditionalCharacter = "OODLES";
        h.Vm.AddAdditionalCharacterCommand.Execute(null); // duplicate
        h.Vm.NewAdditionalCharacter = "Bad Name 9";
        h.Vm.AddAdditionalCharacterCommand.Execute(null); // invalid

        Assert.Equal(new[] { "Oodles" }, h.Vm.AdditionalCharacters);
        Assert.Equal(new[] { "Oodles" }, h.Saved().AdditionalMonitoredCharacters);

        h.Vm.RemoveAdditionalCharacterCommand.Execute("Oodles");
        Assert.Empty(h.Vm.AdditionalCharacters);
        Assert.Empty(h.Saved().AdditionalMonitoredCharacters);
    }

    [Fact]
    public async Task Only_monitored_characters_get_buff_timers_for_buffs_cast_on_others()
    {
        using var h = new Harness();
        h.Dialogs.SpellsFile = Harness.WriteSpellFile(h.SpellsPath);
        await h.Vm.BrowseSpellsCommand.ExecuteAsync(null);
        AddTracked(h, "Spirit of Wolf", TimerOverlayCategory.Buffs);

        h.Vm.NewAdditionalCharacter = "Oodles";
        h.Vm.AddAdditionalCharacterCommand.Execute(null);

        Assert.Contains("Oodles", h.Pipeline.MonitoredCharacters);
        Assert.NotNull(h.Pipeline.Buffs.AllowedTargets);

        h.Pipeline.Buffs.OnLine("Stackprime", "Oodles looks quick.", DateTime.UtcNow);
        h.Pipeline.Buffs.OnLine("Stackprime", "Stranger looks quick.", DateTime.UtcNow);

        ActiveTimerSnapshot timer = Assert.Single(h.Pipeline.Triggers.Snapshot.Timers);
        Assert.Contains("Oodles", timer.Text);
    }

    [Fact]
    public void Chain_positions_are_exclusive_synced_to_the_chain_service_and_saved()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Ream"), h.CreateLog("Oodles") };
        h.Vm.AddLogCommand.Execute(null);
        WatchedLogConfig ream = h.Vm.Logs.First(l => l.CharacterName == "Ream");
        WatchedLogConfig oodles = h.Vm.Logs.First(l => l.CharacterName == "Oodles");

        ream.ChChainPosition = 3;
        Assert.Equal(3, h.Pipeline.ChChain.GetPosition("Ream"));

        oodles.ChChainPosition = 3;
        Assert.Equal(0, ream.ChChainPosition);
        Assert.Equal(0, h.Pipeline.ChChain.GetPosition("Ream"));
        Assert.Equal(3, h.Pipeline.ChChain.GetPosition("Oodles"));
        Assert.Equal(3, h.Saved().WatchedLogs.First(l => l.CharacterName == "Oodles").ChChainPosition);
    }

    [Fact]
    public void The_chain_settings_are_clamped_pushed_and_saved()
    {
        using var h = new Harness();

        h.Vm.ChainCueKeyword = "GO GO";
        h.Vm.ChainPositionTagFormat = "[{N}]";

        Assert.Equal("GO GO", h.Pipeline.ChChain.Settings.CueKeyword);
        Assert.Equal("[{N}]", h.Pipeline.ChChain.Settings.PositionTagFormat);

        h.Vm.ChainPositionTagFormat = "no token here";
        Assert.False(string.IsNullOrEmpty(h.Vm.ChainTagError));
    }

    [Fact]
    public void The_chain_overlay_setting_routes_chain_bars()
    {
        using var h = new Harness();
        Assert.False(h.Vm.IsChChainOverlayVisible);
        Assert.False(h.Pipeline.ChChain.UseChChainOverlay);

        h.Vm.IsChChainOverlayVisible = true;

        Assert.True(h.Pipeline.ChChain.UseChChainOverlay);
        Assert.Contains("visible:ChChain:True", h.Overlays.Calls);
    }

    // ───────────────────────────── Settings tab: history ─────────────────────────────

    [Fact]
    public void The_default_history_window_is_seven_days_with_all_choices_available()
    {
        using var h = new Harness();

        Assert.Equal(7, h.Vm.HistoricalDaysLimit);
        Assert.Equal(new[] { 1, 3, 7, 14, 30, 90, 0 }, h.Vm.HistoricalDaysOptions.Select(o => o.Days));
        Assert.Equal(60, h.Vm.DefaultCasterLevel);
    }

    [Fact]
    public async Task Choosing_a_primary_dps_character_imports_the_last_seven_days_automatically()
    {
        using var h = new Harness();
        // The log's fight is on Oct 21; the injected clock is Oct 26 12:00, i.e. 5 days later.
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime", SampleLog) };
        h.Vm.AddLogCommand.Execute(null);
        await h.Vm.AutoImportTask;

        WatchedLogConfig log = h.Vm.Logs.Single();
        log.IsPrimaryDps = false;
        log.IsPrimaryDps = true;
        await h.Vm.AutoImportTask;

        Assert.NotEmpty(h.Pipeline.HistoricalDps.Snapshot.All);
        Assert.False(h.Vm.IsImporting);
    }

    [Fact]
    public async Task A_shorter_history_window_leaves_out_older_fights()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime", SampleLog) };
        h.Vm.AddLogCommand.Execute(null);
        h.Vm.Logs.Single().IsPrimaryDps = true;
        await h.Vm.AutoImportTask;
        Assert.NotEmpty(h.Pipeline.HistoricalDps.Snapshot.All);

        h.Vm.HistoricalDaysLimit = 1; // the fight is 5 days old
        await h.Vm.AutoImportTask;

        Assert.Empty(h.Pipeline.HistoricalDps.Snapshot.All);
        Assert.Equal(1, h.Saved().HistoricalDaysLimit);
    }

    // ───────────────────────────── DPS exports ─────────────────────────────

    [Fact]
    public void Copy_top_12_puts_chat_text_on_the_clipboard()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 1000, 1));
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Oodles", Giant, 500, 3));
        h.Vm.Refresh();

        h.Vm.CopyTop12ForChatCommand.Execute(null);

        string text = Assert.Single(h.Clipboard.Texts);
        Assert.StartsWith(Giant, text);
        Assert.Contains("1. Stackprime 1,000", text);
        Assert.Contains("2. Oodles 500", text);
    }

    [Fact]
    public void Copy_image_hands_the_encounter_and_the_branded_footer_to_the_clipboard_service()
    {
        using var h = new Harness();
        h.Pipeline.LiveDps.OnCombatEvent(Hit("Stackprime", Giant, 1000, 1));
        h.Vm.Refresh();

        h.Vm.CopyImageForDiscordCommand.Execute(null);

        var image = Assert.Single(h.Clipboard.Images);
        Assert.Equal(Giant, image.Encounter.TargetName);
        Assert.Equal("EQ Overwatch v1.0.32", image.Footer);
        Assert.Equal(EncounterExport.AllAttackers, image.TopCount); // the picture lists everyone, not just the top 12
    }

    [Fact]
    public void Exports_without_a_selected_encounter_only_set_a_status_message()
    {
        using var h = new Harness();

        h.Vm.CopyTop12ForChatCommand.Execute(null);
        h.Vm.CopyImageForDiscordCommand.Execute(null);

        Assert.Empty(h.Clipboard.Texts);
        Assert.Empty(h.Clipboard.Images);
        Assert.Contains("encounter", h.Vm.StatusMessage);
    }

    // ───────────────────────────── sharing ─────────────────────────────

    private static string ShareLine(TriggerRule rule) =>
        $"Ream tells the raid, '{TriggerShareSerializer.Export(rule)}'";

    [Fact]
    public void A_shared_trigger_waits_for_approval_and_is_added_only_when_accepted()
    {
        using var h = new Harness();
        var rule = new TriggerRule { Name = "Mez broke", Pattern = "has been awakened", AlertTextTemplate = "Mez!" };

        h.Pipeline.SharedTriggers.OnLine("Stackprime", ShareLine(rule), DateTime.UtcNow);
        h.Vm.Refresh();

        Assert.True(h.Vm.HasPendingSharedTriggers);
        Assert.Contains("Ream", h.Vm.CurrentSharedOffer!.Title);
        Assert.Empty(h.Vm.Rules); // nothing is enabled without consent

        h.Vm.AcceptSharedCommand.Execute(h.Vm.CurrentSharedOffer);

        Assert.False(h.Vm.HasPendingSharedTriggers);
        TriggerRuleViewModel added = Assert.Single(h.Vm.Rules);
        Assert.Equal("Mez broke", added.Name);
        Assert.Single(h.Saved().Triggers);
    }

    [Fact]
    public void Ignoring_a_shared_trigger_adds_nothing_and_the_same_token_does_not_come_back()
    {
        using var h = new Harness();
        var rule = new TriggerRule { Name = "Mez broke", Pattern = "has been awakened" };
        string line = ShareLine(rule);

        h.Pipeline.SharedTriggers.OnLine("Stackprime", line, DateTime.UtcNow);
        h.Vm.Refresh();
        h.Vm.IgnoreSharedCommand.Execute(h.Vm.CurrentSharedOffer);

        h.Pipeline.SharedTriggers.OnLine("Oodles", line, DateTime.UtcNow.AddSeconds(30));
        h.Vm.Refresh();

        Assert.Empty(h.Vm.Rules);
        Assert.False(h.Vm.HasPendingSharedTriggers);
    }

    [Fact]
    public void Share_commands_copy_an_EQO_token_for_the_selected_trigger_and_for_raid_timers()
    {
        using var h = new Harness();
        h.Vm.AddRuleCommand.Execute(null);
        h.Vm.SelectedRule!.Pattern = "^cast$"; // a blank pattern is (rightly) not importable

        h.Vm.ShareSelectedRuleCommand.Execute(null);

        string token = Assert.Single(h.Clipboard.Texts);
        Assert.StartsWith("{EQO:", token);
        Assert.True(TriggerShareSerializer.TryImport(token, out SharedItem? item, out string error), error);
        Assert.Equal(SharedItemKind.Trigger, item!.Kind);
    }
}
