using EQOverwatch.Core.Settings;
using Xunit;

namespace EQOverwatch.Core.Tests;

public sealed class Release82Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eqo-r82-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Another_editions_settings_folder_is_copied_once_on_first_start()
    {
        string source = Path.Combine(_dir, "EQOverwatch");
        string target = Path.Combine(_dir, "Other");
        Directory.CreateDirectory(Path.Combine(source, "characters"));
        File.WriteAllText(Path.Combine(source, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(source, "characters", "Ream.json"), "[]");

        Assert.True(SettingsFolderImport.CopyIfMissing(source, target));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "characters", "Ream.json")));

        File.WriteAllText(Path.Combine(target, "settings.json"), "{\"changed\":1}");
        Assert.False(SettingsFolderImport.CopyIfMissing(source, target)); // only the first time
        Assert.Equal("{\"changed\":1}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.False(SettingsFolderImport.CopyIfMissing(Path.Combine(_dir, "missing"), Path.Combine(_dir, "Third")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "Third")));
    }

    [Fact]
    public void App_info_comes_from_the_edition()
    {
        Assert.Equal(Edition.Name, AppInfo.Name);
        Assert.Equal(Edition.Repository, AppInfo.Repository);
        Assert.Equal(Edition.SettingsFolderName, AppInfo.SettingsFolderName);
    }
}
