using EQOverwatch.Core.Characters;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class V112Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqo-v112-" + Guid.NewGuid().ToString("N"));

    public V112Tests() => Directory.CreateDirectory(_dir);

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

    private const string Ini =
        "[Defaults]\r\nShowSpellEffects=1\r\n[Socials]\r\nPage1Button1Name=Follow\r\nPage1Button1Color=0\r\nPage1Button2Line1=/stop\r\n" +
        "Page1Button3Color=0\r\nPage1Button5Name=Camp\r\n\r\n[Hotbuttons]\r\nPage1Button1=E631\r\n";

    [Fact]
    public void Socials_go_into_the_first_two_slots_with_no_keys_and_never_touch_others()
    {
        (string? text, SocialsResult result) = OverwatchSocials.Add(Ini, "Porter_frostreaver.ini");

        Assert.Equal(SocialsOutcome.Added, result.Outcome);
        Assert.Equal("page 1 button 4, page 1 button 6", result.Slots);
        Assert.NotNull(text);
        Assert.StartsWith(Ini[..Ini.IndexOf("\r\n\r\n[Hotbuttons]", StringComparison.Ordinal)], text);
        Assert.Contains("Page1Button4Name=Overwatch Data1\r\n", text);
        Assert.Contains("Page1Button4Line5=/outputfile faction\r\n", text);
        Assert.Contains("Page1Button6Name=Overwatch Data2\r\n", text);
        Assert.Contains("Page1Button6Line5=/played\r\n\r\n[Hotbuttons]\r\nPage1Button1=E631\r\n", text);
        Assert.Contains("Page1Button2Line1=/stop", text);
        Assert.Contains("Page1Button3Color=0", text);
    }

    [Fact]
    public void Socials_are_added_once_and_need_two_free_slots()
    {
        (string? text, _) = OverwatchSocials.Add(Ini, "a.ini");
        Assert.Equal(SocialsOutcome.AlreadyThere, OverwatchSocials.Add(text!, "a.ini").Result.Outcome);

        string full = "[Socials]\n" + string.Join("\n", Enumerable.Range(1, 10).SelectMany(p => Enumerable.Range(1, 12).Select(b => $"Page{p}Button{b}Name=x"))
            .Where(l => l != "Page7Button3Name=x")) + "\n";
        (string? none, SocialsResult result) = OverwatchSocials.Add(full, "b.ini");
        Assert.Null(none);
        Assert.Equal(SocialsOutcome.NoRoom, result.Outcome);
    }

    [Fact]
    public void A_file_without_socials_gets_a_section()
    {
        (string? text, SocialsResult result) = OverwatchSocials.Add("[Defaults]\nA=1\n", "c.ini");

        Assert.Equal("page 1 button 1, page 1 button 2", result.Slots);
        Assert.StartsWith("[Defaults]\nA=1\n[Socials]\nPage1Button1Name=Overwatch Data1\n", text);
    }

    [Fact]
    public void Files_are_the_character_file_and_its_class_copies_and_a_backup_is_kept()
    {
        foreach (string name in new[] { "Porter_frostreaver.ini", "Porter_frostreaver_ROG.ini", "Porterii_frostreaver.ini", "UI_Porter_frostreaver.ini", "Porter_vaniki.ini" })
        {
            File.WriteAllText(Path.Combine(_dir, name), Ini);
        }

        IReadOnlyList<string> files = OverwatchSocials.FilesFor(_dir, "Porter", "frostreaver");
        Assert.Equal(new[] { "Porter_frostreaver.ini", "Porter_frostreaver_ROG.ini" }, files.Select(Path.GetFileName));

        IReadOnlyList<SocialsResult> results = OverwatchSocials.AddTo(files);
        Assert.All(results, r => Assert.Equal(SocialsOutcome.Added, r.Outcome));
        Assert.Equal(Ini, File.ReadAllText(files[0] + ".bak"));
        Assert.Contains("Overwatch Data2", File.ReadAllText(files[1]));
        Assert.DoesNotContain("Overwatch", File.ReadAllText(Path.Combine(_dir, "Porter_vaniki.ini")));
    }
}
