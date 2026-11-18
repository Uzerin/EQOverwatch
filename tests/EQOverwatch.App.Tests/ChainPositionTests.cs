using Xunit;

namespace EQOverwatch.App.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Changing_the_chain_position_on_the_characters_tab_reaches_the_chain_at_once()
    {
        using var h = new Harness();
        h.Dialogs.LogFiles = new[] { h.CreateLog("Stackprime") };
        h.Vm.AddLogCommand.Execute(null);
        h.Vm.Logs[0].ChChainPosition = 3;
        Assert.Equal(3, h.Pipeline.ChChain.GetPosition("Stackprime"));
        h.Vm.Logs[0].ChChainPosition = 6;
        Assert.Equal(6, h.Pipeline.ChChain.GetPosition("Stackprime"));
    }

    [Fact]
    public void A_restored_chain_position_follows_changes_too()
    {
        using var h = new Harness((s, d) => s.WatchedLogs.Add(new Core.Settings.WatchedLogSettings
        {
            FilePath = System.IO.Path.Combine(d, "eqlog_Stackprime_Frostreaver.txt"), CharacterName = "Stackprime", ChChainPosition = 3,
        }));
        Assert.Equal(3, h.Pipeline.ChChain.GetPosition("Stackprime"));
        h.Vm.Logs[0].ChChainPosition = 6;
        Assert.Equal(6, h.Pipeline.ChChain.GetPosition("Stackprime"));
    }
}
