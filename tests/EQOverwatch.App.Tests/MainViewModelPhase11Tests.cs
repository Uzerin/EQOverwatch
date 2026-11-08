using EQOverwatch.Core.Updates;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    private static UpdateCheckResult Newer(string version = "v9.0.1") =>
        new(UpdateCheckStatus.UpdateAvailable, version, "https://github.com/Uzerin/EQOverwatch/releases/tag/" + version, "Adds AA & Disc timers.");

    [Fact]
    public async Task A_newer_release_shows_the_update_bar_with_the_versions_and_notes()
    {
        using var h = new Harness();
        h.Updates.Result = Newer();

        await h.Vm.StartUpdateCheckAsync();

        Assert.True(h.Vm.IsUpdateAvailable);
        Assert.Equal($"EQ Overwatch v9.0.1 is available (you have {h.Vm.AppVersion}).", h.Vm.UpdateMessage);
        Assert.Equal("Adds AA & Disc timers.", h.Vm.UpdateNotes);
        Assert.Equal(h.Vm.AppVersion, h.Updates.LastCurrentVersion);
    }

    [Fact]
    public async Task No_update_means_no_bar_and_a_status_line()
    {
        using var h = new Harness();

        await h.Vm.StartUpdateCheckAsync();

        Assert.False(h.Vm.IsUpdateAvailable);
        Assert.Equal($"You have the latest version ({h.Vm.AppVersion}).", h.Vm.UpdateStatus);
    }

    [Fact]
    public async Task A_failed_check_is_silent_at_start_up_but_explained_in_settings()
    {
        using var h = new Harness();
        h.Updates.Result = new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "GitHub answered 403 rate limit exceeded.");

        await h.Vm.StartUpdateCheckAsync();

        Assert.False(h.Vm.IsUpdateAvailable);
        Assert.Contains("Could not check for updates", h.Vm.UpdateStatus);
        Assert.DoesNotContain("Could not", h.Vm.StatusMessage);
    }

    [Fact]
    public async Task Nothing_is_checked_at_start_up_when_update_checks_are_off_but_check_now_still_works()
    {
        using var h = new Harness((settings, _) => settings.CheckForUpdates = false);
        h.Updates.Result = Newer();

        await h.Vm.StartUpdateCheckAsync();
        Assert.Equal(0, h.Updates.Calls);
        Assert.False(h.Vm.IsUpdateAvailable);

        await h.Vm.CheckUpdatesNowCommand.ExecuteAsync(null);
        Assert.Equal(1, h.Updates.Calls);
        Assert.True(h.Vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task The_download_button_opens_the_release_page()
    {
        using var h = new Harness();
        h.Updates.Result = Newer();
        await h.Vm.StartUpdateCheckAsync();

        h.Vm.OpenUpdateCommand.Execute(null);

        Assert.Equal("https://github.com/Uzerin/EQOverwatch/releases/tag/v9.0.1", Assert.Single(h.OpenedUrls));
    }

    [Fact]
    public async Task Later_hides_the_bar_for_now_and_it_comes_back_next_time()
    {
        using var h = new Harness();
        h.Updates.Result = Newer();
        await h.Vm.StartUpdateCheckAsync();

        h.Vm.DismissUpdateCommand.Execute(null);
        Assert.False(h.Vm.IsUpdateAvailable);
        Assert.Equal(string.Empty, h.Saved().SkippedUpdateVersion);

        await h.Vm.StartUpdateCheckAsync();
        Assert.True(h.Vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task Skip_this_version_is_remembered_but_a_newer_version_is_still_announced()
    {
        using var h = new Harness();
        h.Updates.Result = Newer("v9.0.1");
        await h.Vm.StartUpdateCheckAsync();

        h.Vm.SkipUpdateVersionCommand.Execute(null);
        Assert.False(h.Vm.IsUpdateAvailable);
        Assert.Equal("v9.0.1", h.Saved().SkippedUpdateVersion);

        await h.Vm.StartUpdateCheckAsync();
        Assert.False(h.Vm.IsUpdateAvailable);
        Assert.Contains("skip", h.Vm.UpdateStatus);

        await h.Vm.CheckUpdatesNowCommand.ExecuteAsync(null); // asking by hand always shows it
        Assert.True(h.Vm.IsUpdateAvailable);

        h.Vm.DismissUpdateCommand.Execute(null);
        h.Updates.Result = Newer("v9.0.2");
        await h.Vm.StartUpdateCheckAsync();
        Assert.True(h.Vm.IsUpdateAvailable);
        Assert.Contains("v9.0.2", h.Vm.UpdateMessage);
    }

    [Fact]
    public async Task The_update_settings_are_saved_and_restored()
    {
        using var h = new Harness();
        h.Vm.CheckForUpdates = false;
        Assert.False(h.Saved().CheckForUpdates);

        using var again = new Harness((settings, _) =>
        {
            settings.CheckForUpdates = false;
            settings.SkippedUpdateVersion = "v9.0.1";
        });
        Assert.False(again.Vm.CheckForUpdates);
        again.Updates.Result = Newer("v9.0.1");
        await again.Vm.CheckUpdatesNowCommand.ExecuteAsync(null);
        again.Vm.CheckForUpdates = true; // any change saves the settings
        Assert.Equal("v9.0.1", again.Saved().SkippedUpdateVersion);
    }

    [Fact]
    public async Task The_app_checks_again_while_running_after_the_chosen_hours_and_does_not_repeat_the_banner()
    {
        using var h = new Harness();
        h.Updates.Result = Newer();
        await h.Vm.StartUpdateCheckAsync();
        Assert.Equal(1, h.Updates.Calls);
        Assert.Equal(4, h.Vm.UpdateCheckHours);

        await h.Vm.TickUpdateCheck(DateTime.UtcNow.AddHours(1));
        Assert.Equal(1, h.Updates.Calls); // too soon

        h.Vm.DismissUpdateCommand.Execute(null);
        await h.Vm.TickUpdateCheck(DateTime.UtcNow.AddHours(5));
        Assert.Equal(2, h.Updates.Calls);
        Assert.False(h.Vm.IsUpdateAvailable); // already announced this session

        h.Updates.Result = Newer("v9.0.2");
        await h.Vm.TickUpdateCheck(DateTime.UtcNow.AddHours(10));
        Assert.True(h.Vm.IsUpdateAvailable); // a newer one is announced
        Assert.Contains("v9.0.2", h.Vm.UpdateMessage);
    }

    [Fact]
    public async Task Zero_hours_checks_only_at_launch_and_the_setting_is_saved()
    {
        using var h = new Harness((s, _) => s.UpdateCheckHours = 0);
        Assert.Equal(0, h.Vm.UpdateCheckHours);
        await h.Vm.StartUpdateCheckAsync();
        await h.Vm.TickUpdateCheck(DateTime.UtcNow.AddDays(3));
        Assert.Equal(1, h.Updates.Calls);

        h.Vm.UpdateCheckHours = 6;
        Assert.Equal(6, h.Saved().UpdateCheckHours);
        h.Vm.UpdateCheckHours = 100000;
        Assert.Equal(168, h.Vm.UpdateCheckHours);
    }

    [Fact]
    public async Task A_skipped_version_stays_skipped_on_the_repeat_check()
    {
        using var h = new Harness();
        h.Updates.Result = Newer();
        await h.Vm.StartUpdateCheckAsync();
        h.Vm.SkipUpdateVersionCommand.Execute(null);

        await h.Vm.TickUpdateCheck(DateTime.UtcNow.AddHours(5));
        Assert.False(h.Vm.IsUpdateAvailable);
    }

    [Fact]
    public async Task The_install_confirmation_shows_the_release_notes()
    {
        using var h = new Harness();
        h.Updates.Result = Newer();
        await h.Vm.StartUpdateCheckAsync();
        Assert.Contains("What's new:\nAdds AA & Disc timers.", h.Vm.InstallConfirmationText("v9.0.1"));
    }

    [Fact]
    public void The_damage_shield_line_says_how_long_the_shield_was_up_and_is_empty_without_one()
    {
        DateTime start = new(2026, 11, 8, 20, 0, 0);
        var plain = new EQOverwatch.Core.Models.EncounterSnapshot(
            1, "Lord Yelinak", start, start.AddSeconds(60), 60, 1000, false, Array.Empty<EQOverwatch.Core.Models.ParticipantSnapshot>());
        Assert.Equal(string.Empty, EQOverwatch.App.ViewModels.MainViewModel.ShieldSummaryText(plain));

        var shielded = plain with { DamageShield = new EQOverwatch.Core.Models.DamageShieldSummary(1240, 12, 3, 41) };
        Assert.Equal(
            "\nDamage shield from Lord Yelinak: 1,240 in 12 hits, from 0:03 to 0:41 into the fight (38s).",
            EQOverwatch.App.ViewModels.MainViewModel.ShieldSummaryText(shielded).Replace("\r", string.Empty));
    }
}
