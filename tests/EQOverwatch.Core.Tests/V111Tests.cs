using EQOverwatch.Core.Drops;
using EQOverwatch.Core.Session;
using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V111Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqo-v111-" + Guid.NewGuid().ToString("N"));

    public V111Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Player_counts_are_read_with_their_servers()
    {
        PlayerStats stats = PlayerStats.Parse(
            "{\"activeToday\":6,\"serversToday\":[{\"s\":\"vaniki\",\"n\":3}],\"servers7Days\":[{\"s\":\"vaniki\",\"n\":4},{\"s\":\"teek\",\"n\":2}]}");

        Assert.Equal(6, stats.ActiveToday);
        Assert.Equal(3, stats.ServersToday["Vaniki"]);
        Assert.Equal(2, stats.Servers7Days["teek"]);
        Assert.Empty(PlayerStats.Parse("{\"activeToday\":1}").ServersToday);
    }

    [Fact]
    public void Playtime_counts_the_time_between_lines_and_leaves_out_long_gaps()
    {
        string log = Path.Combine(_dir, "eqlog_Tankard_teek.txt");
        File.WriteAllLines(log, new[]
        {
            "[Sun Nov 22 23:58:00 2026] You say, 'hi'",
            "[Mon Nov 23 00:02:00 2026] You say, 'still here'", // 4 minutes across midnight: 2 + 2
            "[Mon Nov 23 00:30:00 2026] You say, 'back'",        // 28 minutes away: not counted
            "[Mon Nov 23 00:33:00 2026] You say, 'bye'",
        });

        Dictionary<DateTime, double> days = Playtime.PerDay(log, new DateTime(2026, 11, 22));

        Assert.Equal(120, days[new DateTime(2026, 11, 22)], 3);
        Assert.Equal(120 + 180, days[new DateTime(2026, 11, 23)], 3);
    }

    [Fact]
    public void An_unreadable_settings_file_is_restored_from_the_last_good_copy()
    {
        string path = Path.Combine(_dir, "settings.json");
        var service = new AppSettingsService(path, TimeSpan.FromMilliseconds(10));
        service.Save(new AppSettings { EqFolderPath = @"C:\EQ" });
        service.Save(new AppSettings { EqFolderPath = @"C:\EQ2" }); // the first becomes settings.json.bak
        File.WriteAllText(path, "{ not json");

        var again = new AppSettingsService(path, TimeSpan.FromMilliseconds(10));
        AppSettings loaded = again.Load();

        Assert.Equal(@"C:\EQ", loaded.EqFolderPath);
        Assert.Contains("last good copy", again.LastLoadError);
    }
}
