using EQOverwatch.Core.ResourceHunter;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    private sealed class FakeResourceService : IResourceHunterService
    {
        public List<string> Calls { get; } = new();
        public List<ResourceFinding> Uploaded { get; } = new();

        public Task<bool> UploadAsync(string server, string id, string key, IReadOnlyList<ResourceFinding> findings, CancellationToken cancellation = default)
        {
            Calls.Add("put " + server);
            Uploaded.AddRange(findings);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ResourceZoneBonus>> SharedAsync(string server, string day, CancellationToken cancellation = default)
        {
            Calls.Add("shared " + server);
            return Task.FromResult<IReadOnlyList<ResourceZoneBonus>>(new[] { new ResourceZoneBonus("Karnor's Castle", ResourceBonus.Aa, ResourceBonusSource.Shared, 3) });
        }

        public Task<IReadOnlyList<ResourceZoneBonus>> WebsiteAsync(string server, string day, CancellationToken cancellation = default)
        {
            Calls.Add("site " + server);
            return Task.FromResult<IReadOnlyList<ResourceZoneBonus>>(new[] { new ResourceZoneBonus("Plane of Fear", ResourceBonus.Loot, ResourceBonusSource.Website) });
        }
    }

    [Fact]
    public async Task Resource_hunter_bonuses_show_on_the_tells_overlay_are_shared_and_fill_the_leveling_list()
    {
        using var h = new Harness();
        DateTime now = DateTime.UtcNow;
        h.Vm.BoxAlertClock = () => now;
        var service = new FakeResourceService();
        h.Vm.ConfigureResourceHunter(service);

        h.Pipeline.ResourceHunter.OnLine("Ream", "frostreaver", "You have entered The Akheva Ruins.", now);
        h.Pipeline.ResourceHunter.OnLine("Ream", "frostreaver", "This land is saturated with unstable magic, multiplying the loot that may be found here.", now);
        Assert.Equal("The Akheva Ruins: Loot bonus", Assert.Single(h.Pipeline.Notices.Current(now.AddSeconds(1))).Text);

        await h.Vm.RunResourceHunterAsync(new[] { ("", "frostreaver"), ("", "vaniki") }, now);
        Assert.Equal(new[] { "put frostreaver", "shared frostreaver", "shared vaniki", "site frostreaver" }, service.Calls.Order()); // no website for vaniki
        Assert.Equal("The Akheva Ruins", Assert.Single(service.Uploaded).Zone);

        await h.Vm.RunResourceHunterAsync(Array.Empty<(string, string)>(), now.AddMinutes(5));
        Assert.Equal(4, service.Calls.Count); // nothing new to share, and the next download is an hour away

        h.Vm.SelectedLevelingServer = "frostreaver";
        h.Vm.ShowResourceRows(now);
        Assert.Equal(new[] { "Karnor's Castle|AA|3 players", "Plane of Fear|Loot|frostreaver.zone", "The Akheva Ruins|Loot|Your logs" },
            h.Vm.ResourceRows.Select(r => $"{r.Zone}|{r.Bonus}|{r.Source}"));
        h.Vm.SelectedResourceFilter = "AA";
        Assert.Equal("Karnor's Castle", Assert.Single(h.Vm.ResourceRows).Zone);

        h.Vm.ResourceHunterNotices = false;
        h.Vm.UseResourceWebsites = false;
        h.Vm.PersonalBestToast = false;
        AppSettings saved = h.Saved();
        Assert.False(saved.ResourceHunterNotices || saved.UseResourceWebsites || saved.PersonalBestToast);
        Assert.True(saved.ShareResourceBonuses);
    }
}
