using System.Threading;
using System.Windows.Media.Imaging;
using EQOverwatch.App.Services;
using EQOverwatch.Core.Models;
using EQOverwatch.Core.Services;
using EQOverwatch.Core.Theming;
using Xunit;

namespace EQOverwatch.App.Tests;

public sealed class DiscordPictureTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 20, 0, 0);

    [Theory]
    [InlineData(40)] // a big raid: tall tables, so the columns widen
    [InlineData(2)]  // a small group: a short picture, so the graphs grow
    public void The_discord_picture_is_16_by_9(int players)
    {
        EncounterSnapshot fight = Fight(players);
        Exception? failure = null;
        int width = 0, height = 0;
        var thread = new Thread(() =>
        {
            try
            {
                BitmapSource image = EncounterImageRenderer.Render(fight, ThemePresets.ObsidianSlate, "EQ Overwatch");
                (width, height) = (image.PixelWidth, image.PixelHeight);
                if (Environment.GetEnvironmentVariable("EQO_PICTURE_OUT") is { Length: > 0 } dir)
                {
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(image));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(dir, $"picture-{players}.png"));
                    png.Save(file);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.InRange(width * 9.0 / height, 15.98, 16.02);
    }

    private static EncounterSnapshot Fight(int players)
    {
        var tracker = new LiveDpsTracker();
        tracker.BeginBatch();
        for (int t = 0; t <= 60; t += 4)
        {
            for (int p = 0; p < players; p++)
            {
                string name = "Raider" + (char)('a' + (p % 26)) + (char)('a' + (p / 26));
                tracker.OnCombatEvent(new CombatEvent(Start.AddSeconds(t), CombatEventKind.Damage, name, "Lord Inquisitor Seru", 100 + p, DamageKind.Melee, "hit"));
                tracker.OnCombatEvent(new CombatEvent(Start.AddSeconds(t), CombatEventKind.Damage, "Lord Inquisitor Seru", name, 50 + p, DamageKind.Melee, "hit"));
            }
        }

        tracker.OnCombatEvent(new CombatEvent(Start.AddSeconds(60), CombatEventKind.Death, "Raideraa", "Lord Inquisitor Seru", 0, DamageKind.None, null));
        tracker.EndBatch();
        return tracker.Snapshot.Completed[0];
    }
}
