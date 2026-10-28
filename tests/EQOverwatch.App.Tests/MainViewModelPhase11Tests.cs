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
}
