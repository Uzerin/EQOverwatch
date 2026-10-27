using EQCompanion.Core.Models;
using Xunit;

namespace EQCompanion.Core.Tests;

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
        Assert.False(config.IsPrimaryDps);
    }

    [Fact]
    public void Only_one_log_can_be_primary_dps()
    {
        var logs = new WatchedLogCollection();
        WatchedLogConfig a = logs.AddFile(Path("Stackprime"));
        WatchedLogConfig b = logs.AddFile(Path("Oodles"));

        a.IsPrimaryDps = true;
        Assert.Same(a, logs.PrimaryDpsLog);

        b.IsPrimaryDps = true;
        Assert.False(a.IsPrimaryDps);
        Assert.True(b.IsPrimaryDps);
        Assert.Same(b, logs.PrimaryDpsLog);
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
    public void Adding_a_config_that_is_already_primary_demotes_the_existing_primary()
    {
        var logs = new WatchedLogCollection();
        WatchedLogConfig a = logs.AddFile(Path("Stackprime"));
        a.IsPrimaryDps = true;

        var b = new WatchedLogConfig(Path("Oodles")) { IsPrimaryDps = true };
        logs.Add(b);

        Assert.False(a.IsPrimaryDps);
        Assert.Same(b, logs.PrimaryDpsLog);
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
