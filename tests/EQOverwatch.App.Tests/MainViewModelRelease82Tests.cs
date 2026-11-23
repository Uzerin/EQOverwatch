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
            return Task.FromResult<IReadOnlyList<ResourceZoneBonus>>(new[]
            {
                new ResourceZoneBonus("Plane of Fear", ResourceBonus.Loot, ResourceBonusSource.Website, 0, 46, 60),
                new ResourceZoneBonus("Velketor's Labyrinth", ResourceBonus.Loot, ResourceBonusSource.Website, 0, 50, 65),
                new ResourceZoneBonus("Befallen", ResourceBonus.Unknown, ResourceBonusSource.Website, 0, 5, 20),
            });
        }

        public Task<IReadOnlyList<ResourceZoneBonus>> WebsiteDayAsync(string server, string day, CancellationToken cancellation = default)
        {
            Calls.Add("day " + day);
            ResourceBonus bonus = day.EndsWith('1') ? ResourceBonus.Coin : ResourceBonus.None;
            return Task.FromResult<IReadOnlyList<ResourceZoneBonus>>(new[] { new ResourceZoneBonus("Plane of Fear", bonus, ResourceBonusSource.Website, 0, 46, 60) });
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
        Assert.Equal(new[] { "AA (1)", "Loot (3)", "Unknown (1)" }, h.Vm.ResourceGroups.Select(g => g.Title));
        Assert.Equal(new[] { "Velketor's Labyrinth|50–65|frostreaver.zone", "Plane of Fear|46–60|frostreaver.zone", "The Akheva Ruins|—|Your logs" },
            h.Vm.ResourceGroups[1].Rows.Select(r => $"{r.Zone}|{r.Levels}|{r.Source}")); // highest levels first
        Assert.Equal("Karnor's Castle|3 players", h.Vm.ResourceGroups[0].Rows.Select(r => $"{r.Zone}|{r.Source}").Single());
        Assert.Equal(new[] { "—|AA (1)", "Loot (3)|—", "—|Unknown (1)" },
            h.Vm.ResourcePairs.Select(p => $"{p.Left?.Title ?? "—"}|{p.Right?.Title ?? "—"}")); // rows of two: XP|AA, …, No bonus|Unknown

        // Past days are read from the website once and kept; a zone's breakdown covers every day.
        service.Calls.Clear();
        h.Vm.ResourceBackfillPause = TimeSpan.Zero;
        await h.Vm.BackfillResourceHistoryAsync(new[] { "frostreaver" }, now);
        int pastDays = service.Calls.Count;
        Assert.True(pastDays > 100);
        await h.Vm.BackfillResourceHistoryAsync(new[] { "frostreaver" }, now);
        Assert.Equal(pastDays, service.Calls.Count); // nothing read twice

        h.Vm.ShowResourceRows(now);
        Assert.Equal(pastDays + 1, h.Vm.ResourceDayChoices.Count);
        h.Vm.ResourcePickedRow = h.Vm.ResourceGroups[1].Rows[1];
        Assert.Equal(new[] { "No bonus", "Coin", "Loot" }, h.Vm.ResourceBreakdown.Select(r => r.Bonus));
        Assert.Equal(pastDays + 1, h.Vm.ResourceBreakdown.Sum(r => int.Parse(r.Days, System.Globalization.CultureInfo.CurrentCulture)));

        h.Vm.SelectedResourceDay = h.Vm.ResourceDayChoices[1]; // yesterday
        Assert.Single(h.Vm.ResourceGroups);

        h.Vm.ResourceHunterNotices = false;
        h.Vm.UseResourceWebsites = false;
        h.Vm.PersonalBestToast = false;
        AppSettings saved = h.Saved();
        Assert.False(saved.ResourceHunterNotices || saved.UseResourceWebsites || saved.PersonalBestToast);
        Assert.True(saved.ShareResourceBonuses);
    }
}
