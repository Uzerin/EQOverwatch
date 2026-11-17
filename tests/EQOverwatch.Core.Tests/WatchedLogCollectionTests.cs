using EQOverwatch.Core.Models;
using Xunit;

namespace EQOverwatch.Core.Tests;

public class WatchedLogCollectionTests
{
    private static string Path(string character) => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eqlog_{character}_Frostreaver.txt");

    [Fact]
    public void Config_extracts_character_and_server_from_the_file_name()
    {
        var config = new WatchedLogConfig(Path("Stackprime"));
        Assert.Equal("Stackprime", config.CharacterName);
        Assert.Equal("Frostreaver", config.ServerName);
        Assert.True(config.IsTriggerEnabled);
    }

    [Fact]
    public void Trigger_flag_is_not_exclusive()
    {
        var logs = new WatchedLogCollection();
        WatchedLogConfig a = logs.AddFile(Path("Stackprime"));
        WatchedLogConfig b = logs.AddFile(Path("Oodles"));

        a.IsTriggerEnabled = true;
        b.IsTriggerEnabled = true;

        Assert.True(a.IsTriggerEnabled && b.IsTriggerEnabled);
    }

    [Fact]
    public void Duplicate_paths_are_rejected_or_deduplicated()
    {
        var logs = new WatchedLogCollection();
        WatchedLogConfig a = logs.AddFile(Path("Stackprime"));

        Assert.Same(a, logs.AddFile(Path("Stackprime")));
        Assert.Throws<ArgumentException>(() => logs.Add(new WatchedLogConfig(Path("Stackprime"))));
        Assert.Single(logs);
    }
}
